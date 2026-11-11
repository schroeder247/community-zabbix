// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Text.Json;
using Community.Zabbix.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The tree and the watcher against a server whose item set is empty, shrinks between polls, or is watched twice, and a
/// history read cancelled mid-page. Time moves only on the injected clock; a wait is a safety net, never a pace.
/// </summary>
public sealed class AdversarialApiTreeTests
{
    private const long T0 = 1_700_000_000;
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);

    private sealed class Harness : IDisposable
    {
        public required ServiceProvider Provider { get; init; }

        public required FakeTimeProvider Time { get; init; }

        public required MetricCollector<long> Polls { get; init; }

        public ZabbixClient Server => Provider.GetRequiredService<ZabbixClient>();

        public async Task PolledAsync(int count)
        {
            using var safety = new CancellationTokenSource(Wait);
            await Polls.WaitForMeasurementsAsync(count, safety.Token);
        }

        public void Dispose()
        {
            Polls.Dispose();
            Provider.Dispose();
        }
    }

    private static Harness Build(ScriptedApi api)
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(T0 + 5));
        var services = new ServiceCollection();
        services.AddSingleton<TimeProvider>(time);
        services.AddZabbix(options => { options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"); options.ApiToken = "tok"; });
        services.AddHttpClient(ZabbixHttp.GetClientName()).ConfigurePrimaryHttpMessageHandler(() => api);
        ServiceProvider provider = services.BuildValidated();
        return new Harness
        {
            Provider = provider,
            Time = time,
            Polls = new MetricCollector<long>(provider.GetRequiredService<IMeterFactory>(), ZabbixTelemetryNames.Tree, ZabbixTreeTelemetry.WatcherPolls),
        };
    }

    private static string Item(string id, string key, string host = "web-01", long lastclock = T0, string lastvalue = "1")
        => $$"""{"itemid":"{{id}}","hostid":"10","key_":"{{key}}","name":"{{key}}","delay":"30s","value_type":"3","lastvalue":"{{lastvalue}}","lastclock":"{{lastclock.ToString(CultureInfo.InvariantCulture)}}","lastns":"0","hosts":[{"hostid":"10","host":"{{host}}"}]}""";

    [Fact]
    public async Task A_watch_over_a_host_with_no_items_polls_and_holds_nothing()
    {
        using ScriptedApi api = new ScriptedApi().On("apiinfo.version", "\"7.0.0\"").On("item.get", "[]").On("host.get", """[{"hostid":"10"}]""").On("history.get", "[]");
        using Harness harness = Build(api);
        await using IZabbixWatcher watcher = harness.Server.Hosts["empty-01"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)));

        await harness.PolledAsync(1);
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        await harness.PolledAsync(2);

        Assert.Empty(watcher.Current);
        Assert.Equal(0, watcher.FailedPolls);
        Assert.Empty(api.ParamsOf("history.get"));                                              // nothing to ask history for
    }

    [Fact]
    public async Task An_item_that_disappears_between_refreshes_leaves_the_current_view()
    {
        int refreshes = 0;
        using ScriptedApi api = new ScriptedApi().On("apiinfo.version", "\"7.0.0\"")
            .On("item.get", _ => Interlocked.Increment(ref refreshes) == 1 ? $"[{Item("1", "a")},{Item("2", "b")}]" : $"[{Item("1", "a")}]")
            .On("history.get", "[]");
        using Harness harness = Build(api);
        await using IZabbixWatcher watcher = harness.Server.Hosts["web-01"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)) { RefreshInterval = TimeSpan.FromSeconds(30) });

        await harness.PolledAsync(1);
        Assert.Equal(2, watcher.Current.Count);
        harness.Time.Advance(TimeSpan.FromSeconds(30));
        await harness.PolledAsync(2);

        Assert.Equal(["a"], watcher.Current.Keys.Select(key => key.Key.ToString()));
    }

    [Fact]
    public async Task Two_watchers_on_one_item_each_see_its_new_value()
    {
        string History(JsonElement parameters)
            => parameters.GetProperty("time_from").GetInt64() <= T0 + 10 ? $$"""[{"itemid":"1","clock":"{{T0 + 10}}","ns":"0","value":"7"}]""" : "[]";
        using ScriptedApi api = new ScriptedApi().On("apiinfo.version", "\"7.0.0\"").On("item.get", $"[{Item("1", "a")}]").On("history.get", History);
        using Harness harness = Build(api);
        await using IZabbixWatcher first = harness.Server.Hosts["web-01"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)));
        await using IZabbixWatcher second = harness.Server.Hosts["web-01"].Item("a").Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)));
        await using IAsyncEnumerator<ZabbixValue> one = first.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await using IAsyncEnumerator<ZabbixValue> two = second.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        ValueTask<bool> nextOne = one.MoveNextAsync();
        ValueTask<bool> nextTwo = two.MoveNextAsync();

        await harness.PolledAsync(2);
        harness.Time.Advance(TimeSpan.FromSeconds(30));

        Assert.True(await nextOne.AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.True(await nextTwo.AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken));
        Assert.Equal("7", one.Current.Value);
        Assert.Equal("7", two.Current.Value);
    }

    [Fact]
    public async Task A_history_read_cancelled_mid_page_is_cancellation_not_another_failure()
    {
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();
        using ScriptedApi api = new ScriptedApi().On("apiinfo.version", "\"7.0.0\"").On("item.get", $"[{Item("1", "a")}]")
            .On("history.get", _ =>
            {
                asked.TrySetResult();
                stop.Token.WaitHandle.WaitOne(Wait);                                             // the page is in flight when the caller stops
                return "[]";
            });
        using Harness harness = Build(api);

        Task<List<ZabbixValue>> reading = Task.Run(async () => await harness.Server.Hosts["web-01"].GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(T0), cancellationToken: stop.Token).ToListAsync(stop.Token), TestContext.Current.CancellationToken);
        await asked.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(Wait, TestContext.Current.CancellationToken));
    }
}
