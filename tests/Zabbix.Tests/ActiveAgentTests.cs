// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Community.Zabbix.Hosting;
using Community.Zabbix.Listening;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The active side: the server says which keys, each is collected when its delay says (flexible and scheduling intervals
/// included), values go as agent data by item id with their state and a stable id, a failed or paused upload keeps the
/// batch, and every exchange leaves a span. The agent runs on a fake clock; the test moves it only once the agent is
/// waiting on it (<see cref="WatchedClock"/>), and waits for frames, not for time.
/// </summary>
public sealed class ActiveAgentTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(15);

    private const string Items = """{"response":"success","data":[{"itemid":"100","key":"agent.ping","delay":"1s"},{"itemid":"101","key":"custom.fast","delay":"1s","timeout":"2"},{"itemid":"102","key":"nope","delay":"1s"},{"itemid":"103","key":"agent.ping[x]","delay":"1w"}],"config_revision":"7"}""";
    private const string Success = """{"response":"success","info":"processed: 1; failed: 0; total: 1; seconds spent: 0.000100"}""";

    /// <summary>A fake server: answers each request kind from a script (by how many of that kind it has seen) and hands every frame to the test.</summary>
    private sealed class FakeServer : IAsyncDisposable
    {
        private readonly ZabbixListener _listener;
        private readonly Func<string, int, string> _script;
        private readonly ConcurrentDictionary<string, int> _seen = new(StringComparer.Ordinal);
        private readonly ConcurrentDictionary<string, Channel<JsonElement>> _frames = new(StringComparer.Ordinal);
        private readonly List<string> _order = [];

        private FakeServer(Func<string, int, string> script)
        {
            _script = script;
            _listener = new ZabbixListener(new ZabbixListenerOptions { Address = "127.0.0.1", Port = 0, AllowedPeers = { "127.0.0.1" } }, HandleAsync, new SocketTransportFactory(Options.Create(new SocketTransportOptions()), NullLoggerFactory.Instance));
        }

        public static async Task<FakeServer> StartAsync(Func<string, int, string> script)
        {
            var server = new FakeServer(script);
            await server._listener.StartingAsync(default);
            await server._listener.StartAsync(default);
            return server;
        }

        public ZabbixServerNode Node => new(((IPEndPoint)_listener.LocalEndPoint!).Address.ToString(), ((IPEndPoint)_listener.LocalEndPoint!).Port);

        /// <summary>The request kinds in arrival order.</summary>
        public IReadOnlyList<string> Order
        {
            get { lock (_order) return [.. _order]; }
        }

        /// <summary>The next frame of <paramref name="request"/> the server receives (or has received and not handed out).</summary>
        public async Task<JsonElement> NextAsync(string request) => await Frames(request).Reader.ReadAsync();

        private Channel<JsonElement> Frames(string request) => _frames.GetOrAdd(request, _ => Channel.CreateUnbounded<JsonElement>());

        private ValueTask<ReadOnlyMemory<byte>> HandleAsync(ZabbixRequest request, CancellationToken cancellationToken)
        {
            using JsonDocument frame = JsonDocument.Parse(request.Payload);
            string kind = frame.RootElement.GetProperty("request").GetString()!;
            int nth = _seen.AddOrUpdate(kind, 0, (_, count) => count + 1);
            lock (_order) _order.Add(kind);
            Frames(kind).Writer.TryWrite(frame.RootElement.Clone());
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(_script(kind, nth)));
        }

        public async ValueTask DisposeAsync() { await _listener.StoppingAsync(default); await _listener.StopAsync(default); await _listener.StoppedAsync(default); await _listener.DisposeAsync(); }
    }

    private static string Script(string kind, int nth) => kind == "active checks" ? Items : Success;

    /// <summary>An active agent named <paramref name="name"/> on <paramref name="time"/>, pointed at <paramref name="node"/>.</summary>
    private static ServiceProvider Agent(string name, ZabbixServerNode node, WatchedClock time, LogProbe logs, Action<ZabbixActiveAgentOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddMetrics();
        services.AddZabbixActiveAgent(name).AddServer(new DnsEndPoint(node.Host, node.Port)).AddItem("custom.fast", _ => "42")
            .Configure(o =>
            {
                o.Hostname = "edge-01";
                o.Connection.Timeout = TimeSpan.FromDays(1);                                 // stepping the fake clock never expires an exchange in flight
                o.RefreshInterval = TimeSpan.FromDays(1);
                o.HeartbeatInterval = TimeSpan.Zero;
                o.Send.FlushInterval = TimeSpan.FromSeconds(1);
                configure(o);
            });
        return services.BuildValidated();
    }

    private static async Task StopAsync(IHostedLifecycleService hosted)
    {
        await hosted.StoppingAsync(default);
        await hosted.StopAsync(default);
        await hosted.StoppedAsync(default);
    }

    private static Dictionary<long, JsonElement> ByItem(JsonElement frame) => frame.GetProperty("data").EnumerateArray().ToDictionary(item => item.GetProperty("itemid").GetInt64());

    private static long[] Ids(JsonElement frame) => [.. frame.GetProperty("data").EnumerateArray().Select(item => item.GetProperty("id").GetInt64())];

    [Fact]
    public void An_active_agent_composed_by_hand_takes_its_options_value_and_its_destination()
    {
        var options = new ZabbixActiveAgentOptions { Hostname = "by-hand", ServerActive = "zbx-01;zbx-02,zbx-03:10061" };
        ZabbixItemDictionary handlers = ZabbixItemDictionary.BuiltIn(options.Hostname);

        using var second = new ZabbixActiveAgentDestination("by-hand", options, handlers, NullLoggerFactory.Instance, TimeProvider.System, destination: 1);

        Assert.Equal(["zbx-03"], second.Destination.Select(node => node.Host));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZabbixActiveAgentDestination("by-hand", options, handlers, NullLoggerFactory.Instance, TimeProvider.System, destination: 2));
    }

    [Fact]
    public void The_host_and_the_assignment_take_their_optional_parts_by_overload_not_by_default_value()
    {
        List<string> defaulted = [.. new[] { typeof(ZabbixActiveAgentDestination), typeof(ZabbixItemAssignment) }
            .SelectMany(type => type.GetConstructors())
            .Where(constructor => constructor.GetParameters().Any(parameter => parameter.IsOptional))
            .Select(constructor => constructor.ToString()!)];
        var log = new ZabbixItemAssignment(7UL, "log[/var/log/app]", "1m", null);

        Assert.Empty(defaulted);
        Assert.Equal((0L, 0), (log.LastLogSize, log.Mtime));
    }

    [Fact]
    public async Task The_server_s_items_are_collected_and_uploaded_by_item_id_with_their_state_and_a_session_id()
    {
        await using FakeServer server = await FakeServer.StartAsync(Script);
        var time = new WatchedClock();
        using var logs = new LogProbe();
        using ServiceProvider provider = Agent("items", server.Node, time, logs, o => o.Send.BatchSize = 4);
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();

        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        var agent = provider.GetRequiredKeyedService<ZabbixActiveAgent>("items").Sessions[0];
        Assert.Equal(4, agent.Assignments.Count);                                       // asked before the loops start
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        JsonElement data = await server.NextAsync("agent data").WaitAsync(Guard, TestContext.Current.CancellationToken);
        await StopAsync(hosted);

        JsonElement checks = await server.NextAsync("active checks").WaitAsync(Guard, TestContext.Current.CancellationToken);
        Assert.Equal("edge-01", checks.GetProperty("host").GetString());
        Assert.Equal(agent.Session, checks.GetProperty("session").GetString());
        Assert.Equal(agent.Session, data.GetProperty("session").GetString());
        Dictionary<long, JsonElement> items = ByItem(data);
        Assert.Equal("1", items[100].GetProperty("value").GetString());
        Assert.False(items[100].TryGetProperty("state", out _));
        Assert.Equal("42", items[101].GetProperty("value").GetString());
        Assert.Equal(1, items[102].GetProperty("state").GetInt32());
        Assert.Equal("This agent does not answer the key.", items[102].GetProperty("value").GetString());
        Assert.Equal(1, items[103].GetProperty("state").GetInt32());                   // "1w" is not an update interval
        Assert.Equal("Invalid update interval \"1w\".", items[103].GetProperty("value").GetString());
        Assert.Equal([1L, 2L, 3L, 4L], Ids(data).Order());                            // running ids, one per value
        Assert.Contains(logs.Events, entry => entry.Id == 3205 && entry.Level == LogLevel.Warning);
    }

    /// <summary>A non-"success" active-checks reply is refused, logged (3201), and the (empty) assignments are left alone.</summary>
    [Fact]
    public async Task A_refused_active_checks_reply_is_logged_and_the_assignments_are_kept()
    {
        await using FakeServer server = await FakeServer.StartAsync((kind, nth) => kind == "active checks" && nth == 0
            ? """{"response":"failed","info":"host not monitored"}"""
            : Success);
        var time = new WatchedClock();
        using var logs = new LogProbe();
        using ServiceProvider provider = Agent("refused-checks", server.Node, time, logs, _ => { });
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();

        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        var agent = provider.GetRequiredKeyedService<ZabbixActiveAgent>("refused-checks").Sessions[0];
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await StopAsync(hosted);

        Assert.Empty(agent.Assignments);
        Assert.Contains(logs.Events, entry => entry.Id == 3201 && entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task An_item_list_that_repeats_an_item_id_is_taken_last_row_wins_and_warned_about()
    {
        // Upstream add_check merges a repeated item; refusing the whole list instead would refuse it on every refresh.
        await using FakeServer server = await FakeServer.StartAsync((kind, _) => kind == "active checks"
            ? """{"response":"success","data":[{"itemid":"100","key":"agent.ping","delay":"1h"},{"itemid":"100","key":"agent.ping[x]","delay":"1h"},{"itemid":"101","key":"custom.fast","delay":"1h"}],"config_revision":"2"}"""
            : Success);
        using var logs = new LogProbe();
        using ServiceProvider provider = Agent("repeats", server.Node, new WatchedClock(), logs, _ => { });
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();

        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        var agent = provider.GetRequiredKeyedService<ZabbixActiveAgent>("repeats").Sessions[0];

        Assert.Equal(2, agent.Assignments.Count);
        Assert.Equal("agent.ping[x]", agent.Assignments[100].Key);                    // the last row of the id
        Assert.Contains(logs.Events, entry => entry.Id == 3208 && entry.Level == LogLevel.Warning);
        await StopAsync(hosted);
    }

    [Fact]
    public async Task A_wall_clock_stepped_back_moves_the_check_schedule_with_it_and_is_warned_about()
    {
        // Upstream active_checks.c: "the system time has been pushed back, adjusting active check schedule".
        await using FakeServer server = await FakeServer.StartAsync((kind, _) => kind == "active checks"
            ? """{"response":"success","data":[{"itemid":"100","key":"agent.ping","delay":"1s"}],"config_revision":"1"}"""
            : Success);
        var clock = new AdversarialSteppedClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        using var logs = new LogProbe();
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(clock);
        services.AddLogging(logging => logging.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddMetrics();
        services.AddZabbixActiveAgent("stepped").AddServer(new DnsEndPoint(server.Node.Host, server.Node.Port)).Configure(o =>
        {
            o.Hostname = "edge-01";
            o.Connection.Timeout = TimeSpan.FromDays(1);
            o.RefreshInterval = TimeSpan.FromDays(1);
            o.HeartbeatInterval = TimeSpan.Zero;
            o.Send.BatchSize = 1;
            o.Send.FlushInterval = TimeSpan.FromMinutes(7);
        });
        using ServiceProvider provider = services.BuildValidated();
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();
        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await server.NextAsync("agent data").WaitAsync(Guard, TestContext.Current.CancellationToken);   // the first collection
        await foreach (TimeSpan due in clock.Created.ReadAllAsync(TestContext.Current.CancellationToken))
            if (due == TimeSpan.FromSeconds(1)) break;                                // the check loop waits a second

        clock.Step(TimeSpan.FromHours(-1));
        clock.Advance(TimeSpan.FromSeconds(1));

        await server.NextAsync("agent data").WaitAsync(Guard, TestContext.Current.CancellationToken);   // collected on, not an hour later
        await logs.WaitForAsync(3209).WaitAsync(Guard, TestContext.Current.CancellationToken);
        Assert.Contains(logs.Events, entry => entry.Id == 3209 && entry.Level == LogLevel.Warning);
        await StopAsync(hosted);
    }

    [Fact]
    public async Task A_flexible_interval_decides_when_an_item_is_collected_again()
    {
        // "1h;10s/1-7,00:00-24:00" is every 10 s, all week, not hourly: the flexible interval overrides the first number.
        const string flexible = """{"response":"success","data":[{"itemid":"100","key":"agent.ping","delay":"1h;10s/1-7,00:00-24:00"}],"config_revision":"1"}""";
        await using FakeServer server = await FakeServer.StartAsync((kind, _) => kind == "active checks" ? flexible : Success);
        var time = new WatchedClock();
        using var logs = new LogProbe();
        using ServiceProvider provider = Agent("flexible", server.Node, time, logs, o => o.Send.BatchSize = 1);
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();
        DateTimeOffset start = time.GetUtcNow();
        TimeSpan cadence = ZabbixItemDelay.Parse("1h;10s/1-7,00:00-24:00", CultureInfo.InvariantCulture).GetNextCheck(start, TimeZoneInfo.Utc, 100)!.Value - start;

        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        JsonElement first = await server.NextAsync("agent data").WaitAsync(Guard, TestContext.Current.CancellationToken);   // a new item is collected at once
        await time.WaitForTimerAsync(cadence, TestContext.Current.CancellationToken).WaitAsync(Guard, TestContext.Current.CancellationToken);                       // the check loop waits for the next due
        time.Advance(cadence);
        JsonElement second = await server.NextAsync("agent data").WaitAsync(Guard, TestContext.Current.CancellationToken);
        await StopAsync(hosted);

        Assert.Equal(TimeSpan.FromSeconds(10), cadence);
        Assert.Equal(start.ToUnixTimeSeconds(), ByItem(first)[100].GetProperty("clock").GetInt64());
        Assert.Equal(start.ToUnixTimeSeconds() + 10, ByItem(second)[100].GetProperty("clock").GetInt64());
    }

    [Fact]
    public async Task A_scheduling_interval_waits_for_its_first_instant()
    {
        // "0;h3m7" is daily at 03:07 and nothing else: not at start, and not on any default interval.
        const string scheduled = """{"response":"success","data":[{"itemid":"100","key":"agent.ping","delay":"0;h3m7"}],"config_revision":"1"}""";
        await using FakeServer server = await FakeServer.StartAsync((kind, _) => kind == "active checks" ? scheduled : Success);
        var time = new WatchedClock();
        using var logs = new LogProbe();
        using ServiceProvider provider = Agent("scheduled", server.Node, time, logs, o => o.Send.BatchSize = 1);
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();
        DateTimeOffset start = time.GetUtcNow();
        DateTimeOffset due = new(start.Year, start.Month, start.Day, 3, 7, 0, TimeSpan.Zero);

        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        var agent = provider.GetRequiredKeyedService<ZabbixActiveAgent>("scheduled").Sessions[0];
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await time.WaitForTimerAsync(due - start, TestContext.Current.CancellationToken).WaitAsync(Guard, TestContext.Current.CancellationToken);                   // waiting for 03:07, having collected nothing
        Assert.Equal(0, agent.Status.Stored);
        Assert.DoesNotContain("agent data", server.Order);
        time.Advance(due - start);
        JsonElement data = await server.NextAsync("agent data").WaitAsync(Guard, TestContext.Current.CancellationToken);
        await StopAsync(hosted);

        Assert.Equal(due.ToUnixTimeSeconds(), ByItem(data)[100].GetProperty("clock").GetInt64());
    }

    [Fact]
    public async Task A_failed_reply_keeps_the_batch_and_sends_it_again_with_the_same_ids()
    {
        // Upstream keeps its buffer on anything but "success"; the ids are assigned when the values enter
        // the buffer, so the retry carries the same ones and the server's last_id drops a duplicate.
        await using FakeServer server = await FakeServer.StartAsync((kind, nth) => kind switch
        {
            "active checks" => Items,
            "agent data" when nth == 0 => """{"response":"failed","info":"cannot process data"}""",
            _ => Success,
        });
        var time = new WatchedClock();
        using var logs = new LogProbe();
        var spans = new ConcurrentBag<Activity>();
        using ActivityListener listener = Listen(spans, "refusal");
        using ServiceProvider provider = Agent("refusal", server.Node, time, logs, o =>
        {
            o.Send.BatchSize = 4;
            o.Send.RetryDelay = TimeSpan.FromSeconds(1);
            o.Send.MaxRetryDelay = TimeSpan.FromSeconds(1);
        });
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();
        using var durations = new MetricCollector<double>(provider.GetRequiredService<IMeterFactory>(), ZabbixAgentTelemetry.MeterName, ZabbixAgentTelemetry.ExchangeDurationInstrument);

        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        JsonElement refused = await server.NextAsync("agent data").WaitAsync(Guard, TestContext.Current.CancellationToken);
        Task<JsonElement> again = server.NextAsync("agent data");
        await time.StepUntilAsync(again, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken).WaitAsync(Guard, TestContext.Current.CancellationToken);
        await StopAsync(hosted);

        JsonElement resent = await again;
        Assert.Equal(Ids(refused), Ids(resent));
        Assert.Equal(ByItem(refused)[101].GetProperty("clock").GetInt64(), ByItem(resent)[101].GetProperty("clock").GetInt64());
        Assert.Contains(logs.Events, entry => entry.Id == 3203 && entry.Level == LogLevel.Warning);
        Assert.Contains(spans, span => span.OperationName == "agent data" && span.Status == ActivityStatusCode.Error && (string?)span.GetTagItem("error.type") == "refused");
        Assert.Contains(spans, span => span.OperationName == "agent data" && span.Status == ActivityStatusCode.Ok);
        Assert.Contains(spans, span => span.OperationName == "active checks" && span.Status == ActivityStatusCode.Ok);
        Assert.Contains(durations.GetMeasurementSnapshot(), sample => sample.Value >= 0 && sample.ContainsTags(
            new KeyValuePair<string, object?>(ZabbixAgentTelemetry.AgentTag, "refusal"), new KeyValuePair<string, object?>("error.type", "refused")));
    }

    [Fact]
    public async Task An_upload_disabled_reply_holds_history_until_the_server_says_otherwise()
    {
        // {"upload":"disabled"} (the server's history cache is full) pauses uploads; the next reply without it
        // resumes them. The batch is held, not dropped, and not counted as a failure.
        await using FakeServer server = await FakeServer.StartAsync((kind, nth) => (kind, nth) switch
        {
            ("active checks", 0) => Items,
            ("active checks", _) => """{"response":"success","config_revision":7}""",
            ("agent data", 0) => """{"response":"failed","info":"data collection is paused","upload":"disabled"}""",
            _ => Success,
        });
        var time = new WatchedClock();
        using var logs = new LogProbe();
        using ServiceProvider provider = Agent("paused", server.Node, time, logs, o =>
        {
            o.Send.BatchSize = 4;
            o.Send.RetryDelay = TimeSpan.FromSeconds(1);
            o.Send.MaxRetryDelay = TimeSpan.FromSeconds(1);
            o.RefreshInterval = TimeSpan.FromSeconds(5);
        });
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();

        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        JsonElement paused = await server.NextAsync("agent data").WaitAsync(Guard, TestContext.Current.CancellationToken);
        // `paused` is the server's observation of the first frame; the agent reading the reply (flipping the paused flag,
        // logging 3206, holding the batch and logging 3101) is not ordered against it. Wait for the agent's own observables
        // before the clock moves: stepping time while that reply is still in flight lets the exchange's deadline fire, and
        // the batch is then retried as a failure, never held. A sleep is not a wait: the test waits for the conditions.
        await logs.WaitForAsync(3206).WaitAsync(Guard, TestContext.Current.CancellationToken);
        await logs.WaitForAsync(3101).WaitAsync(Guard, TestContext.Current.CancellationToken);
        Task<JsonElement> resumed = server.NextAsync("agent data");
        await time.StepUntilAsync(resumed, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken).WaitAsync(Guard, TestContext.Current.CancellationToken);
        await StopAsync(hosted);

        Assert.Equal(Ids(paused), Ids(await resumed));
        List<string> order = [.. server.Order];
        Assert.Equal("active checks", order[order.IndexOf("agent data") + 1]);         // nothing was sent until a refresh resumed it
        Assert.Contains(logs.Events, entry => entry.Id == 3101);
        Assert.Contains(logs.Events, entry => entry.Id == 3206);
        Assert.DoesNotContain(logs.Events, entry => entry.Id is 3203 or 3104);           // a pause is not a failure
    }

    [Fact]
    public async Task An_unreachable_server_is_a_warning_for_the_item_list_and_for_the_heartbeat()
    {
        int closed;
        using (var probe = new TcpListener(IPAddress.Loopback, 0))
        {
            probe.Start();
            closed = ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        var time = new WatchedClock();
        using var logs = new LogProbe();
        using ServiceProvider provider = Agent("unreachable", new ZabbixServerNode("127.0.0.1", closed), time, logs, o => o.HeartbeatInterval = TimeSpan.FromSeconds(5));
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();

        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        await logs.WaitForAsync(3202).WaitAsync(Guard, TestContext.Current.CancellationToken);
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(5));                                        // the heartbeat's timer exists from StartAsync
        await logs.WaitForAsync(3204).WaitAsync(Guard, TestContext.Current.CancellationToken);
        await StopAsync(hosted);

        Assert.Contains(logs.Events, entry => entry.Id == 3202 && entry.Level == LogLevel.Warning && entry.Exception is SocketException);
        Assert.Contains(logs.Events, entry => entry.Id == 3204 && entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_check_past_its_timeout_answers_the_agent_s_timeout_and_a_stop_is_not_an_answer()
    {
        // The caller's stop is not an answer: it must not be reported to the server as a timed-out check.
        var time = new FakeTimeProvider();
        ZabbixItemDictionary handlers = new ZabbixItemDictionary().Add("slow", async (key, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new ZabbixAgentAnswer(key, "never", null);
        });
        ZabbixItemKey slow = ZabbixItemKey.Parse("slow", CultureInfo.InvariantCulture);
        using var stop = new CancellationTokenSource();

        Task<ZabbixAgentAnswer> timingOut = handlers.AnswerAsync(slow, TimeSpan.FromSeconds(3), time, TestContext.Current.CancellationToken).AsTask();
        time.Advance(TimeSpan.FromSeconds(3));
        ZabbixAgentAnswer timedOut = await timingOut.WaitAsync(Guard, TestContext.Current.CancellationToken);
        Task<ZabbixAgentAnswer> stopping = handlers.AnswerAsync(slow, TimeSpan.FromSeconds(3), time, stop.Token).AsTask();
        await stop.CancelAsync();

        Assert.Equal("The key did not answer before its deadline.", timedOut.Error);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping.WaitAsync(Guard, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_handler_that_ignores_its_token_or_returns_nothing_still_gets_an_answer()
    {
        var never = new TaskCompletionSource<ZabbixAgentAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        ZabbixItemDictionary handlers = new ZabbixItemDictionary()
            .Add("deaf", (_, _) => new ValueTask<ZabbixAgentAnswer>(never.Task))
            .Add("empty", (ZabbixItemCallback)((_, _) => default));

        ZabbixAgentAnswer deaf = await handlers.AnswerAsync(ZabbixItemKey.Parse("deaf", CultureInfo.InvariantCulture), TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken).AsTask().WaitAsync(Guard, TestContext.Current.CancellationToken);
        ZabbixAgentAnswer empty = await handlers.AnswerAsync(ZabbixItemKey.Parse("empty", CultureInfo.InvariantCulture), Guard, TestContext.Current.CancellationToken);

        Assert.Equal("The key did not answer before its deadline.", deaf.Error);
        Assert.Equal("The key's handler gave no answer.", empty.Error);
    }

    [Theory]
    [InlineData("app.queue[orders]")]
    [InlineData("two words")]
    public void A_handler_cannot_be_registered_under_what_is_not_a_key_name(string name)
    {
        Assert.Throws<ArgumentException>(() => new ZabbixItemDictionary().Add(name, (key, _) => ValueTask.FromResult(ZabbixAgentAnswer.Supported(key, "1"))));
        Assert.Throws<ArgumentException>(() => new ZabbixItemDictionary()[name] = (key, _) => ValueTask.FromResult(ZabbixAgentAnswer.Supported(key, "1")));
    }

    [Fact]
    public void An_active_agent_registered_twice_under_one_name_is_one_hosted_service()
    {
        var services = new ServiceCollection();
        services.AddZabbixActiveAgent("edge").AddServer(new DnsEndPoint("127.0.0.1", 10051));
        services.AddZabbixActiveAgent("edge").Configure(o => o.MaxItems = 5);
        using ServiceProvider provider = services.BuildValidated();

        Assert.Single(provider.GetServices<IHostedService>());
        Assert.Equal(5, provider.GetRequiredService<IOptionsMonitor<ZabbixActiveAgentOptions>>().Get("edge").MaxItems);
    }

    [Fact]
    public void Wait_is_refused_as_the_overflow_of_an_active_agent()
    {
        var services = new ServiceCollection();
        services.AddZabbixActiveAgent("edge").AddServer(new DnsEndPoint("127.0.0.1", 10051)).Configure(o => o.Overflow = BoundedChannelFullMode.Wait);
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptionsMonitor<ZabbixActiveAgentOptions>>().Get("edge"));
    }

    [Theory]
    [InlineData(0.5)]                                                                            // not whole seconds
    [InlineData(3601)]                                                                           // past ZBX_AGENT_HEARTBEAT_FREQUENCY_MAX
    [InlineData(-1)]
    public void A_heartbeat_interval_the_server_would_refuse_is_refused_at_start(double seconds)
    {
        // trapper.c 8.0:1136 takes heartbeat_freq 1..3600 whole seconds; anything else is dropped there.
        var services = new ServiceCollection();
        services.AddZabbixActiveAgent("edge").AddServer(new DnsEndPoint("127.0.0.1", 10051)).Configure(o => o.HeartbeatInterval = TimeSpan.FromSeconds(seconds));
        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException refused = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptionsMonitor<ZabbixActiveAgentOptions>>().Get("edge"));
        Assert.Contains("HeartbeatInterval", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]                                                                              // off
    [InlineData(1)]
    [InlineData(3600)]
    public void A_heartbeat_interval_of_whole_seconds_up_to_an_hour_or_zero_is_taken(int seconds)
    {
        var services = new ServiceCollection();
        services.AddZabbixActiveAgent("edge").AddServer(new DnsEndPoint("127.0.0.1", 10051)).Configure(o => o.HeartbeatInterval = TimeSpan.FromSeconds(seconds));
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Equal(TimeSpan.FromSeconds(seconds), provider.GetRequiredService<IOptionsMonitor<ZabbixActiveAgentOptions>>().Get("edge").HeartbeatInterval);
    }

    /// <summary>Collects the agent layer's spans for one agent.</summary>
    private static ActivityListener Listen(ConcurrentBag<Activity> spans, string agent)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ZabbixAgentTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => { if ((string?)activity.GetTagItem(ZabbixAgentTelemetry.AgentTag) == agent) spans.Add(activity); },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
