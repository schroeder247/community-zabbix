// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Buffers;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Community.Zabbix.Listening;
using Community.Zabbix.Protocol;
using Community.Zabbix.Proxy;
using Community.Zabbix.Storage;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// Adversarial tests of the listener's and the proxy's bounds: an accept that fails, a flood from one address
/// against a name-based peer list, an agent that sends a new session token per frame, a task result the tasks store
/// will not keep, and the words a refusal is reported in.
/// </summary>
[Collection("Adversarial wire stress")]
public sealed class AdversarialProxyHardeningTests
{
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(10);

    /// <summary>A transport whose listener fails its first accepts, then reports itself unbound.</summary>
    private sealed class FailingTransport(int failures) : IConnectionListenerFactory, IConnectionListener
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public EndPoint EndPoint { get; } = new IPEndPoint(IPAddress.Loopback, 10051);

        public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default) => ValueTask.FromResult<IConnectionListener>(this);

        public ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default)
            => Interlocked.Increment(ref _calls) <= failures
                ? ValueTask.FromException<ConnectionContext?>(new SocketException((int)SocketError.TooManyOpenSockets))
                : ValueTask.FromResult<ConnectionContext?>(null);

        public ValueTask UnbindAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task An_accept_that_fails_is_logged_and_the_listener_goes_on_accepting()
    {
        var logs = new FakeLogCollector();
        await using var transport = new FailingTransport(failures: 2);
        var options = new ZabbixListenerOptions { Address = "127.0.0.1", Port = 0 };
        options.AllowedPeers.Add("127.0.0.1");
        await using var listener = new ZabbixListener(options, (_, _) => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty), transport, time: null, logger: new FakeLogger<ZabbixListener>(logs), meters: null);
        await listener.StartingAsync(TestContext.Current.CancellationToken);

        await listener.StartAsync(TestContext.Current.CancellationToken);
        await listener.StoppingAsync(TestContext.Current.CancellationToken).WaitAsync(Failsafe, TimeProvider.System, TestContext.Current.CancellationToken);

        Assert.Equal(3, transport.Calls);                                                    // two failures, then the unbind that ends the loop
        Assert.Equal(2, logs.GetSnapshot().Count(record => record.Id.Id == 6008 && record.Level == LogLevel.Error));
    }

    [Fact]
    public async Task A_flood_from_one_address_past_its_limit_is_refused_before_the_peer_list_s_names_are_resolved()
    {
        int lookups = 0;
        var holding = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new ZabbixListenerOptions { Address = "127.0.0.1", Port = 0, MaxConnectionsPerPeer = 1, Timeout = TimeSpan.FromSeconds(20) };
        options.AllowedPeers.Add("collector.example");
        var listener = new ZabbixListener(options, async (_, cancellationToken) =>
        {
            entered.TrySetResult();
            await holding.Task.WaitAsync(cancellationToken);
            return "{\"response\":\"success\"}"u8.ToArray();
        }, ListenerTests.Transport())
        {
            Resolver = (_, _) =>
            {
                Interlocked.Increment(ref lookups);
                return Task.FromResult(new[] { IPAddress.Loopback });
            },
        };
        await listener.StartingAsync(TestContext.Current.CancellationToken);
        await listener.StartAsync(TestContext.Current.CancellationToken);
        var endPoint = (IPEndPoint)listener.LocalEndPoint!;
        var clients = new List<TcpClient>();
        try
        {
            var first = new TcpClient();
            clients.Add(first);
            await first.ConnectAsync(endPoint, TestContext.Current.CancellationToken);
            var frame = new ArrayBufferWriter<byte>();
            ZabbixHeader.Write(frame, "{\"request\":\"x\"}"u8);
            await first.GetStream().WriteAsync(frame.WrittenMemory, TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(Failsafe, TimeProvider.System, TestContext.Current.CancellationToken);   // the one slot is held

            for (int i = 0; i < 20; i++)
            {
                var flood = new TcpClient();
                clients.Add(flood);
                await flood.ConnectAsync(endPoint, TestContext.Current.CancellationToken);
                var sink = new byte[1];
                try { _ = await flood.GetStream().ReadAsync(sink, TestContext.Current.CancellationToken).AsTask().WaitAsync(Failsafe, TimeProvider.System, TestContext.Current.CancellationToken); }
                catch (IOException) { /* reset: refused as well */ }
            }

            Assert.Equal(1, Volatile.Read(ref lookups));
        }
        finally
        {
            holding.TrySetResult();
            foreach (TcpClient client in clients) client.Dispose();
            await listener.StoppingAsync(TestContext.Current.CancellationToken);
            await listener.StopAsync(TestContext.Current.CancellationToken);
            await listener.StoppedAsync(TestContext.Current.CancellationToken);
            await listener.DisposeAsync();
        }
    }

    [Fact]
    public void A_session_table_full_of_one_frame_tokens_keeps_the_sessions_it_holds_and_opens_no_more()
    {
        var sessions = new ZabbixAgentSessions(TimeProvider.System);
        ZabbixAgentSession? kept = sessions.Get(10, "kept-session-token-0000000000000");
        for (int i = 1; i < ZabbixAgentSessions.MaxSessions; i++) sessions.Get(10, i.ToString("D32", System.Globalization.CultureInfo.InvariantCulture));

        ZabbixAgentSession? overflow = sessions.Get(10, "one-more-token-00000000000000000");

        Assert.Null(overflow);
        Assert.Same(kept, sessions.Get(10, "kept-session-token-0000000000000"));
        Assert.Equal(ZabbixAgentSessions.MaxSessions, sessions.Count);
    }

    [Fact]
    public async Task A_task_result_the_tasks_store_will_not_keep_is_logged_not_lost_silently()
    {
        var logs = new FakeLogCollector();
        await using var tasks = new ZabbixMemoryStore<ZabbixProxyTaskResult>();
        tasks.Complete();
        ZabbixProxyResponse response = JsonSerializer.Deserialize("""{"response":"success","tasks":[{"type":2,"clock":1,"ttl":0,"command":"echo"}]}""", ZabbixProxyJson.Default.ZabbixProxyResponse)!;

        await ZabbixProxyPaging.HandleTasksAsync(response, (task, _) => ValueTask.FromResult(new ZabbixProxyTaskResult(ZabbixProxyTaskType.RemoteCommandResult, task.Clock, task.Ttl, Status: 0, "ok", 0UL)),
            tasks, new FakeLogger(logs), "proxy-01", TestContext.Current.CancellationToken);

        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 7021 && record.Level == LogLevel.Warning);
    }

    [Theory]
    [InlineData("cannot process proxy data", "The server refused proxy data: cannot process proxy data.")]
    [InlineData("proxy \"p\" not found.", "The server refused proxy data: proxy \"p\" not found.")]
    [InlineData(null, "The server refused proxy data: no reason given.")]
    public void A_refusal_is_reported_as_one_sentence(string? info, string message)
        => Assert.Equal(message, new ZabbixProxyRefusedException("proxy data", info, null).Message);
}
