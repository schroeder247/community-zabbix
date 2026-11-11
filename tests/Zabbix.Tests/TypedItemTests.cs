// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>
/// A <see cref="ZabbixItemKey{T}"/> declares a key once with its type, and every path takes it: a send, a read from an agent, an
/// answer, and a poll. An application's own <see cref="Meter"/> reports through a push agent with no Zabbix type in its code.
/// </summary>
public sealed class TypedItemTests
{
    private static readonly ZabbixItemKey<long> Requests = new("app.requests");
    private static readonly ZabbixItemKey<string> Version = new("app.version");
    private static readonly ZabbixItemKey<long> DiskFree = new("app.disk.free");

    private static ZabbixExchangeCallback Answering(string reply, List<JsonElement> sent) => (request, _) =>
    {
        sent.Add(JsonDocument.Parse(request).RootElement.Clone());
        return ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(reply));
    };

    private static string Agent(string value) => $$"""{"version":"7.0.0","variant":1,"data":[{"value":"{{value}}"}]}""";

    // ----- the item -------------------------------------------------------------------------------------------------

    [Fact]
    public void An_item_reads_and_writes_each_type_as_Zabbix_carries_it()
    {
        Assert.Equal(42L, Requests.Parse("42"));
        Assert.Equal("42", Requests.Format(42L));
        Assert.Equal(0.75, new ZabbixItemKey<double>("a").Parse("0.75"));
        Assert.Equal("0.75", new ZabbixItemKey<double>("a").Format(0.75));
        Assert.True(new ZabbixItemKey<bool>("a").Parse("1"));
        Assert.False(new ZabbixItemKey<bool>("a").Parse("0"));
        Assert.Equal("1", new ZabbixItemKey<bool>("a").Format(true));
        Assert.Equal(TimeSpan.FromSeconds(90), new ZabbixItemKey<TimeSpan>("a").Parse("90"));
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(100), new ZabbixItemKey<DateTimeOffset>("a").Parse("100"));
        Assert.Equal("100", new ZabbixItemKey<DateTimeOffset>("a").Format(DateTimeOffset.UnixEpoch.AddSeconds(100)));
        Assert.Equal("text", Version.Parse("text"));
    }

    [Fact]
    public void A_value_of_another_type_says_so_and_Zabbix_stores_no_NaN()
    {
        Assert.Throws<FormatException>(() => Requests.Parse("not a number"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZabbixItemKey<double>("a").Format(double.NaN));
    }

    [Fact]
    public void An_item_takes_parameters_as_the_key_writes_them()
    {
        Assert.Equal("app.disk.free[/]", DiskFree.With("/").ToString());
        Assert.Equal("app.disk.free[\"a,b\",c]", DiskFree.With("a,b", "c").ToString());
    }

    [Fact]
    public void A_key_that_is_not_a_key_is_refused_where_the_item_is_declared()
        => Assert.ThrowsAny<FormatException>(() => new ZabbixItemKey<long>("not a key"));

    // ----- the wire -------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_typed_send_carries_the_host_the_key_and_the_value()
    {
        List<JsonElement> sent = [];
        using var wire = new ZabbixWireClient(Answering(MockTrapper.Success(1, 0), sent));

        ZabbixSenderResponse response = await wire.SendAsync("web-01", Requests, 42, TestContext.Current.CancellationToken);

        JsonElement row = Assert.Single(sent).GetProperty("data")[0];
        Assert.Equal(("web-01", "app.requests", "42"), (row.GetProperty("host").GetString(), row.GetProperty("key").GetString(), row.GetProperty("value").GetString()));
        Assert.Equal(1, response.Processed);
    }

    [Fact]
    public async Task A_batch_sends_its_values_together()
    {
        List<JsonElement> sent = [];
        using var wire = new ZabbixWireClient(Answering(MockTrapper.Success(2, 0), sent));

        await wire.SendAsync(
            batch => batch.Add("web-01", Requests, 42).Add("web-02", Version, "2.4.1", DateTimeOffset.UnixEpoch.AddSeconds(100)),
            TestContext.Current.CancellationToken);

        JsonElement data = Assert.Single(sent).GetProperty("data");
        Assert.Equal(2, data.GetArrayLength());
        Assert.Equal("2.4.1", data[1].GetProperty("value").GetString());
        Assert.Equal(100, data[1].GetProperty("clock").GetInt64());
    }

    [Fact]
    public async Task A_typed_read_answers_the_type_and_throws_for_what_an_agent_does_not_support()
    {
        List<JsonElement> sent = [];
        using var answering = new ZabbixWireClient(Answering(Agent("17"), sent));
        using var refusing = new ZabbixWireClient(Answering("""{"version":"7.0.0","variant":1,"data":[{"error":"no such key"}]}""", sent));

        long free = await answering.GetAgentItemAsync("web-01", DiskFree.With("/"), TestContext.Current.CancellationToken);
        ZabbixItemNotSupportedException refused = await Assert.ThrowsAsync<ZabbixItemNotSupportedException>(() => refusing.GetAgentItemAsync("web-01", DiskFree, TestContext.Current.CancellationToken));

        Assert.Equal(17, free);
        Assert.Equal("app.disk.free[/]", sent[0].GetProperty("data")[0].GetProperty("key").GetString());
        Assert.Equal("no such key", refused.Reason);
    }

    // ----- answering ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_agent_answers_a_typed_item_formatted_as_Zabbix_reads_it()
    {
        ZabbixItemDictionary table = new ZabbixItemDictionary()
            .Add(Requests, () => 42L)
            .Add(new ZabbixItemKey<double>("app.ratio"), (_, _) => ValueTask.FromResult(0.75));

        Assert.Equal("42", (await AnswerAsync(table, "app.requests")).Value);
        Assert.Equal("0.75", (await AnswerAsync(table, "app.ratio")).Value);
    }

    [Fact]
    public async Task A_typed_answer_reads_the_key_s_parameters_and_a_bad_one_is_not_supported()
    {
        ZabbixItemDictionary table = new ZabbixItemDictionary()
            .Add(DiskFree, key => key[0].Length)
            .Add(new ZabbixItemKey<long>("app.sum"), key => long.Parse(key[0], CultureInfo.InvariantCulture) + long.Parse(key[1], CultureInfo.InvariantCulture));

        Assert.Equal("3", (await AnswerAsync(table, "app.disk.free[abc]")).Value);
        Assert.Equal("5", (await AnswerAsync(table, "app.sum[2,3]")).Value);
        Assert.False((await AnswerAsync(table, "app.sum[2,x]")).IsSupported);
        Assert.False((await AnswerAsync(table, "app.disk.free")).IsSupported);
    }

    [Fact]
    public void Adding_items_chains_and_keeps_the_builder_s_own_type()
    {
        using ZabbixActiveAgent active = ZabbixActiveAgent.CreateBuilder("web-01")
            .AddServer("zbx")
            .AddItem(Version, () => "2.4.1")
            .AddItem(DiskFree, key => key[0].Length)
            .Build();

        Assert.True(active.Items.ContainsKey("app.version"));
        Assert.True(active.Items.ContainsKey("app.disk.free"));
    }

    // ----- the item shapes: each lambda picks its overload, and the key's type fixes the value's --------------------------

    [Fact]
    public async Task Every_item_shape_answers_on_a_built_agent_with_its_services_from_the_builder()
    {
        ZabbixPassiveAgentBuilder builder = ZabbixPassiveAgent.CreateBuilder("web-01").AllowPeer(IPAddress.Loopback);
        builder.Services.AddSingleton<IOrders>(new Orders(7));
        using ZabbixPassiveAgent agent = Shapes(builder).Build();

        await AssertShapesAsync(agent.Items);
    }

    [Fact]
    public async Task Every_item_shape_answers_on_a_registered_agent_with_its_services_from_the_host()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOrders>(new Orders(7));
        Shapes(services.AddZabbixActiveAgent("web-01").AddServer("zbx-1"));
        using ServiceProvider provider = services.BuildValidated();

        await AssertShapesAsync(provider.GetRequiredKeyedService<ZabbixActiveAgent>("web-01").Items);
    }

    [Fact]
    public async Task A_service_an_item_reads_is_resolved_in_a_scope_of_its_own_for_each_check()
    {
        var scopes = new ScopeLog();
        ZabbixActiveAgentBuilder builder = ZabbixActiveAgent.CreateBuilder("web-01").AddServer("zbx-1")
            .AddItem(new ZabbixItemKey<string>("app.scope"), (Scoped scoped) => scoped.Id);
        builder.Services.AddSingleton(scopes).AddScoped<Scoped>();
        using ZabbixActiveAgent agent = builder.Build();

        string? first = (await AnswerAsync(agent.Items, "app.scope")).Value;
        string? second = (await AnswerAsync(agent.Items, "app.scope")).Value;

        Assert.NotEqual(first, second);
        Assert.Equal(2, scopes.Disposed);                                                  // each check's scope ends with it
    }

    [Fact]
    public void An_item_or_a_poll_that_reads_a_service_the_container_lacks_fails_the_build()
    {
        ZabbixActiveAgentBuilder item = ZabbixActiveAgent.CreateBuilder("web-01").AddServer("zbx-1").AddItem(Requests, (IOrders orders) => orders.Pending);
        ZabbixPushAgentBuilder poll = ZabbixPushAgent.CreateBuilder("web-01").AddServer("zbx-1").AddPoll(Requests, TimeSpan.FromMinutes(1), (IOrders orders, CancellationToken ct) => orders.CountAsync(ct));

        InvalidOperationException refusedItem = Assert.Throws<InvalidOperationException>(() => item.Build().Dispose());
        InvalidOperationException refusedPoll = Assert.Throws<InvalidOperationException>(() => poll.Build().Dispose());

        Assert.All([refusedItem, refusedPoll], refused =>
        {
            Assert.Contains(nameof(IOrders), refused.Message, StringComparison.Ordinal);
            Assert.Contains("app.requests", refused.Message, StringComparison.Ordinal);
        });
    }

    // The five shapes and the string keys, written as a caller writes them: each lambda picks its overload by its shape.
    private static TBuilder Shapes<TBuilder>(TBuilder builder) where TBuilder : IZabbixItemsBuilder
        => builder
            .AddItem(new ZabbixItemKey<long>("shape.value"), () => 1L)
            .AddItem(new ZabbixItemKey<long>("shape.key"), key => key[0].Length)
            .AddItem(new ZabbixItemKey<double>("shape.async"), async (key, ct) =>
            {
                await Task.Yield();
                return key.Parameters.Count + 0.5;
            })
            .AddItem(new ZabbixItemKey<long>("shape.service"), (IOrders orders) => orders.Pending)
            .AddItem(new ZabbixItemKey<long>("shape.service.async"), (IOrders orders, CancellationToken ct) => orders.CountAsync(ct))
            .AddItem("shape.text", key => "text:" + key[0])
            .AddItem("shape.text.async", (key, ct) => ValueTask.FromResult("async"))
            .AddItem("shape.answer", (key, ct) => ValueTask.FromResult(ZabbixAgentAnswer.NotSupported(key, "not here")));

    private static async Task AssertShapesAsync(ZabbixItemDictionary items)
    {
        Assert.Equal("1", (await AnswerAsync(items, "shape.value")).Value);
        Assert.Equal("3", (await AnswerAsync(items, "shape.key[abc]")).Value);
        Assert.Equal("2.5", (await AnswerAsync(items, "shape.async[a,b]")).Value);
        Assert.Equal("7", (await AnswerAsync(items, "shape.service")).Value);
        Assert.Equal("8", (await AnswerAsync(items, "shape.service.async")).Value);
        Assert.Equal("text:x", (await AnswerAsync(items, "shape.text[x]")).Value);
        Assert.Equal("async", (await AnswerAsync(items, "shape.text.async")).Value);
        Assert.Equal("not here", (await AnswerAsync(items, "shape.answer")).Error);
        Assert.False((await AnswerAsync(items, "shape.key")).IsSupported);                     // a parameter the key doesn't have
    }

    private static ValueTask<ZabbixAgentAnswer> AnswerAsync(ZabbixItemDictionary table, string key)
        => table.AnswerAsync(ZabbixItemKey.Parse(key, CultureInfo.InvariantCulture), TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

    // ----- the push agent -------------------------------------------------------------------------------------------

    [Fact]
    public void A_typed_poll_or_a_meter_needs_a_host_and_the_build_says_so()
    {
        ZabbixPushAgentBuilder builder = ZabbixPushAgent.CreateBuilder("edge").AddServer("127.0.0.1").AddMeter("Not.Used").Configure(o => o.Hostname = null);

        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => builder.Build().Dispose());

        Assert.Contains("Hostname", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_poll_or_a_meter_s_interval_is_refused_where_it_is_passed()
    {
        Assert.Equal("interval", Assert.Throws<ArgumentOutOfRangeException>(() => ZabbixPushAgent.CreateBuilder("edge").AddPoll(Requests, TimeSpan.Zero, () => 1L)).ParamName);
        Assert.Equal("interval", Assert.Throws<ArgumentOutOfRangeException>(() => ZabbixPushAgent.CreateBuilder("edge").AddPoll(TimeSpan.FromSeconds(-1), () => new ZabbixValue("web-01", "k", "1", DateTimeOffset.UnixEpoch))).ParamName);
        Assert.Equal("interval", Assert.Throws<ArgumentOutOfRangeException>(() => ZabbixPushAgent.CreateBuilder("edge").AddMeter("MyApp", TimeSpan.Zero)).ParamName);
    }

    [Fact]
    public async Task A_typed_poll_sends_for_the_agent_s_host()
    {
        (int port, Task<IReadOnlyList<byte[]>> frames) = MockTrapper.Record(1);
        await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")
            .AddServer($"127.0.0.1:{port}")
            .AddPoll(Requests, TimeSpan.FromMilliseconds(50), () => 42L)
            .Configure(o => o.Send.FlushInterval = TimeSpan.FromMilliseconds(50))
            .Build();
        await agent.StartAsync(TestContext.Current.CancellationToken);

        JsonElement row = RowsOf(await frames.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))[0];

        Assert.Equal(("web-01", "app.requests", "42"), (row.GetProperty("host").GetString(), row.GetProperty("key").GetString(), row.GetProperty("value").GetString()));
    }

    [Fact]
    public async Task Every_poll_shape_reads_its_value_for_the_agent_s_host()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IOrders>(new Orders(7));
        TimeSpan minute = TimeSpan.FromMinutes(1);                                                // a poll's first cycle runs at once
        services.AddZabbixPushAgent("web-01").AddServer("zbx-1")
            .AddPoll(new ZabbixItemKey<long>("poll.value"), minute, () => 1L)
            .AddPoll(new ZabbixItemKey<long>("poll.async"), minute, ct => ValueTask.FromResult(2L))
            .AddPoll(new ZabbixItemKey<long>("poll.service"), minute, (IOrders orders) => orders.Pending)
            .AddPoll(new ZabbixItemKey<long>("poll.service.async"), minute, (IOrders orders, CancellationToken ct) => orders.CountAsync(ct))
            .AddPoll(minute, () => new ZabbixValue("other", "poll.untyped", "3", DateTimeOffset.UnixEpoch))
            .AddPoll(minute, ct => ValueTask.FromResult(new ZabbixValue("other", "poll.untyped.async", "4", DateTimeOffset.UnixEpoch)))
            .AddPoll(minute, ct => ValueTask.FromResult<IEnumerable<ZabbixValue>>([new("other", "poll.many", "5", DateTimeOffset.UnixEpoch), new("other", "poll.many", "6", DateTimeOffset.UnixEpoch)]));
        await using ServiceProvider provider = services.BuildValidated();
        ZabbixPushAgentOptions options = provider.GetRequiredService<IOptionsMonitor<ZabbixPushAgentOptions>>().Get("web-01");

        List<ZabbixValue> read = [];
        foreach (Func<IServiceProvider, IAsyncEnumerable<ZabbixValue>> source in options.Sources)
            read.AddRange(await source(provider).Take(1).ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal(
            [("web-01", "poll.value", "1"), ("web-01", "poll.async", "2"), ("web-01", "poll.service", "7"), ("web-01", "poll.service.async", "8"),
             ("other", "poll.untyped", "3"), ("other", "poll.untyped.async", "4"), ("other", "poll.many", "5")],
            read.Select(value => (value.Host, value.Key.ToString(), value.Value)));
    }

    [Fact]
    public async Task A_built_agent_s_poll_reads_a_service_from_the_builder_s_container()
    {
        (int port, Task<IReadOnlyList<byte[]>> frames) = MockTrapper.Record(1);
        ZabbixPushAgentBuilder builder = ZabbixPushAgent.CreateBuilder("web-01")
            .AddServer($"127.0.0.1:{port}")
            .AddPoll(Requests, TimeSpan.FromMinutes(1), (IOrders orders) => orders.Pending)
            .Configure(o => o.Send.FlushInterval = TimeSpan.FromMilliseconds(50));
        builder.Services.AddSingleton<IOrders>(new Orders(7));
        await using ZabbixPushAgent agent = builder.Build();
        await agent.StartAsync(TestContext.Current.CancellationToken);

        JsonElement row = RowsOf(await frames.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))[0];

        Assert.Equal(("web-01", "app.requests", "7"), (row.GetProperty("host").GetString(), row.GetProperty("key").GetString(), row.GetProperty("value").GetString()));
    }

    [Fact]
    public async Task A_meter_s_counter_sends_its_running_total_and_its_tags_become_parameters()
    {
        string name = "Typed.Tests." + Guid.NewGuid().ToString("N");
        using Meter meter = new(name);
        Counter<long> sold = meter.CreateCounter<long>("app.sold");
        (int port, Task<IReadOnlyList<byte[]>> frames) = MockTrapper.Record(3);
        await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")
            .AddServer($"127.0.0.1:{port}")
            .AddMeter(name, TimeSpan.FromMilliseconds(50))
            .Configure(o => o.Send.FlushInterval = TimeSpan.FromMilliseconds(50))
            .Build();
        await agent.StartAsync(TestContext.Current.CancellationToken);
        await WaitForListenerAsync(meter);

        sold.Add(2);
        sold.Add(3);
        sold.Add(7, new KeyValuePair<string, object?>("colour", "red"));

        List<JsonElement> rows = RowsOf(await frames.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Contains(rows, row => row.GetProperty("key").GetString() == "app.sold" && row.GetProperty("value").GetString() == "5" && row.GetProperty("host").GetString() == "web-01");
        Assert.Contains(rows, row => row.GetProperty("key").GetString() == "app.sold[red]" && row.GetProperty("value").GetString() == "7");
    }

    [Fact]
    public async Task A_meter_s_gauge_and_observable_send_what_they_last_said_and_a_histogram_is_left_out()
    {
        string name = "Typed.Tests." + Guid.NewGuid().ToString("N");
        using Meter meter = new(name);
        Gauge<double> level = meter.CreateGauge<double>("app.level");
        meter.CreateObservableGauge("app.queue", () => 9L);
        Histogram<double> latency = meter.CreateHistogram<double>("app.latency");
        (int port, Task<IReadOnlyList<byte[]>> frames) = MockTrapper.Record(3);
        await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")
            .AddServer($"127.0.0.1:{port}")
            .AddMeter(name, TimeSpan.FromMilliseconds(50))
            .Configure(o => o.Send.FlushInterval = TimeSpan.FromMilliseconds(50))
            .Build();
        await agent.StartAsync(TestContext.Current.CancellationToken);
        await WaitForListenerAsync(meter);

        level.Record(1.5);
        level.Record(2.5);
        latency.Record(100);

        List<JsonElement> rows = RowsOf(await frames.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Contains(rows, row => row.GetProperty("key").GetString() == "app.level" && row.GetProperty("value").GetString() == "2.5");
        Assert.Contains(rows, row => row.GetProperty("key").GetString() == "app.queue" && row.GetProperty("value").GetString() == "9");
        Assert.DoesNotContain(rows, row => row.GetProperty("key").GetString() == "app.latency");
    }

    [Fact]
    public async Task A_histogram_and_a_name_a_key_can_t_hold_are_left_out_with_a_warning()
    {
        string name = "Typed.Tests." + Guid.NewGuid().ToString("N");
        using Meter meter = new(name);
        var logs = new FakeLogCollector();
        using var provider = new FakeLoggerProvider(logs);
        using var loggers = new LoggerFactory([provider]);
        (int port, Task<IReadOnlyList<byte[]>> frames) = MockTrapper.Record(1);
        await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")
            .AddServer($"127.0.0.1:{port}")
            .AddMeter(name, TimeSpan.FromMilliseconds(50))
            .Configure(o => o.Send.FlushInterval = TimeSpan.FromMilliseconds(50))
            .UseLogging(loggers)
            .Build();
        await agent.StartAsync(TestContext.Current.CancellationToken);
        await WaitForListenerAsync(meter);

        meter.CreateHistogram<double>("app.latency").Record(1);
        meter.CreateCounter<long>("not a key").Add(1);
        meter.CreateCounter<long>("app.good").Add(1);

        // A counter sends its running total every cycle, so a slow machine can put several of its rows in one frame.
        JsonElement[] rows = [.. RowsOf(await frames.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)).Where(value => value.GetProperty("key").GetString() != "probe.listening")];
        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.Equal("app.good", row.GetProperty("key").GetString()));
        Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 3010);
        Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 3011);
    }

    // The listener starts when the agent's pump first reads the source, a moment after StartAsync.
    private static async Task WaitForListenerAsync(Meter meter)
    {
        Counter<long> probe = meter.CreateCounter<long>("probe.listening");
        for (int attempt = 0; attempt < 500 && !probe.Enabled; attempt++)
            await Task.Delay(TimeSpan.FromMilliseconds(10), TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.True(probe.Enabled, "the agent's listener never subscribed to the meter");
    }

    internal interface IOrders
    {
        long Pending { get; }

        ValueTask<long> CountAsync(CancellationToken cancellationToken);
    }

    private sealed class Orders(long pending) : IOrders
    {
        public long Pending => pending;

        public ValueTask<long> CountAsync(CancellationToken cancellationToken) => ValueTask.FromResult(pending + 1);
    }

    private sealed class ScopeLog
    {
        private int _disposed;

        public int Disposed => Volatile.Read(ref _disposed);

        public void Ended() => Interlocked.Increment(ref _disposed);
    }

    private sealed class Scoped(ScopeLog scopes) : IDisposable
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");

        public void Dispose() => scopes.Ended();
    }

    private static List<JsonElement> RowsOf(IReadOnlyList<byte[]> frames)
    {
        List<JsonElement> rows = [];
        foreach (byte[] frame in frames)
            foreach (JsonElement row in JsonDocument.Parse(frame).RootElement.GetProperty("data").EnumerateArray())
                rows.Add(row.Clone());
        return rows;
    }
}
