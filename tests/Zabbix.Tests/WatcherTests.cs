// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using System.Threading.Channels;
using Community.Zabbix.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// A watcher reads every value its items collect (history since the last clock, whatever each item's delay), gives every
/// reader every value in clock order, lets a slow reader lose counted values rather than delay the poll, makes a failed
/// tick visible and keeps the cadence, never re-reads the item set every tick, and stops with its server. Every wait
/// here is on an observable (the watcher's own poll counters, a reader's next value), never on time.
/// </summary>
public sealed class WatcherTests
{
    private const long T0 = 1_700_000_000;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);                      // a safety net for a hung test, never a pacing sleep

    private static string Item(string itemId = "1", string key = "cpu", long lastclock = T0, string lastvalue = "1")
        => $$"""{"itemid":"{{itemId}}","hostid":"10","key_":"{{key}}","name":"CPU","delay":"10s","value_type":"0","lastvalue":"{{lastvalue}}","lastclock":"{{lastclock.ToString(CultureInfo.InvariantCulture)}}","lastns":"0","hosts":[{"hostid":"10","host":"web-01"}]}""";

    private sealed class Watched : IDisposable
    {
        public required ServiceProvider Provider { get; init; }

        public required ScriptedApi Api { get; init; }

        public required FakeTimeProvider Time { get; init; }

        public required FakeLogCollector Logs { get; init; }

        public required MetricCollector<long> Polls { get; init; }

        public required MetricCollector<long> Failures { get; init; }

        public required MetricCollector<long> Dropped { get; init; }

        public ZabbixClient Server => Provider.GetRequiredService<ZabbixClient>();

        /// <summary>Waits until <paramref name="ticks"/> ticks have ended, succeeded or failed: each one leaves one measurement.</summary>
        public async Task TicksAsync(int ticks)
        {
            using var timeout = new CancellationTokenSource(Wait);
            while (Polls.GetMeasurementSnapshot().Count + Failures.GetMeasurementSnapshot().Count < ticks)
            {
                Task polled = Polls.WaitForMeasurementsAsync(Polls.GetMeasurementSnapshot().Count + 1, timeout.Token);
                Task failed = Failures.WaitForMeasurementsAsync(Failures.GetMeasurementSnapshot().Count + 1, timeout.Token);
                await Task.WhenAny(polled, failed);
                timeout.Token.ThrowIfCancellationRequested();
            }
        }

        public void Dispose()
        {
            Polls.Dispose();
            Failures.Dispose();
            Dropped.Dispose();
            Provider.Dispose();
        }
    }

    private static Watched Build(ScriptedApi api, Func<ScriptedApi, HttpMessageHandler>? primary = null)
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(T0 + 5));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddFakeLogging().AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace));
        services.AddZabbixClient().Configure(options => { options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"); options.ApiToken = "tok"; });
        services.AddHttpClient(ZabbixHttp.GetClientName()).ConfigurePrimaryHttpMessageHandler(() => primary?.Invoke(api) ?? api);
        ServiceProvider provider = services.BuildValidated();
        IMeterFactory meters = provider.GetRequiredService<IMeterFactory>();
        return new Watched
        {
            Provider = provider,
            Api = api,
            Time = time,
            Logs = provider.GetFakeLogCollector(),
            Polls = new MetricCollector<long>(meters, ZabbixTelemetryNames.Tree, ZabbixTreeTelemetry.WatcherPolls),
            Failures = new MetricCollector<long>(meters, ZabbixTelemetryNames.Tree, ZabbixTreeTelemetry.WatcherFailures),
            Dropped = new MetricCollector<long>(meters, ZabbixTelemetryNames.Tree, ZabbixTreeTelemetry.WatcherDropped),
        };
    }

    private static FakeHistory Collected(int count, int every = 10)
    {
        var history = new FakeHistory().Add("1", ZabbixItemValueType.Float, T0, 0, "1");      // the item's lastvalue: already seen
        for (int n = 1; n <= count; n++) history.Add("1", ZabbixItemValueType.Float, T0 + n * every, 0, (n + 1).ToString(CultureInfo.InvariantCulture));
        return history;
    }

    [Fact]
    public async Task A_watcher_yields_every_value_the_item_collected_since_the_last_read_whatever_its_delay()
    {
        FakeHistory history = Collected(3);                                                // delay 10 s, interval 30 s: three values per tick
        using Watched w = Build(new ScriptedApi().On("item.get", $"[{Item()}]").On("history.get", history.Answer));
        await using IZabbixWatcher watcher = w.Server.Hosts["web-01"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)));
        await w.TicksAsync(1);
        await using IAsyncEnumerator<ZabbixValue> reading = watcher.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        w.Time.Advance(TimeSpan.FromSeconds(30));
        await w.TicksAsync(2);

        var values = new List<string>();
        for (int n = 0; n < 3; n++) { Assert.True(await reading.MoveNextAsync()); values.Add(reading.Current.Value); }
        Assert.Equal(["2", "3", "4"], values);                                               // 3 collected, 3 yielded, none skipped
        Assert.Equal("4", watcher.Current[("web-01", ZabbixItemKey.Parse("cpu"))].Value);
        Assert.Equal(0, watcher.DroppedCount);
        Assert.Equal(2, watcher.SucceededPolls);
    }

    [Fact]
    public async Task Every_value_collected_between_watch_and_its_first_poll_is_yielded_not_only_the_latest()
    {
        // The watch begins at T0+5; three values arrive before its first item.get, which reports only the latest.
        FakeHistory history = new FakeHistory().Add("1", ZabbixItemValueType.Float, T0, 0, "1")
            .Add("1", ZabbixItemValueType.Float, T0 + 6, 0, "2").Add("1", ZabbixItemValueType.Float, T0 + 6, 500_000_000, "3").Add("1", ZabbixItemValueType.Float, T0 + 7, 0, "4");
        var attached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using Watched w = Build(new ScriptedApi().On("item.get", $"[{Item(lastclock: T0 + 7, lastvalue: "4")}]").On("history.get", history.Answer), api => new Held(api, attached.Task));
        await using IZabbixWatcher watcher = w.Server.Hosts["web-01"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)));
        await using IAsyncEnumerator<ZabbixValue> reading = watcher.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        attached.SetResult();                                                                // the first poll's item.get answers only now
        await w.TicksAsync(1);

        var values = new List<string>();
        for (int n = 0; n < 3; n++) { Assert.True(await reading.MoveNextAsync().AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken)); values.Add(reading.Current.Value); }

        Assert.Equal(["2", "3", "4"], values);                                               // "1" was collected before the watch: not a new value
        Assert.Equal("4", watcher.Current[("web-01", ZabbixItemKey.Parse("cpu"))].Value);
    }

    /// <summary>Holds every request until <paramref name="release"/> completes.</summary>
    private sealed class Held(HttpMessageHandler inner, Task release) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await release.WaitAsync(cancellationToken);
            return await base.SendAsync(request, cancellationToken);
        }
    }

    [Fact]
    public async Task Every_reader_gets_every_value()
    {
        using Watched w = Build(new ScriptedApi().On("item.get", $"[{Item()}]").On("history.get", Collected(1).Answer));
        await using IZabbixWatcher watcher = w.Server.Hosts["web-01"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)));
        await w.TicksAsync(1);
        await using IAsyncEnumerator<ZabbixValue> first = watcher.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using IAsyncEnumerator<ZabbixValue> second = watcher.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        w.Time.Advance(TimeSpan.FromSeconds(30));
        await w.TicksAsync(2);

        Assert.True(await first.MoveNextAsync());
        Assert.True(await second.MoveNextAsync());
        Assert.Equal("2", first.Current.Value);
        Assert.Equal("2", second.Current.Value);
    }

    [Fact]
    public async Task A_slow_reader_loses_counted_values_and_never_delays_the_poll()
    {
        using Watched w = Build(new ScriptedApi().On("item.get", $"[{Item()}]").On("history.get", Collected(5, every: 5).Answer));
        await using IZabbixWatcher watcher = w.Server.Hosts["web-01"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)) { MaxItems = 2 });
        await w.TicksAsync(1);
        await using IAsyncEnumerator<ZabbixValue> reading = watcher.GetAsyncEnumerator(TestContext.Current.CancellationToken);       // attached, never read

        w.Time.Advance(TimeSpan.FromSeconds(30));
        await w.TicksAsync(2);

        Assert.Equal(3, watcher.DroppedCount);                                                    // five values, a bound of two: three shed, counted
        Assert.Equal(3, w.Dropped.GetMeasurementSnapshot().Sum(m => m.Value));               // …and measured
        Assert.Contains(w.Logs.GetSnapshot(), record => record.Id.Id == 5009);
        Assert.True(await reading.MoveNextAsync());
        Assert.Equal("5", reading.Current.Value);                                            // the oldest were shed; the newest two remain
    }

    [Fact]
    public void A_plan_that_waits_on_a_reader_is_refused()
    {
        using Watched w = Build(new ScriptedApi().On("item.get", "[]"));

        Assert.Throws<ArgumentException>(() => w.Server.Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(1)) { WhenFull = BoundedChannelFullMode.Wait }));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_plan_with_no_room_for_a_reader_is_refused_when_the_watch_starts(int capacity)
    {
        using Watched w = Build(new ScriptedApi().On("item.get", "[]"));

        Assert.Throws<ArgumentOutOfRangeException>(() => w.Server.Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(1)) { MaxItems = capacity }));
    }

    [Fact]
    public void A_plan_whose_full_mode_is_not_one_is_refused()
    {
        using Watched w = Build(new ScriptedApi().On("item.get", "[]"));

        Assert.Throws<ArgumentOutOfRangeException>(() => w.Server.Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(1)) { WhenFull = (BoundedChannelFullMode)42 }));
    }

    [Fact]
    public async Task A_disposed_server_says_so_on_every_member_instead_of_cancelling()
    {
        using Watched w = Build(new ScriptedApi().On("apiinfo.version", "\"7.0.0\"").On("item.get", "[]"));
        ZabbixClient client = w.Server;
        await w.Provider.DisposeAsync();                                                              // the container that shares the client

        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetApiVersionAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.GetFrontendAsync(TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => client.Hosts["web-01"]);
        Assert.Throws<ObjectDisposedException>(() => client.HostGroups["Linux servers"]);
        Assert.Throws<ObjectDisposedException>(() => client.Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(1))));
        Assert.Throws<ObjectDisposedException>(() => client.GetLatestAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Disposing_the_watcher_ends_every_reader()
    {
        using Watched w = Build(new ScriptedApi().On("item.get", "[]"));
        IZabbixWatcher watcher = w.Server.Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)));
        await using IAsyncEnumerator<ZabbixValue> reading = watcher.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        ValueTask<bool> pending = reading.MoveNextAsync();

        await watcher.DisposeAsync();

        Assert.False(await pending.AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Disposing_the_server_stops_its_watchers_and_ends_their_readers()
    {
        using Watched w = Build(new ScriptedApi().On("item.get", $"[{Item()}]").On("history.get", "[]"));
        ZabbixClient client = w.Server;
        IZabbixWatcher watcher = client.Hosts["web-01"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)));
        await w.TicksAsync(1);
        await using IAsyncEnumerator<ZabbixValue> reading = watcher.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        ValueTask<bool> pending = reading.MoveNextAsync();

        await w.Provider.DisposeAsync();                                                              // the server's end, by its container, is its watchers' end

        Assert.False(await pending.AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken));
        await watcher.DisposeAsync();
        w.Time.Advance(TimeSpan.FromSeconds(90));
        Assert.Single(w.Api.ParamsOf("item.get"));                                            // no tick after the server went
        Assert.Contains(w.Logs.GetSnapshot(), record => record.Id.Id == 5004 && record.Message.Contains("its client was disposed", StringComparison.Ordinal));
        Assert.DoesNotContain(w.Logs.GetSnapshot(), record => record.Id.Id == 5003);         // no ObjectDisposedException every tick
        Assert.Throws<ObjectDisposedException>(() => client.Hosts["web-01"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30))));
    }

    [Fact]
    public async Task A_failed_tick_is_counted_logged_and_skipped_and_the_next_tick_reads_what_it_missed()
    {
        FakeHistory history = Collected(1);
        int reads = 0;
        using Watched w = Build(new ScriptedApi().On("item.get", $"[{Item()}]")
            .On("history.get", parameters => ++reads == 1 ? "\"not rows\"" : history.Answer(parameters)));
        await using IZabbixWatcher watcher = w.Server.Hosts["web-01"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)));
        await w.TicksAsync(1);
        await using IAsyncEnumerator<ZabbixValue> reading = watcher.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        w.Time.Advance(TimeSpan.FromSeconds(30));
        await w.TicksAsync(2);
        w.Time.Advance(TimeSpan.FromSeconds(30));
        await w.TicksAsync(3);

        Assert.Equal(1, watcher.FailedPolls);
        Assert.Equal(2, watcher.SucceededPolls);
        CollectedMeasurement<long> failure = Assert.Single(w.Failures.GetMeasurementSnapshot());
        Assert.True(failure.Tags.ContainsKey("error.type"));
        Assert.Contains(w.Logs.GetSnapshot(), record => record.Id.Id == 5003 && record.Level == LogLevel.Error);
        Assert.True(await reading.MoveNextAsync());
        Assert.Equal("2", reading.Current.Value);                                            // the value the failed tick would have read
    }

    [Fact]
    public async Task An_unreadable_key_is_left_out_and_logged_once_not_every_tick()
    {
        using Watched w = Build(new ScriptedApi().On("item.get", $"[{Item()},{Item("2", "key[a]]")}]").On("history.get", Collected(2).Answer));
        await using IZabbixWatcher watcher = w.Server.Hosts["web-01"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(10)) { RefreshInterval = TimeSpan.FromSeconds(10) });
        await w.TicksAsync(1);

        for (int tick = 2; tick <= 3; tick++)
        {
            w.Time.Advance(TimeSpan.FromSeconds(10));
            await w.TicksAsync(tick);
        }

        Assert.Equal(3, w.Api.ParamsOf("item.get").Count());                                 // the item set was re-read every tick here…
        Assert.Single(w.Logs.GetSnapshot(), record => record.Id.Id == 5005);                  // …and the bad key logged once
        Assert.Equal(3, watcher.SucceededPolls);                                              // the good item kept the node alive
    }

    [Fact]
    public async Task The_item_set_is_read_once_per_refresh_not_every_tick_and_each_tick_asks_only_for_known_items()
    {
        using Watched w = Build(new ScriptedApi().On("item.get", $"[{Item()},{Item("7", "mem")}]").On("history.get", "[]"));
        await using IZabbixWatcher watcher = w.Server.Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(10)) { RefreshInterval = TimeSpan.FromSeconds(60) });
        await w.TicksAsync(1);

        for (int tick = 2; tick <= 4; tick++)
        {
            w.Time.Advance(TimeSpan.FromSeconds(10));
            await w.TicksAsync(tick);
        }

        Assert.Single(w.Api.ParamsOf("item.get"));                                            // the server scope did not re-read every item every tick
        JsonElement[] reads = [.. w.Api.ParamsOf("history.get")];
        Assert.Equal(3, reads.Length);
        Assert.All(reads, read => Assert.Equal(["1", "7"], read.GetProperty("itemids").EnumerateArray().Select(id => id.GetString()).Order()));
    }

    [Fact]
    public async Task Current_is_one_snapshot_until_a_tick_changes_it()
    {
        using Watched w = Build(new ScriptedApi().On("item.get", $"[{Item()}]").On("history.get", Collected(1).Answer));
        await using IZabbixWatcher watcher = w.Server.Hosts["web-01"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)));
        await w.TicksAsync(1);

        IReadOnlyDictionary<(string Host, ZabbixItemKey Key), ZabbixValue> before = watcher.Current;
        Assert.Same(before, watcher.Current);                                                 // no FrozenDictionary rebuilt per read
        w.Time.Advance(TimeSpan.FromSeconds(30));
        await w.TicksAsync(2);

        Assert.NotSame(before, watcher.Current);
        Assert.Equal("2", watcher.Current.Single().Value.Value);
    }

    [Fact]
    public async Task Every_tick_is_a_root_span_tagged_with_the_watched_depth()
    {
        using var capture = new TelemetryCapture(ZabbixTelemetryNames.Tree, span => span.OperationName == ZabbixTreeTelemetry.WatcherTickSpan && span.GetTagItem(ZabbixTreeTelemetry.ScopeTag) is "item");
        using Watched w = Build(new ScriptedApi().On("item.get", $"[{Item()}]").On("history.get", "[]"));
        await using IZabbixWatcher watcher = w.Server.Hosts["web-01"].Items["cpu"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)));
        await w.TicksAsync(1);
        w.Time.Advance(TimeSpan.FromSeconds(30));
        await w.TicksAsync(2);

        await watcher.DisposeAsync();

        Assert.True(capture.Spans.Count >= 2);
        Assert.All(capture.Spans, span => Assert.Null(span.Parent));
        Assert.All(w.Polls.GetMeasurementSnapshot(), measurement => Assert.Equal("item", measurement.Tags[ZabbixTreeTelemetry.ScopeTag]));
    }
}
