// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using System.Globalization;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.Hosting;
using Community.Zabbix.Listening;
using Community.Zabbix.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// Every public stream of the libraries under a hostile consumer — one that never asks again after
/// the first value, one disposed mid-stream, two on one sequence, one cancelled between values, one abandoned without a
/// dispose. What a producer does then is pinned: bounded or lazy, stopped, no further server call.
/// </summary>
public sealed class AdversarialStreamTests
{
    private const long S = 1_700_000_000;
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(15);

    private const string Items = """
        [{"itemid":"1","hostid":"10","key_":"cpu","name":"CPU","delay":"1s","value_type":"0","lastvalue":"1","lastclock":"1700000000","lastns":"0","hosts":[{"hostid":"10","host":"web-01"}]}]
        """;

    private sealed record Tree(ServiceProvider Provider, ScriptedApi Api, FakeTimeProvider Time) : IDisposable
    {
        public ZabbixClient Server => Provider.GetRequiredService<ZabbixClient>();

        public int Calls(string method)
        {
            lock (Api.Calls) return Api.Calls.Count(call => call.Method == method);
        }

        public void Dispose() => Provider.Dispose();
    }

    /// <summary>A tree over a scripted API the container owns; <paramref name="script"/> scripts it.</summary>
    private static Tree Build(Func<ScriptedApi, ScriptedApi> script)
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(S + 5));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton(_ => script(new ScriptedApi()));
        services.AddLogging();
        services.AddZabbix(options => { options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"); options.ApiToken = "tok"; });
        services.AddHttpClient(ZabbixHttp.GetClientName()).ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<ScriptedApi>());
        ServiceProvider provider = services.BuildValidated();
        return new Tree(provider, provider.GetRequiredService<ScriptedApi>(), time);
    }

    /// <summary>More rows than one page, one per second, so a full read takes three <c>history.get</c> calls.</summary>
    private static Tree PagedHistory()
    {
        var history = new FakeHistory();
        for (int n = 0; n <= 5000; n++) history.Add("1", ZabbixItemValueType.Float, S + n, 0, n.ToString(CultureInfo.InvariantCulture));
        return Build(api => api.On("item.get", Items).On("history.get", history.Answer));
    }

    // ----- the pull plane (history, latest, the host scope) ---------------------------------------------------------------

    [Fact]
    public async Task A_history_reader_that_takes_one_value_and_is_disposed_costs_one_page_and_no_more()
    {
        using Tree tree = PagedHistory();
        IAsyncEnumerable<ZabbixValue> history = tree.Server.Hosts["web-01"].GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(S), cancellationToken: TestContext.Current.CancellationToken);

        await using (IAsyncEnumerator<ZabbixValue> reader = history.GetAsyncEnumerator(TestContext.Current.CancellationToken))
            Assert.True(await reader.MoveNextAsync());

        Assert.Equal(1, tree.Calls("history.get"));
    }

    [Fact]
    public async Task A_history_reader_that_never_asks_again_after_the_first_value_holds_no_server_call_open()
    {
        using Tree tree = PagedHistory();
        IAsyncEnumerator<ZabbixValue> reader = tree.Server.Hosts["web-01"].GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(S), cancellationToken: TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await reader.MoveNextAsync());                                             // and then the consumer walks away

        Assert.Equal(1, tree.Calls("history.get"));                                         // lazy: the next page waits for a MoveNextAsync
        await reader.DisposeAsync();
    }

    [Fact]
    public async Task Cancelling_a_history_read_between_values_ends_it_with_cancellation_and_no_further_server_call()
    {
        using Tree tree = PagedHistory();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int read = 0;

        Exception? ended = await Record.ExceptionAsync(async () =>
        {
            await foreach (ZabbixValue _ in tree.Server.Hosts["web-01"].GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(S), cancellationToken: TestContext.Current.CancellationToken).WithCancellation(cancel.Token))
                if (++read == 1) await cancel.CancelAsync();
        });

        Assert.IsAssignableFrom<OperationCanceledException>(ended);
        Assert.Equal(1, tree.Calls("history.get"));                                         // the page in hand may drain; no new page is asked for
    }

    [Fact]
    public async Task Two_enumerations_of_one_history_sequence_are_two_independent_reads()
    {
        using Tree tree = PagedHistory();
        IAsyncEnumerable<ZabbixValue> history = tree.Server.Hosts["web-01"].GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(S), cancellationToken: TestContext.Current.CancellationToken);

        List<ZabbixValue>[] both = await Task.WhenAll(history.ToListAsync(TestContext.Current.CancellationToken).AsTask(), history.ToListAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Equal(5001, both[0].Count);
        Assert.Equal(both[0].Select(value => value.Value), both[1].Select(value => value.Value));
        Assert.Equal(2, tree.Calls("item.get"));                                            // cold: each enumeration is its own read
    }

    [Fact]
    public async Task A_host_scope_enumerated_twice_reads_the_server_twice_and_a_disposed_one_stops()
    {
        using Tree tree = Build(api => api.On("host.get", """[{"hostid":"10","host":"web-01","name":"web","status":"0"},{"hostid":"11","host":"db-01","name":"db","status":"0"}]"""));
        ZabbixHostScope hosts = tree.Server.AllHosts;

        await using (IAsyncEnumerator<ZabbixHostNode> first = hosts.GetAsyncEnumerator(TestContext.Current.CancellationToken))
            Assert.True(await first.MoveNextAsync());
        int all = await hosts.CountAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, all);
        Assert.Equal(2, tree.Calls("host.get"));
    }

    // ----- the watcher -----------------------------------------------------------------------------------------------------

    /// <summary>Waits until the watcher has ended <paramref name="ticks"/> ticks, on its own poll metrics (one measurement per tick).</summary>
    private static async Task TicksAsync(MetricCollector<long> polls, int ticks)
        => await polls.WaitForMeasurementsAsync(ticks, Failsafe);

    private static (Tree Tree, FakeHistory History, MetricCollector<long> Polls) Watched()
    {
        FakeHistory history = new FakeHistory().Add("1", ZabbixItemValueType.Float, S, 0, "seed");      // the item's lastvalue: already seen
        Tree tree = Build(api => api.On("item.get", Items).On("history.get", history.Answer));
        return (tree, history, new MetricCollector<long>(tree.Provider.GetRequiredService<IMeterFactory>(), ZabbixTelemetryNames.Tree, ZabbixTreeTelemetry.WatcherPolls));
    }

    /// <summary>Values the item collected during one more tick: <paramref name="count"/> of them, numbered on from <paramref name="after"/>.</summary>
    private static void Collect(FakeHistory history, int after, int count)
    {
        for (int n = after + 1; n <= after + count; n++) history.Add("1", ZabbixItemValueType.Float, S + n, 0, n.ToString(CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task An_abandoned_watcher_reader_holds_at_most_its_capacity_and_never_delays_the_poll()
    {
        (Tree tree, FakeHistory history, MetricCollector<long> polls) = Watched();
        using (tree)
        using (polls)
        {
            await using IZabbixWatcher watcher = tree.Server.Hosts["web-01"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)) { MaxItems = 4 });
            await TicksAsync(polls, 1);
            IAsyncEnumerator<ZabbixValue> abandoned = watcher.GetAsyncEnumerator(TestContext.Current.CancellationToken);   // never read, never disposed

            for (int tick = 2; tick <= 4; tick++)
            {
                Collect(history, (tick - 2) * 30, 30);
                tree.Time.Advance(TimeSpan.FromSeconds(30));
                await TicksAsync(polls, tick);
            }

            Assert.Equal(4, watcher.SucceededPolls);
            Assert.Equal(1, ((ZabbixWatcher)watcher).Readers);                               // held until disposed: the watcher cannot know it was abandoned
            Assert.Equal(90 - 4, watcher.DroppedCount);                                            // everything past its four is shed, counted
            await abandoned.DisposeAsync();
            Assert.Equal(0, ((ZabbixWatcher)watcher).Readers);
        }
    }

    [Fact]
    public async Task A_watcher_reader_cancelled_between_values_throws_on_the_next_move_and_leaves_when_disposed()
    {
        (Tree tree, FakeHistory history, MetricCollector<long> polls) = Watched();
        using (tree)
        using (polls)
        {
            await using IZabbixWatcher watcher = tree.Server.Hosts["web-01"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)));
            await TicksAsync(polls, 1);
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            IAsyncEnumerator<ZabbixValue> reader = watcher.GetAsyncEnumerator(cancel.Token);
            Collect(history, 0, 3);
            tree.Time.Advance(TimeSpan.FromSeconds(30));
            await TicksAsync(polls, 2);
            Assert.True(await reader.MoveNextAsync());
            await cancel.CancelAsync();

            Exception? next = await Record.ExceptionAsync(async () => await reader.MoveNextAsync());
            await reader.DisposeAsync();

            Assert.IsAssignableFrom<OperationCanceledException>(next);                       // two values were buffered; cancellation still wins
            Assert.Equal(0, ((ZabbixWatcher)watcher).Readers);
        }
    }

    [Fact]
    public async Task Disposing_one_watcher_reader_mid_stream_leaves_the_other_reading()
    {
        (Tree tree, FakeHistory history, MetricCollector<long> polls) = Watched();
        using (tree)
        using (polls)
        {
            await using IZabbixWatcher watcher = tree.Server.Hosts["web-01"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)));
            await TicksAsync(polls, 1);
            IAsyncEnumerator<ZabbixValue> gone = watcher.GetAsyncEnumerator(TestContext.Current.CancellationToken);
            await using IAsyncEnumerator<ZabbixValue> staying = watcher.GetAsyncEnumerator(TestContext.Current.CancellationToken);
            Collect(history, 0, 3);
            tree.Time.Advance(TimeSpan.FromSeconds(30));
            await TicksAsync(polls, 2);
            Assert.True(await gone.MoveNextAsync());

            await gone.DisposeAsync();
            Collect(history, 3, 3);
            tree.Time.Advance(TimeSpan.FromSeconds(30));
            await TicksAsync(polls, 3);

            var read = new List<string>();
            for (int n = 0; n < 6; n++) { Assert.True(await staying.MoveNextAsync()); read.Add(staying.Current.Value); }
            Assert.Equal(["1", "2", "3", "4", "5", "6"], read);
            Assert.Equal(1, ((ZabbixWatcher)watcher).Readers);
        }
    }

    // ----- the poll --------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_poll_whose_consumer_is_late_still_collects_on_every_tick()
    {
        // A pure-pull chain makes a poller lazy, and a slow reader would stretch the cadence: the poll collects on its
        // own heartbeat whatever its consumer does, as the watcher does.
        var time = new WatchedClock();
        int collected = 0;
        IAsyncEnumerable<int> poll = ZabbixPoll.Every(TimeSpan.FromSeconds(10), _ => ValueTask.FromResult<IEnumerable<int>>([Interlocked.Increment(ref collected)]), time, NullLogger.Instance);
        await using IAsyncEnumerator<int> reader = poll.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await reader.MoveNextAsync());                                            // the first cycle, at once
        Task<bool> waiting = reader.MoveNextAsync().AsTask();                                 // the reader waits on the timer
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(10));
        Assert.True(await waiting.WaitAsync(Failsafe, TestContext.Current.CancellationToken));   // cycle 2; now the reader is busy for 30 s

        time.Advance(TimeSpan.FromSeconds(30));                                               // three ticks pass while it is busy

        Assert.Equal(5, Volatile.Read(ref collected));                                        // 1 + 1 + 3: the cadence did not wait for the reader
    }

    [Fact]
    public async Task A_disposed_poll_collects_nothing_more_however_far_the_clock_moves()
    {
        var time = new WatchedClock();
        int collected = 0;
        IAsyncEnumerable<int> poll = ZabbixPoll.Every(TimeSpan.FromSeconds(10), _ => ValueTask.FromResult<IEnumerable<int>>([Interlocked.Increment(ref collected)]), time, NullLogger.Instance);
        IAsyncEnumerator<int> reader = poll.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await reader.MoveNextAsync());

        await reader.DisposeAsync();
        time.Advance(TimeSpan.FromMinutes(10));

        Assert.Equal(1, Volatile.Read(ref collected));
    }

    // ----- the store's stream and the trapper source's edge ---------------------------------------------------------------

    [Theory]
    [InlineData("memory")]
    [InlineData("durable")]
    public async Task A_store_stream_disposed_after_one_record_leaves_every_other_record_in_the_store(string kind)
    {
        await using ZabbixStore<int> store = kind == "memory" ? new ZabbixMemoryStore<int>() : new AdversarialRowStore<int>();
        for (int i = 1; i <= 5; i++) store.TryWrite(i);

        await using (IAsyncEnumerator<int> reader = store.GetAsyncEnumerator(TestContext.Current.CancellationToken))
            Assert.True(await reader.MoveNextAsync());

        store.Complete();
        Assert.Equal([2, 3, 4, 5], await store.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_trapper_source_s_edge_has_one_reader_and_a_second_is_refused()
    {
        await using var source = new ZabbixTrapperSource(new ZabbixListenerOptions { Address = "127.0.0.1", Port = 0, AllowedPeers = { "127.0.0.1" } }, ListenerTests.Transport());
        _ = source.ReadAllAsync(TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(() => source.ReadAllAsync(TestContext.Current.CancellationToken));
    }
}
