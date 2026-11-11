// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Community.Zabbix.Listening;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>The collection every wire test that holds sockets or floods a listener runs in, one at a time.</summary>
[CollectionDefinition("Adversarial wire stress", DisableParallelization = true)]
public sealed class AdversarialWireStress;

/// <summary>
/// <see cref="ZabbixListener"/> against peers that misbehave — slow-loris, floods past both caps, a
/// half-close, a peer that never reads, and handlers that throw, cancel themselves or never finish. The deadline runs on
/// a <see cref="FakeTimeProvider"/>; every wait is on an observable (a handler's signal, the rejected-connections
/// instrument, <see cref="ZabbixListener.InFlight"/>, the peer's socket closing), with a failsafe bound only.
/// </summary>
[Collection("Adversarial wire stress")]
public sealed class AdversarialListenerTests
{
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(10);

    private static async Task<(ZabbixListener Listener, IPEndPoint EndPoint)> StartAsync(ZabbixRequestCallback handler, TimeProvider time, Action<ZabbixListenerOptions>? configure = null, ILogger<ZabbixListener>? logger = null, IMeterFactory? meters = null)
    {
        var options = new ZabbixListenerOptions { Address = "127.0.0.1", Port = 0, Timeout = TimeSpan.FromSeconds(5) };
        options.AllowedPeers.Add("127.0.0.1");
        configure?.Invoke(options);
        using ILoggerFactory? loggers = SingleLoggerFactory.Of(logger);
        var listener = new ZabbixListener(options, handler, ListenerTests.Transport(), loggers, time, meters);
        await listener.StartingAsync(TestContext.Current.CancellationToken);
        await listener.StartAsync(TestContext.Current.CancellationToken);
        return (listener, (IPEndPoint)listener.LocalEndPoint!);
    }

    private static async Task StopAsync(ZabbixListener listener)
    {
        await listener.StoppingAsync(TestContext.Current.CancellationToken);
        using var patience = new CancellationTokenSource(Failsafe, TimeProvider.System);
        await listener.StopAsync(patience.Token);
        await listener.StoppedAsync(TestContext.Current.CancellationToken);
        await listener.DisposeAsync();
    }

    private static async Task<TcpClient> ConnectAsync(IPEndPoint endPoint)
    {
        var client = new TcpClient();
        await client.ConnectAsync(endPoint, TestContext.Current.CancellationToken);
        return client;
    }

    private static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        var buffer = new ArrayBufferWriter<byte>();
        ZabbixHeader.Write(buffer, payload);
        return buffer.WrittenSpan.ToArray();
    }

    // The peer's view of the end: the listener closed (0 bytes) or reset the connection. Anything it sent first is returned.
    private static async Task<byte[]> ReadToCloseAsync(NetworkStream stream)
    {
        using var received = new MemoryStream();
        var chunk = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk, TestContext.Current.CancellationToken).AsTask().WaitAsync(Failsafe, TimeProvider.System, TestContext.Current.CancellationToken)) > 0)
                received.Write(chunk.AsSpan(0, read));
        }
        catch (IOException) { /* reset: closed as well */ }
        return received.ToArray();
    }

    private static async Task InFlightAsync(ZabbixListener listener, int count)
    {
        using var failsafe = new CancellationTokenSource(Failsafe, TimeProvider.System);
        while (listener.InFlight != count)
        {
            failsafe.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    private static ServiceProvider Metrics() => new ServiceCollection().AddMetrics().BuildServiceProvider();

    [Fact]
    public async Task A_peer_that_sends_one_byte_and_stalls_is_cut_when_the_fake_clock_passes_the_deadline()
    {
        var time = new FakeTimeProvider();
        var logs = new FakeLogCollector();
        (ZabbixListener listener, IPEndPoint endPoint) = await StartAsync((_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>("never"u8.ToArray()), time, logger: new FakeLogger<ZabbixListener>(logs));
        byte[] reply;
        try
        {
            using TcpClient client = await ConnectAsync(endPoint);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync("Z"u8.ToArray(), TestContext.Current.CancellationToken);
            await InFlightAsync(listener, 1);                                                     // from here its deadline runs on the fake clock

            time.Advance(TimeSpan.FromSeconds(5));
            reply = await ReadToCloseAsync(stream);
            await InFlightAsync(listener, 0);
        }
        finally { await StopAsync(listener); }

        Assert.Empty(reply);
        Assert.Contains(logs.GetSnapshot(), record => ListenerRefusals.Is(record, ZabbixListenerRejection.Timeout));
    }

    [Fact]
    public async Task A_peer_that_sends_a_header_that_declares_the_cap_and_stalls_costs_what_it_sent_and_is_cut_at_the_deadline()
    {
        var time = new FakeTimeProvider();
        (ZabbixListener listener, IPEndPoint endPoint) = await StartAsync((_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>("never"u8.ToArray()), time);
        byte[] reply;
        try
        {
            using TcpClient client = await ConnectAsync(endPoint);
            NetworkStream stream = client.GetStream();
            byte[] header = Frame(default);
            header[5] = 0x00; header[6] = 0x00; header[7] = 0x00; header[8] = 0x01;                // declares exactly 16 MiB, the default cap
            await stream.WriteAsync(header, TestContext.Current.CancellationToken);
            await stream.WriteAsync(new byte[1000], TestContext.Current.CancellationToken);
            await InFlightAsync(listener, 1);

            time.Advance(TimeSpan.FromSeconds(5));
            reply = await ReadToCloseAsync(stream);
            await InFlightAsync(listener, 0);
        }
        finally { await StopAsync(listener); }

        Assert.Empty(reply);
    }

    [Fact]
    public async Task A_flood_past_the_global_cap_is_refused_at_once_beyond_the_queue_and_never_handled_unbounded()
    {
        const int Peers = 40;
        const int Cap = 4;                                                                        // Cap serving + Cap queued
        var time = new FakeTimeProvider();
        using ServiceProvider metrics = Metrics();
        using var rejected = new MetricCollector<long>(metrics.GetRequiredService<IMeterFactory>(), ZabbixListenerTelemetry.Name, ZabbixListenerTelemetry.RejectedConnections);
        int handled = 0;
        (ZabbixListener listener, IPEndPoint endPoint) = await StartAsync(async (_, token) =>
        {
            Interlocked.Increment(ref handled);
            await new TaskCompletionSource().Task.WaitAsync(token);
            return default;
        }, time, o => { o.MaxConcurrentConnections = Cap; o.MaxConnectionsPerPeer = 65536; }, meters: metrics.GetRequiredService<IMeterFactory>());
        var clients = new List<TcpClient>();
        try
        {
            for (int i = 0; i < Peers; i++)
            {
                TcpClient client = await ConnectAsync(endPoint);
                clients.Add(client);
                await client.GetStream().WriteAsync(Frame("{}"u8), TestContext.Current.CancellationToken);
            }
            await rejected.WaitForMeasurementsAsync(Peers - (2 * Cap), Failsafe);
            await InFlightAsync(listener, 2 * Cap);

            Assert.Equal(Cap, Volatile.Read(ref handled));
            Assert.All(rejected.GetMeasurementSnapshot(), refusal => Assert.Equal("listener_full", refusal.Tags[ZabbixListenerTelemetry.RejectionTag]));

            time.Advance(TimeSpan.FromSeconds(5));                                                  // every held connection's deadline
            await InFlightAsync(listener, 0);
        }
        finally
        {
            foreach (TcpClient client in clients) client.Dispose();
            await StopAsync(listener);
        }

        Assert.Equal(Peers - (2 * Cap), rejected.GetMeasurementSnapshot().Count);
    }

    // Upstream reads one frame and writes the reply whatever the peer did with its sending half (comms.c zbx_tcp_recv then
    // zbx_tcp_send; a FIN after the request is not a close). The socket transport fires Kestrel's
    // `connection.ConnectionClosed` when the receive side ends, so linked into the deadline after the request is read, it
    // would cancel the reply of a peer that half-closes after its request (nc -N, a script's shutdown(SHUT_WR)): 0 bytes
    // back, the connection closed.
    [Fact]
    public async Task A_peer_that_half_closes_after_its_request_still_gets_the_reply()
    {
        var time = new FakeTimeProvider();
        (ZabbixListener listener, IPEndPoint endPoint) = await StartAsync((request, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes("echo:" + Encoding.UTF8.GetString(request.Payload.Span))), time);
        byte[] reply;
        try
        {
            using TcpClient client = await ConnectAsync(endPoint);
            NetworkStream stream = client.GetStream();
            await stream.WriteAsync(Frame("ping"u8), TestContext.Current.CancellationToken);
            client.Client.Shutdown(SocketShutdown.Send);                                           // what `nc -N` and some senders do
            reply = await ReadToCloseAsync(stream);
        }
        finally { await StopAsync(listener); }

        Assert.Equal(Frame("echo:ping"u8), reply);
    }

    // When the deadline cancels the reply's FlushAsync (6003 "timed out" is logged), the connection is aborted
    // (`connection.Abort`, Kestrel's documented way to drop a connection without draining). Disposing it instead waits
    // for Kestrel's socket connection to drain its output, which a peer that never reads never lets happen: InFlight
    // would stay 1 until the peer itself closes, and StopAsync would wait on it for the host's whole shutdown timeout.
    [Fact]
    public async Task A_peer_that_never_reads_a_large_reply_is_cut_at_the_deadline_and_does_not_hold_the_listener()
    {
        var time = new FakeTimeProvider();
        var logs = new FakeLogCollector();
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] large = new byte[40 * 1024 * 1024];                                                 // past loopback's socket buffers
        (ZabbixListener listener, IPEndPoint endPoint) = await StartAsync((_, _) =>
        {
            answered.TrySetResult();
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(large);
        }, time, logger: new FakeLogger<ZabbixListener>(logs));
        try
        {
            using TcpClient client = await ConnectAsync(endPoint);
            client.ReceiveBufferSize = 4096;
            await client.GetStream().WriteAsync(Frame("{}"u8), TestContext.Current.CancellationToken);
            await answered.Task.WaitAsync(Failsafe, TimeProvider.System, TestContext.Current.CancellationToken);

            time.Advance(TimeSpan.FromSeconds(5));
            await LoggedAsync(logs, record => ListenerRefusals.Is(record, ZabbixListenerRejection.Timeout));   // the write gave up at the deadline
            await InFlightAsync(listener, 0);                                                      // and the connection is gone
        }
        finally { await StopAsync(listener); }
    }

    private static async Task LoggedAsync(FakeLogCollector logs, Func<FakeLogRecord, bool> match)
    {
        using var failsafe = new CancellationTokenSource(Failsafe, TimeProvider.System);
        while (!logs.GetSnapshot().Any(match))
        {
            failsafe.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }

    [Fact]
    public async Task A_handler_that_throws_after_the_peer_left_does_not_fault_the_listener()
    {
        var time = new FakeTimeProvider();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (ZabbixListener listener, IPEndPoint endPoint) = await StartAsync(async (_, _) =>
        {
            entered.TrySetResult();
            await gate.Task;
            throw new InvalidOperationException("late fault");
        }, time);
        ReadOnlyMemory<byte> next;
        try
        {
            using (TcpClient client = await ConnectAsync(endPoint))
            {
                await client.GetStream().WriteAsync(Frame("{}"u8), TestContext.Current.CancellationToken);
                await entered.Task.WaitAsync(Failsafe, TimeProvider.System, TestContext.Current.CancellationToken);
                client.Client.LingerState = new LingerOption(true, 0);                             // leave with a reset
            }
            gate.SetResult();
            await InFlightAsync(listener, 0);
            next = await ZabbixFrames.Connect(new ZabbixServerNode("127.0.0.1", endPoint.Port))("{}"u8.ToArray(), TestContext.Current.CancellationToken);
        }
        finally { await StopAsync(listener); }

        using JsonDocument failed = JsonDocument.Parse(next);
        Assert.Equal("failed", failed.RootElement.GetProperty("response").GetString());          // the listener still answers
    }

    // The fault contract ("the peer is told, in the protocol's own words") is keyed on who cancelled, not on the
    // exception type: a handler's own OperationCanceledException (a TaskCanceledException from a downstream HttpClient
    // timeout, a store's own deadline) is a fault. Treated as the listener's own stop, it would close the connection
    // without a word and record it as Unset, not Error: the peer's exchange would fail with
    // InvalidDataException("the connection ended before a whole frame arrived") and no 6005 would be logged.
    [Fact]
    public async Task A_handler_that_cancels_itself_answers_the_peer_failed_like_any_other_fault()
    {
        var time = new FakeTimeProvider();
        var logs = new FakeLogCollector();
        (ZabbixListener listener, IPEndPoint endPoint) = await StartAsync((_, _) => throw new TaskCanceledException("the downstream call timed out"), time, logger: new FakeLogger<ZabbixListener>(logs));
        ReadOnlyMemory<byte> reply;
        try
        {
            reply = await ZabbixFrames.Connect(new ZabbixServerNode("127.0.0.1", endPoint.Port))("{}"u8.ToArray(), TestContext.Current.CancellationToken);
        }
        finally { await StopAsync(listener); }

        using JsonDocument answer = JsonDocument.Parse(reply);
        Assert.Equal("failed", answer.RootElement.GetProperty("response").GetString());
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 6005);
    }

    // The deadline is enforced on a handler that does not observe its token: the connection is released at the deadline
    // while the handler keeps running. Awaited unbounded, a handler that ignores its token would keep the peer's socket,
    // a global slot and a per-peer slot for as long as it runs, past the deadline and past StopAsync.
    [Fact]
    public async Task A_handler_that_ignores_its_token_still_loses_the_connection_at_the_deadline()
    {
        var time = new FakeTimeProvider();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (ZabbixListener listener, IPEndPoint endPoint) = await StartAsync(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;                                                                    // deaf to its token
            return "late"u8.ToArray();
        }, time);
        Exception? closed;
        try
        {
            using TcpClient client = await ConnectAsync(endPoint);
            await client.GetStream().WriteAsync(Frame("{}"u8), TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(Failsafe, TimeProvider.System, TestContext.Current.CancellationToken);

            time.Advance(TimeSpan.FromSeconds(5));
            closed = await Record.ExceptionAsync(() => ReadToCloseAsync(client.GetStream()));
        }
        finally
        {
            release.TrySetResult();
            await StopAsync(listener);
        }

        Assert.Null(closed);                                                                       // a TimeoutException here is the failsafe
    }

    [Fact]
    public async Task Stopping_with_the_host_s_patience_spent_cancels_the_handlers_in_flight_and_closes_their_peers()
    {
        var time = new FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (ZabbixListener listener, IPEndPoint endPoint) = await StartAsync(async (_, token) =>
        {
            entered.TrySetResult();
            try { await new TaskCompletionSource().Task.WaitAsync(token); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
            return default;
        }, time);
        using TcpClient client = await ConnectAsync(endPoint);
        await client.GetStream().WriteAsync(Frame("{}"u8), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(Failsafe, TimeProvider.System, TestContext.Current.CancellationToken);

        await listener.StoppingAsync(TestContext.Current.CancellationToken);
        await listener.StopAsync(new CancellationToken(canceled: true));                          // the host gave up waiting
        await cancelled.Task.WaitAsync(Failsafe, TimeProvider.System, TestContext.Current.CancellationToken);
        byte[] reply = await ReadToCloseAsync(client.GetStream());
        await InFlightAsync(listener, 0);
        await listener.StoppedAsync(TestContext.Current.CancellationToken);
        await listener.DisposeAsync();

        Assert.Empty(reply);
    }

    [Fact]
    public async Task Stopping_with_patience_left_lets_the_connection_in_flight_finish_its_reply()
    {
        var time = new FakeTimeProvider();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (ZabbixListener listener, IPEndPoint endPoint) = await StartAsync(async (_, token) =>
        {
            entered.TrySetResult();
            await gate.Task.WaitAsync(token);
            return "done"u8.ToArray();
        }, time);
        using TcpClient client = await ConnectAsync(endPoint);
        await client.GetStream().WriteAsync(Frame("{}"u8), TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(Failsafe, TimeProvider.System, TestContext.Current.CancellationToken);

        await listener.StoppingAsync(TestContext.Current.CancellationToken);
        Task stopping = listener.StopAsync(TestContext.Current.CancellationToken);
        Assert.False(stopping.IsCompleted);                                                        // it waits for the one in flight
        gate.SetResult();
        await stopping.WaitAsync(Failsafe, TimeProvider.System, TestContext.Current.CancellationToken);
        byte[] reply = await ReadToCloseAsync(client.GetStream());
        await listener.StoppedAsync(TestContext.Current.CancellationToken);
        await listener.DisposeAsync();

        Assert.Equal(Frame("done"u8), reply);
    }

    [Fact]
    public async Task A_peer_that_sends_garbage_instead_of_a_frame_is_closed_without_a_reply_and_the_handler_never_runs()
    {
        var time = new FakeTimeProvider();
        int handled = 0;
        (ZabbixListener listener, IPEndPoint endPoint) = await StartAsync((_, _) =>
        {
            Interlocked.Increment(ref handled);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>("x"u8.ToArray());
        }, time);
        byte[] reply;
        try
        {
            using TcpClient client = await ConnectAsync(endPoint);
            await client.GetStream().WriteAsync("GET / HTTP/1.1\r\nHost: x\r\n\r\n"u8.ToArray(), TestContext.Current.CancellationToken);
            reply = await ReadToCloseAsync(client.GetStream());
        }
        finally { await StopAsync(listener); }

        Assert.Empty(reply);
        Assert.Equal(0, handled);
    }

    [Fact]
    public async Task A_frame_past_the_listener_s_cap_is_refused_from_its_header_before_the_body_arrives()
    {
        var time = new FakeTimeProvider();
        int handled = 0;
        (ZabbixListener listener, IPEndPoint endPoint) = await StartAsync((_, _) =>
        {
            Interlocked.Increment(ref handled);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>("x"u8.ToArray());
        }, time, o => o.MaxPayloadBytes = 1024);
        byte[] reply;
        try
        {
            using TcpClient client = await ConnectAsync(endPoint);
            byte[] header = Frame(default);
            header[5] = 0x01; header[6] = 0x04;                                                      // 1025 declared, nothing delivered
            await client.GetStream().WriteAsync(header, TestContext.Current.CancellationToken);
            reply = await ReadToCloseAsync(client.GetStream());                                     // no clock advance: refused at once
        }
        finally { await StopAsync(listener); }

        Assert.Empty(reply);
        Assert.Equal(0, handled);
    }
}
