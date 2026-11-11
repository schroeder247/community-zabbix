// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Community.Zabbix.Listening;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// What stalled PSK handshakes cost the thread pool. The listener's PSK acceptor pumps BouncyCastle
/// non-blocking over the connection's pipes (<c>ZabbixTlsAcceptor.AcceptPskAsync</c>), and the client's handshake does
/// the same over the connection's stream (<c>ZabbixPskClientStream</c>), so neither holds a pool thread while it waits. Each test stalls <see cref="Peers"/> handshakes at once and measures how long an unrelated
/// <see cref="Task.Run(Action)"/> takes to be scheduled (the pool's responsiveness) and how many pool threads exist.
/// </summary>
[Collection("Adversarial wire stress")]
public sealed class AdversarialPskThreadTests
{
    private const int Peers = 48;
    private const string Key = "1f87b595725ac58dd977beef14b97461a7c1045b9a1c963065002c5473194952";
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(20);

    private static async Task<TimeSpan> ProbeAsync()
    {
        // The global queue, not this thread's local one: a work item from outside, as a timer or a socket completion is.
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        long started = TimeProvider.System.GetTimestamp();
        ThreadPool.UnsafeQueueUserWorkItem(static done => done.TrySetResult(), ran, preferLocal: false);
        await ran.Task.WaitAsync(Failsafe, TimeProvider.System, TestContext.Current.CancellationToken);
        return TimeProvider.System.GetElapsedTime(started);
    }

    [Fact]
    public async Task Stalled_psk_hellos_against_the_listener_hold_no_pool_thread_and_the_deadline_frees_them()
    {
        var time = new FakeTimeProvider();
        var options = new ZabbixListenerOptions { Address = "127.0.0.1", Port = 0, Timeout = TimeSpan.FromSeconds(5), TlsAccept = ZabbixTlsAcceptModes.Psk, MaxConnectionsPerPeer = 1024 };
        options.AllowedPeers.Add("127.0.0.1");
        options.PreSharedKeys["edge-01"] = Key;
        var listener = new ZabbixListener(options, static (request, _) => ValueTask.FromResult(request.Payload), ListenerTests.Transport(), loggers: null, time, meters: null);
        await listener.StartingAsync(TestContext.Current.CancellationToken);
        await listener.StartAsync(TestContext.Current.CancellationToken);
        var clients = new List<TcpClient>();
        int threadsBefore = ThreadPool.ThreadCount;
        TimeSpan probe;
        int threadsDuring;
        long pending;
        try
        {
            for (int i = 0; i < Peers; i++)
            {
                var client = new TcpClient();
                clients.Add(client);
                await client.ConnectAsync((IPEndPoint)listener.LocalEndPoint!, TestContext.Current.CancellationToken);
                await client.GetStream().WriteAsync(new byte[] { 0x16, 0x03, 0x01, 0x00, 0xC8, 0x01, 0x00 }, TestContext.Current.CancellationToken);   // half a ClientHello
            }
            using (var failsafe = new CancellationTokenSource(Failsafe, TimeProvider.System))
                while (listener.InFlight != Peers) { failsafe.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
            probe = await ProbeAsync();
            threadsDuring = ThreadPool.ThreadCount;
            pending = ThreadPool.PendingWorkItemCount;

            time.Advance(TimeSpan.FromSeconds(5));
            using var drained = new CancellationTokenSource(Failsafe, TimeProvider.System);
            while (listener.InFlight != 0) { drained.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
        }
        finally
        {
            foreach (TcpClient client in clients) client.Dispose();
            await listener.StoppingAsync(TestContext.Current.CancellationToken);
            await listener.StopAsync(TestContext.Current.CancellationToken);
            await listener.StoppedAsync(TestContext.Current.CancellationToken);
            await listener.DisposeAsync();
        }

        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"listener: {Peers} stalled, probe {probe.TotalMilliseconds:F1} ms, pool threads {threadsBefore} → {threadsDuring}, pending {pending}"));
        Assert.True(probe < TimeSpan.FromSeconds(1), $"a Task.Run took {probe.TotalMilliseconds:F0} ms with {Peers} stalled PSK hellos");
        Assert.True(threadsDuring - threadsBefore < Peers / 2, $"the pool grew from {threadsBefore} to {threadsDuring} threads");
    }

    // A client handshake to a PSK server that stalls must not block a pool thread: BouncyCastle's synchronous Connect would
    // hold one until the deadline closed the socket (48 stalled clients on 12 cores: 13-14 pool threads, 36-41 handshakes
    // queued). The handshake is non-blocking; the deadline frees every one of the stalled clients, on the injected clock,
    // as TimeoutException.
    [Fact]
    public async Task Psk_clients_stalled_by_their_server_are_all_freed_by_the_deadline()
    {
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var held = new List<TcpClient>();
        using var stop = new CancellationTokenSource();
        Task accepting = Task.Run(async () =>
        {
            try
            {
                while (true) { TcpClient accepted = await server.AcceptTcpClientAsync(stop.Token); lock (held) held.Add(accepted); }   // never answers
            }
            catch (OperationCanceledException) { }
        }, TestContext.Current.CancellationToken);
        var time = new FakeTimeProvider();
        var node = new ZabbixServerNode("127.0.0.1", ((IPEndPoint)server.LocalEndpoint).Port);
        var words = new ZabbixConnectionOptions { Timeout = TimeSpan.FromSeconds(5), Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = "edge-01", Psk = Key } };
        int threadsBefore = ThreadPool.ThreadCount;
        List<Task<Exception?>> exchanges = [.. Enumerable.Range(0, Peers).Select(_ => Record.ExceptionAsync(async () => await ZabbixFrames.Connect(node, words, time)("x"u8.ToArray(), TestContext.Current.CancellationToken)).AsTask())];
        using (var failsafe = new CancellationTokenSource(Failsafe, TimeProvider.System))
            while (true) { lock (held) if (held.Count == Peers) break; failsafe.Token.ThrowIfCancellationRequested(); await Task.Yield(); }
        long pending = ThreadPool.PendingWorkItemCount;
        TimeSpan probe = await ProbeAsync();
        int threadsDuring = ThreadPool.ThreadCount;

        time.Advance(TimeSpan.FromSeconds(5));
        Exception?[] failures = await Task.WhenAll(exchanges).WaitAsync(Failsafe, TimeProvider.System, TestContext.Current.CancellationToken);
        await stop.CancelAsync();
        await accepting;
        lock (held) foreach (TcpClient client in held) client.Dispose();

        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"client: {Peers} stalled, probe {probe.TotalMilliseconds:F1} ms, pool threads {threadsBefore} → {threadsDuring}, pending {pending}"));
        Assert.All(failures, failure => Assert.IsType<TimeoutException>(failure));
    }

    /// <summary>
    /// Stream's contract: <see cref="Stream.Flush"/> on a stream that buffers nothing returns. Both PSK streams hand every
    /// write's records to their transport before the write returns, so a synchronous flush (a <see cref="StreamWriter"/>
    /// over one, a caller's own <c>Flush</c>) has nothing to do and must not throw.
    /// </summary>
    [Fact]
    public void A_synchronous_flush_of_either_psk_stream_returns()
    {
        var pipe = new System.IO.Pipelines.Pipe();
        // Neither is disposed: a protocol that never handshook cannot send close_notify (BouncyCastle throws), and
        // neither stream holds anything but the protocol and the transport, both collected with it.
#pragma warning disable CA2000
        var server = new ZabbixPskStream(new Org.BouncyCastle.Tls.TlsServerProtocol(), new Duplex(pipe.Reader, pipe.Writer));
        var client = (Stream)Activator.CreateInstance(typeof(ZabbixPskClientStream), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance, null,
            [new Org.BouncyCastle.Tls.TlsClientProtocol(), Stream.Null], CultureInfo.InvariantCulture)!;
#pragma warning restore CA2000

        server.Flush();
        client.Flush();
    }

    private sealed record Duplex(System.IO.Pipelines.PipeReader Input, System.IO.Pipelines.PipeWriter Output) : System.IO.Pipelines.IDuplexPipe;
}
