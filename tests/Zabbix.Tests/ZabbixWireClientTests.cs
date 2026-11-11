// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>
/// The Zabbix protocol's one client: it belongs to one server or proxy, sends values and asks that server, and reads items
/// from the agents each call names. What each request puts on the wire is pinned on what sends it
/// (<see cref="ZabbixSenderTests"/>, <see cref="ZabbixServerWireTests"/>); these pin the client's own promises.
/// </summary>
public sealed class ZabbixWireClientTests
{
    private static readonly ZabbixValue Value = new("web-01", "app.requests", 42L, DateTimeOffset.UnixEpoch);

    private static ZabbixExchangeCallback Answering(string reply, List<JsonElement>? sent = null) => (request, _) =>
    {
        if (sent is not null && request.Span.IndexOf((byte)'{') == 0) sent.Add(JsonDocument.Parse(request).RootElement.Clone());
        return ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(reply));
    };

    private static string Processed(int count) => MockTrapper.Success(count, 0);

    private const string Pong = """{"response":"success","data":{}}""";

    private const string AgentAnswer = """{"version":"7.0.0","variant":1,"data":[{"value":"1"}]}""";

    // ----- which server -------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_client_built_from_a_host_and_port_sends_to_it()
    {
        (int port, Task<int> received) = MockTrapper.Collect(1);
        using var wire = new ZabbixWireClient($"127.0.0.1:{port}");

        ZabbixSenderResponse sent = await wire.SendAsync([Value], TestContext.Current.CancellationToken);

        Assert.Equal(1, await received.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(1, sent.Processed);
    }

    [Fact]
    public async Task The_nodes_of_an_HA_cluster_are_tried_in_order()
    {
        (int port, Task<int> received) = MockTrapper.Collect(1);
        using var wire = new ZabbixWireClient($"127.0.0.1:{FramesTests.ClosedPort()};127.0.0.1:{port}");

        await wire.SendAsync([Value], TestContext.Current.CancellationToken);

        Assert.Equal(1, await received.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("zbx1,zbx2")]        // two destinations are two clients
    [InlineData("zbx1;zbx1")]        // ServerActive refuses a node named twice
    [InlineData("a b")]
    public void A_server_that_names_no_one_server_is_refused_where_it_is_passed(string server)
        => Assert.Equal("server", Assert.ThrowsAny<ArgumentException>(() => new ZabbixWireClient(server)).ParamName);

    [Fact]
    public async Task A_request_to_the_server_with_none_set_says_what_to_set()
    {
        using var wire = new ZabbixWireClient();

        InvalidOperationException sending = await Assert.ThrowsAsync<InvalidOperationException>(() => wire.SendAsync([Value], TestContext.Current.CancellationToken));
        InvalidOperationException asking = await Assert.ThrowsAsync<InvalidOperationException>(() => wire.GetStatusAsync(TestContext.Current.CancellationToken));

        Assert.Contains("WithServer", sending.Message, StringComparison.Ordinal);
        Assert.Equal(sending.Message, asking.Message);
    }

    [Fact]
    public async Task A_client_keeps_its_own_copy_of_the_options_it_was_given()
    {
        (int port, Task<int> received) = MockTrapper.Collect(1);
        var options = new ZabbixWireOptions { Server = $"127.0.0.1:{port}", Connection = { Timeout = TimeSpan.FromSeconds(10) } };
        await using var wire = new ZabbixWireClient(options);
        options.Server = "a b";                                                            // a change after the client was built doesn't reach it
        options.Connection.Timeout = TimeSpan.Zero;

        await wire.SendAsync([Value], TestContext.Current.CancellationToken);

        Assert.Equal(1, await received.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal(($"127.0.0.1:{port}", TimeSpan.FromSeconds(10)), (wire.Options.Server, wire.Options.Connection.Timeout));
        Assert.Null(typeof(ZabbixWireClient).GetProperty(nameof(ZabbixWireClient.Options), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance));
    }

    [Fact]
    public async Task Options_that_cannot_be_used_fail_the_first_request_as_the_client_s_state()
    {
        using var unparsable = new ZabbixWireClient(new ZabbixWireOptions { Server = "a b" });
        using var badConnection = new ZabbixWireClient(new ZabbixWireOptions { Server = "127.0.0.1", Connection = new ZabbixConnectionOptions { Timeout = TimeSpan.Zero } });

        InvalidOperationException server = await Assert.ThrowsAsync<InvalidOperationException>(() => unparsable.PingAsync(TestContext.Current.CancellationToken));
        InvalidOperationException connection = await Assert.ThrowsAsync<InvalidOperationException>(() => badConnection.PingAsync(TestContext.Current.CancellationToken));

        Assert.Contains("a b", server.Message, StringComparison.Ordinal);
        Assert.Contains("Timeout", connection.Message, StringComparison.Ordinal);
        Assert.IsType<OptionsValidationException>(connection.InnerException);
    }

    // ----- what the options carry ---------------------------------------------------------------------------------

    [Fact]
    public async Task The_session_goes_with_the_server_s_requests()
    {
        var sent = new List<JsonElement>();
        using var wire = new ZabbixWireClient(Answering(Pong, sent), new ZabbixWireOptions { SessionId = "the-session" }, null, null);

        await wire.PingAsync(TestContext.Current.CancellationToken);

        Assert.Equal("the-session", Assert.Single(sent).GetProperty("sid").GetString());
    }

    [Fact]
    public void The_builder_s_token_is_the_options_session()
    {
        using ZabbixWireClient wire = ZabbixWireClient.CreateBuilder().WithServer("zbx-server-01").WithToken("the-session").Build();

        Assert.Equal(("zbx-server-01:10051", "the-session"), (wire.Options.Server, wire.Options.SessionId));
    }

    [Fact]
    public async Task The_batch_size_splits_a_send()
    {
        var frames = new List<int>();
        ZabbixExchangeCallback exchange = (request, _) =>
        {
            int values = MockTrapper.Values(request.ToArray());
            frames.Add(values);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(Processed(values)));
        };
        using var wire = new ZabbixWireClient(exchange, new ZabbixWireOptions { BatchSize = 2 }, null, null);

        ZabbixSenderResponse sent = await wire.SendAsync(Enumerable.Repeat(Value, 5).ToArray(), TestContext.Current.CancellationToken);

        Assert.Equal([2, 2, 1], frames);
        Assert.Equal(5, sent.Processed);
    }

    [Fact]
    public async Task The_check_timeout_goes_with_an_agent_read()
    {
        var sent = new List<JsonElement>();
        using var wire = new ZabbixWireClient(Answering(AgentAnswer, sent), new ZabbixWireOptions { CheckTimeout = TimeSpan.FromSeconds(7) }, null, null);

        await wire.GetAgentItemAsync("web-01", "agent.ping", TestContext.Current.CancellationToken);

        Assert.Equal(7, Assert.Single(sent).GetProperty("data")[0].GetProperty("timeout").GetInt32());
    }

    [Theory]
    [InlineData(0, ZabbixWireOptions.DefaultMaxBatchBytes, 3)]
    [InlineData(1, 35, 3)]
    [InlineData(1, ZabbixWireOptions.DefaultMaxBatchBytes + 1, 3)]
    [InlineData(1, ZabbixWireOptions.DefaultMaxBatchBytes, 0)]
    [InlineData(1, ZabbixWireOptions.DefaultMaxBatchBytes, 601)]
    [InlineData(1, ZabbixWireOptions.DefaultMaxBatchBytes, 1.5)]
    public async Task A_setting_outside_what_the_protocol_takes_is_refused_at_the_first_request(int batchSize, long maxBatchBytes, double checkTimeout)
    {
        ZabbixWireOptions options = new() { BatchSize = batchSize, MaxBatchBytes = maxBatchBytes, CheckTimeout = TimeSpan.FromSeconds(checkTimeout) };
        using var sending = new ZabbixWireClient(Answering(Processed(1)), options, null, null);
        using var reading = new ZabbixWireClient(Answering(AgentAnswer), options, null, null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => sending.SendAsync([Value], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reading.GetAgentItemAsync("web-01", "agent.ping", TestContext.Current.CancellationToken));
    }

    // ----- agents -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_client_with_no_server_reads_from_the_agents_each_call_names()
    {
        (int one, Task<string> first) = MockTrapper.Answer(AgentAnswer);
        (int two, Task<string> second) = MockTrapper.Answer(AgentAnswer);
        using var wire = new ZabbixWireClient();

        ZabbixAgentAnswer a = await wire.GetAgentItemAsync($"127.0.0.1:{one}", "agent.ping", TestContext.Current.CancellationToken);
        ZabbixAgentAnswer b = await wire.GetAgentItemAsync($"127.0.0.1:{two}", ZabbixItemKey.Parse("agent.hostname"), TestContext.Current.CancellationToken);

        Assert.Equal(("1", "1"), (a.Value, b.Value));
        Assert.Contains("agent.ping", await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains("agent.hostname", await second.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public void An_agent_listens_on_10050_unless_it_names_another_port()
        => Assert.Equal(10050, ZabbixWireClient.DefaultAgentPort);

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("a b")]
    public async Task An_agent_that_does_not_parse_is_refused_as_the_argument(string agent)
    {
        using var wire = new ZabbixWireClient();

        ArgumentException refused = await Assert.ThrowsAnyAsync<ArgumentException>(() => wire.GetAgentItemAsync(agent, "agent.ping", TestContext.Current.CancellationToken));

        Assert.Equal("agent", refused.ParamName);
    }

    [Fact]
    public async Task What_a_client_learns_about_an_older_agent_stays_with_that_agent()
    {
        var requests = new List<string>();
        ZabbixExchangeCallback exchange = (request, _) =>
        {
            string text = Encoding.UTF8.GetString(request.Span);
            requests.Add(text);
            bool older = requests.Count <= 3;       // the first agent is older than 7.0: it reads JSON as a key
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(older ? "1" : AgentAnswer));
        };
        using var wire = new ZabbixWireClient(exchange);

        await wire.GetAgentItemAsync("old-agent", "agent.ping", TestContext.Current.CancellationToken);   // JSON, then plaintext
        await wire.GetAgentItemAsync("old-agent", "agent.ping", TestContext.Current.CancellationToken);   // plaintext at once
        await wire.GetAgentItemAsync("new-agent", "agent.ping", TestContext.Current.CancellationToken);   // JSON again

        Assert.Equal(4, requests.Count);
        Assert.Equal([true, false, false, true], requests.Select(request => request.StartsWith('{')));
    }

    // ----- redirects ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_send_follows_a_proxy_group_s_redirect_and_a_request_to_the_server_does_not()
    {
        await using var target = new FramesTests.Peer((stream, _, stopping) => ZabbixHeader.WriteAsync(stream, Encoding.UTF8.GetBytes(Processed(1)), stopping));
        byte[] redirect = Encoding.UTF8.GetBytes($$"""{"redirect":{"revision":1,"address":"{{target.Node}}"},"response":"failed"}""");
        await using var moved = new FramesTests.Peer((stream, _, stopping) => ZabbixHeader.WriteAsync(stream, redirect, stopping));
        using var sending = new ZabbixWireClient(moved.Node.ToString());
        using var asking = new ZabbixWireClient(moved.Node.ToString());

        ZabbixSenderResponse sent = await sending.SendAsync([Value], TestContext.Current.CancellationToken);
        Exception? refused = await Record.ExceptionAsync(() => asking.PingAsync(TestContext.Current.CancellationToken));

        Assert.Equal(1, sent.Processed);
        Assert.Equal(1, target.Frames.Reader.Count);       // only the send reached the redirect's target
        Assert.NotNull(refused);
    }

    // ----- lifetime and logging -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_disposed_client_refuses_every_request_naming_itself()
    {
        var wire = new ZabbixWireClient("127.0.0.1:9");
        await wire.DisposeAsync();
        await wire.DisposeAsync();

        ObjectDisposedException sending = await Assert.ThrowsAsync<ObjectDisposedException>(() => wire.SendAsync([Value], TestContext.Current.CancellationToken));
        ObjectDisposedException reading = await Assert.ThrowsAsync<ObjectDisposedException>(() => wire.GetAgentItemAsync("web-01", "agent.ping", TestContext.Current.CancellationToken));

        Assert.Equal((typeof(ZabbixWireClient).FullName, typeof(ZabbixWireClient).FullName), (sending.ObjectName, reading.ObjectName));
    }

    [Fact]
    public async Task A_client_from_the_container_outlives_a_caller_s_dispose_until_the_container_is_disposed()
    {
        (int port, Task<int> received) = MockTrapper.Collect(2);
        var services = new ServiceCollection();
        services.AddZabbixWireClient("prod").WithServer($"127.0.0.1:{port}");
        await using ServiceProvider provider = services.BuildServiceProvider();
        ZabbixWireClient wire = provider.GetRequiredService<IZabbixWireClientFactory>().Create("prod");
        ZabbixWireClient injected = provider.GetRequiredKeyedService<ZabbixWireClient>("prod");
        await using ZabbixWireClient built = ZabbixWireClient.CreateBuilder().WithServer($"127.0.0.1:{port}").Build();

        DisposeAsACallerWould(wire);                                                       // the container's client: left running
        await wire.DisposeAsync();
        await wire.SendAsync([Value], TestContext.Current.CancellationToken);
        await injected.SendAsync([Value], TestContext.Current.CancellationToken);
        Assert.Equal(2, await received.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await provider.DisposeAsync();                                                     // the container releases it
        await built.DisposeAsync();                                                        // a builder's client is its caller's

        Assert.Same(wire, injected);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => wire.SendAsync([Value], TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => built.SendAsync([Value], TestContext.Current.CancellationToken));
    }

    // A caller's using block, written by code that doesn't know the client is the container's.
    private static void DisposeAsACallerWould(IDisposable disposable) => disposable.Dispose();

    [Fact]
    public async Task A_given_exchange_stays_the_caller_s()
    {
        int calls = 0;
        ZabbixExchangeCallback exchange = (_, _) =>
        {
            calls++;
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(Pong));
        };
        var wire = new ZabbixWireClient(exchange);
        await wire.PingAsync(TestContext.Current.CancellationToken);

        await wire.DisposeAsync();
        await exchange(ReadOnlyMemory<byte>.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
        Assert.Empty(wire.Links);
    }

    [Fact]
    public async Task All_of_a_client_s_logs_carry_its_name()
    {
        var logs = new FakeLogCollector();
        using var provider = new FakeLoggerProvider(logs);
        using var loggers = new LoggerFactory([provider]);
        ZabbixExchangeCallback exchange = (request, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(
            request.Span.IndexOf("sender data"u8) >= 0 ? Processed(1) : request.Span.IndexOf("status.get"u8) >= 0 ? Pong : AgentAnswer));
        using var wire = new ZabbixWireClient(exchange, new ZabbixWireOptions(), loggers, null);

        await wire.SendAsync([Value], TestContext.Current.CancellationToken);
        await wire.PingAsync(TestContext.Current.CancellationToken);
        await wire.GetAgentItemAsync("web-01", "agent.ping", TestContext.Current.CancellationToken);

        Assert.NotEmpty(logs.GetSnapshot());
        Assert.All(logs.GetSnapshot(), record => Assert.Equal(typeof(ZabbixWireClient).FullName, record.Category));
    }

    // ----- standing in for it ---------------------------------------------------------------------------------------

    private sealed class Stand : ZabbixWireClient
    {
        public List<string> Calls { get; } = [];

        public override Task<ZabbixSenderResponse> SendAsync(IReadOnlyCollection<ZabbixValue> values, CancellationToken cancellationToken)
        {
            Calls.Add("send");
            return Task.FromResult(new ZabbixSenderResponse(values.Count, 0, values.Count, TimeSpan.Zero, "scripted"));
        }

        public override Task<ZabbixAgentAnswer> GetAgentItemAsync(string agent, ZabbixItemKey key, CancellationToken cancellationToken)
        {
            Calls.Add(agent);
            return Task.FromResult(ZabbixAgentAnswer.Supported(key, "stood in"));
        }

        public override Task PingAsync(CancellationToken cancellationToken = default)
        {
            Calls.Add("ping");
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task A_test_stands_in_for_the_client_by_overriding_the_longest_overloads()
    {
        using var stand = new Stand();

#pragma warning disable xUnit1051 // the overloads without a token are what this pins
        ZabbixSenderResponse sent = await stand.SendAsync([Value]);
        ZabbixAgentAnswer read = await stand.GetAgentItemAsync("web-01", "agent.ping");
#pragma warning restore xUnit1051
        await stand.PingAsync(TestContext.Current.CancellationToken);

        Assert.Equal((1, "stood in"), (sent.Processed, read.Value));
        Assert.Equal(["send", "web-01", "ping"], stand.Calls);
    }

    [Fact]
    public async Task A_connection_refused_is_the_failure_it_always_was()
    {
        using var wire = new ZabbixWireClient($"127.0.0.1:{FramesTests.ClosedPort()}");

        await Assert.ThrowsAsync<SocketException>(() => wire.SendAsync([Value], TestContext.Current.CancellationToken));
    }
}
