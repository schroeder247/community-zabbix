// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using Community.Zabbix.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>A poll is an async stream on an injected clock: once at once, then every tick; a failed cycle is logged and skipped.</summary>
public sealed class PollTests
{
    [Fact]
    public async Task A_poll_collects_at_once_and_then_on_every_tick_of_the_injected_clock()
    {
        var time = new FakeTimeProvider();
        int cycle = 0;
        IAsyncEnumerable<int> poll = ZabbixPoll.Every(TimeSpan.FromSeconds(30), _ => new ValueTask<IEnumerable<int>>([++cycle]), time, NullLogger.Instance);
        await using IAsyncEnumerator<int> reading = poll.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await reading.MoveNextAsync());
        Assert.Equal(1, reading.Current);

        ValueTask<bool> next = reading.MoveNextAsync();
        Assert.False(next.IsCompleted);                                          // nothing until the clock moves
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True(await next.AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.Equal(2, reading.Current);
    }

    [Fact]
    public async Task A_failed_cycle_is_logged_and_the_cadence_continues()
    {
        var time = new FakeTimeProvider();
        var logs = new FakeLogCollector();
        int cycle = 0;
        IAsyncEnumerable<int> poll = ZabbixPoll.Every(TimeSpan.FromSeconds(1),
            _ => ++cycle == 1 ? throw new InvalidOperationException("probe down") : new ValueTask<IEnumerable<int>>([cycle]), time, new FakeLogger(logs));
        await using IAsyncEnumerator<int> reading = poll.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        // Cycle 1 throws synchronously inside MoveNextAsync, is logged, and the iterator is parked on the timer before
        // MoveNextAsync returns, so the advance below is observed (no sleep).
        ValueTask<bool> next = reading.MoveNextAsync();
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 3005);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(await next.AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.Equal(2, reading.Current);
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 3005);
    }

    [Fact]
    public async Task A_reader_that_falls_a_buffer_behind_loses_the_oldest_records_counted_and_logged()
    {
        // The poll collects on its own heartbeat into a bounded buffer; a reader further behind than the
        // buffer sheds the oldest (warned at the 1st, 10th, 100th drop, event 3006) and never paces the cadence.
        var time = new FakeTimeProvider();
        var logs = new FakeLogCollector();
        int over = 5;
        IAsyncEnumerable<int> poll = ZabbixPoll.Every(TimeSpan.FromSeconds(1),
            _ => new ValueTask<IEnumerable<int>>(Enumerable.Range(1, ZabbixPoll.DefaultMaxItems + over)), time, new FakeLogger(logs));
        await using IAsyncEnumerator<int> reading = poll.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await reading.MoveNextAsync());

        Assert.Equal(over + 1, reading.Current);                                     // the first five were shed for the rest
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 3006);
    }

    [Fact]
    public async Task A_poll_s_buffer_capacity_is_the_caller_s()
    {
        var time = new FakeTimeProvider();
        IAsyncEnumerable<int> poll = ZabbixPoll.Every(TimeSpan.FromSeconds(1), _ => new ValueTask<IEnumerable<int>>(Enumerable.Range(1, 10)), time, NullLogger.Instance, 3);
        await using IAsyncEnumerator<int> reading = poll.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await reading.MoveNextAsync());

        Assert.Equal(8, reading.Current);                                            // ten collected into three: 8, 9, 10 kept
        Assert.Throws<ArgumentOutOfRangeException>(() => ZabbixPoll.Every(TimeSpan.FromSeconds(1), _ => new ValueTask<IEnumerable<int>>([1]), TimeProvider.System, NullLogger.Instance, 0));
    }

    [Fact]
    public void A_poll_refuses_a_non_positive_interval_eagerly()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ZabbixPoll.Every<int>(TimeSpan.Zero, _ => new ValueTask<IEnumerable<int>>([]), TimeProvider.System, NullLogger.Instance));

    [Fact]
    public async Task A_cycle_that_cancels_itself_is_a_failed_cycle_and_the_cadence_continues()
    {
        // An HttpClient timeout throws TaskCanceledException; only the reader's token stops the poll.
        var time = new FakeTimeProvider();
        var logs = new FakeLogCollector();
        int cycle = 0;
        IAsyncEnumerable<int> poll = ZabbixPoll.Every(TimeSpan.FromSeconds(1),
            _ => ++cycle == 1 ? throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.") : new ValueTask<IEnumerable<int>>([cycle]), time, new FakeLogger(logs));
        await using IAsyncEnumerator<int> reading = poll.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        ValueTask<bool> next = reading.MoveNextAsync();
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 3005);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.True(await next.AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.Equal(2, reading.Current);
    }

    [Fact]
    public async Task Records_collected_and_never_read_when_the_reader_stops_are_counted_and_logged()
    {
        var time = new FakeTimeProvider();
        var logs = new FakeLogCollector();
        IAsyncEnumerable<int> poll = ZabbixPoll.Every(TimeSpan.FromSeconds(1), _ => new ValueTask<IEnumerable<int>>([1, 2, 3, 4, 5]), time, new FakeLogger(logs));
        IAsyncEnumerator<int> reading = poll.GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(await reading.MoveNextAsync());
        await reading.DisposeAsync();                                               // the reader stops with four records unread

        FakeLogRecord abandoned = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 3007);
        Assert.Equal(LogLevel.Warning, abandoned.Level);
        Assert.Contains("4 collected record(s)", abandoned.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_poll_takes_one_value_sync_or_async_as_well_as_a_batch()
    {
        var services = new ServiceCollection();
        services.AddZabbixPushAgent("edge").AddServer("zbx1")
            .AddPoll(TimeSpan.FromSeconds(1), () => new ZabbixValue("h", "a", 1L, ZabbixClock.UnixEpoch))
            .AddPoll(TimeSpan.FromSeconds(1), async ct => { await Task.Yield(); return new ZabbixValue("h", "b", 2L, ZabbixClock.UnixEpoch); })
            .AddPoll(TimeSpan.FromSeconds(1), _ => ValueTask.FromResult<IEnumerable<ZabbixValue>>([new ZabbixValue("h", "c", 3L, ZabbixClock.UnixEpoch)]));
        using ServiceProvider provider = services.BuildValidated();
        List<string> first = [];

        foreach (Func<IServiceProvider, IAsyncEnumerable<ZabbixValue>> source in provider.GetRequiredService<IOptionsMonitor<ZabbixPushAgentOptions>>().Get("edge").Sources)
        {
            await using IAsyncEnumerator<ZabbixValue> reading = source(provider).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            Assert.True(await reading.MoveNextAsync());
            first.Add($"{reading.Current.Key}={reading.Current.Value}");
        }

        Assert.Equal(["a=1", "b=2", "c=3"], first);
    }

    [Fact]
    public void A_numeric_value_is_written_as_Zabbix_parses_it_in_any_culture()
    {
        CultureInfo before = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            Assert.Equal("0.25", new ZabbixValue("h", "k", 0.25, ZabbixClock.UnixEpoch).Value);
            Assert.Equal("-1234567", new ZabbixValue("h", "k", -1_234_567L, ZabbixClock.UnixEpoch).Value);
            Assert.Equal("8", new ZabbixValue("h", "k", 8, ZabbixClock.UnixEpoch).Value);            // an int is a long, not a double
            Assert.Equal("1E+300", new ZabbixValue("h", "k", 1e300, ZabbixClock.UnixEpoch).Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = before;
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => new ZabbixValue("h", "k", double.NaN, ZabbixClock.UnixEpoch));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ZabbixValue("h", "k", double.PositiveInfinity, ZabbixClock.UnixEpoch));
    }

    [Fact]
    public void Now_is_the_injected_clocks_instant()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));

        Assert.Equal(new ZabbixClock(1_700_000_000, 0), ZabbixClock.Now(time));
    }
}
