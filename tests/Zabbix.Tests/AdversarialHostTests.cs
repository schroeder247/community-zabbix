// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Community.Zabbix.Hosting;
using Community.Zabbix.Listening;
using Community.Zabbix.Proxy;
using Community.Zabbix.Storage;
using Community.Zabbix.Storage.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The hosted services under a hostile server and a hostile clock — a configuration revision that
/// goes backwards, colliding item ids, uploads disabled on every other cycle, refused and cut pages, a wall clock stepped
/// back and forward, a stop in the middle of a send, a start run twice. Every wait is on a frame, a timer created or a
/// log event; a time bound is a failsafe only.
/// </summary>
public sealed class AdversarialHostTests
{
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(15);
    private const string Success = """{"response":"success","info":"processed: 1; failed: 0; total: 1; seconds spent: 0.000100"}""";

    private static IHostedLifecycleService Hosted(ServiceProvider provider) => (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();

    private static async Task StopAsync(IHostedLifecycleService hosted)
    {
        await hosted.StoppingAsync(default);
        await hosted.StopAsync(default);
        await hosted.StoppedAsync(default);
    }

    // ----- configuration revisions and colliding ids --------------------------------------------------------------------

    private const string FullSync = """
        {"full_sync":1,"config_revision":10,"data":{"hosts":{"fields":["hostid","host","status"],"data":[[10,"web-01",0]]},
         "items":{"fields":["itemid","type","hostid","key_","status","trapper_hosts","allow_traps"],"data":[[100,2,10,"cpu",0,"",0]]},
         "globalmacro":{"fields":["globalmacroid","macro","value"],"data":[[1,"{$TRAPPERS}","10.0.0.1"]]}}}
        """;

    [Fact]
    public async Task A_config_revision_older_than_the_one_held_is_taken_as_the_server_s_word_as_upstream_takes_it()
    {
        // proxyconfigwrite.c: the proxy stores whatever config_revision the server sent (zbx_dc_set_upstream_revision);
        // the server owns the revision and may restart below it. The proxy therefore follows, and asks with it next time.
        string[] replies = [FullSync, """{"config_revision":4,"data":{"hosts":{"fields":["hostid","host","status"],"data":[[11,"web-02",0]]}}}""", "{}"];
        var sent = new List<JsonElement>();
        int turn = 0;
        ZabbixExchangeCallback exchange = (request, _) =>
        {
            sent.Add(JsonDocument.Parse(request).RootElement.Clone());
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(replies[turn++]));
        };
        var uplink = new ZabbixProxyUplink(exchange, "adv-proxy");
        ZabbixProxyConfig held = await uplink.GetConfigAsync(ZabbixProxyConfig.Empty, TestContext.Current.CancellationToken);

        ZabbixProxyConfig older = await uplink.GetConfigAsync(held, TestContext.Current.CancellationToken);
        await uplink.GetConfigAsync(older, TestContext.Current.CancellationToken);

        Assert.Equal(4, older.ConfigRevision);
        Assert.Equal(2, older.Hosts.Count);                                                  // the delta merged over what was held
        Assert.Equal(4, sent[2].GetProperty("config_revision").GetInt64());
    }

    [Fact]
    public void Removed_macro_host_id_zero_removes_the_global_macros_as_upstream_does()
    {
        // proxyconfigwrite.c (7.0 2226, 8.0 2431): a 0 in del_macro_hostids (ZBX_PROTO_TAG_REMOVED_MACRO_HOSTIDS, zbxjson.h
        // 7.0:201, 8.0:205) means "the global macros are gone"
        // (delete_globalmacros); a stale {$TRAPPERS} would keep admitting or refusing trappers by a macro that no longer exists.
        using JsonDocument full = JsonDocument.Parse(FullSync);
        ZabbixProxyConfig held = ZabbixProxyConfig.Empty.Merge(full.RootElement);
        using JsonDocument delta = JsonDocument.Parse("""{"data":{},"del_macro_hostids":[0],"config_revision":11}""");

        ZabbixProxyConfig merged = held.Merge(delta.RootElement);

        Assert.Single(held.GlobalMacros);
        Assert.Empty(merged.GlobalMacros);
    }

    [Fact]
    public void Two_item_rows_with_one_id_in_a_config_keep_the_last_row_as_the_item()
    {
        using JsonDocument full = JsonDocument.Parse("""
            {"full_sync":1,"config_revision":3,"data":{"hosts":{"fields":["hostid","host","status"],"data":[[10,"web-01",0]]},
             "items":{"fields":["itemid","type","hostid","key_","status","trapper_hosts","allow_traps"],"data":[[100,2,10,"cpu",0,"",0],[100,2,10,"mem",0,"",0]]}}}
            """);

        ZabbixProxyConfig config = ZabbixProxyConfig.Empty.Merge(full.RootElement);

        ZabbixProxyConfigItem item = Assert.Single(config.Items.Values);
        Assert.Equal("mem", item.Key);
        Assert.Equal(item, Assert.Single(config.ItemsByKey.Values));                      // no stale (web-01, cpu) mapping to item 100
    }

    [Fact]
    public async Task An_active_check_list_that_repeats_an_item_id_is_still_taken_and_the_others_are_collected()
    {
        await using AdversarialScriptedServer server = await AdversarialScriptedServer.StartAsync((kind, _, _) => kind == "active checks"
            ? """{"response":"success","data":[{"itemid":"100","key":"adv.one","delay":"1h"},{"itemid":"100","key":"adv.one","delay":"1h"},{"itemid":"101","key":"adv.two","delay":"1h"}],"config_revision":"2"}"""
            : Success);
        using ServiceProvider provider = ActiveAgent(server.Node, new FakeTimeProvider(), o => { });
        IHostedLifecycleService hosted = Hosted(provider);

        await hosted.StartingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, provider.GetRequiredKeyedService<ZabbixActiveAgent>("adv").Sessions[0].Assignments.Count);
        await StopAsync(hosted);
    }

    [Fact]
    public async Task An_active_check_list_with_an_older_revision_is_taken_and_asked_for_with_that_revision_next()
    {
        // active_checks.c parse_list_of_checks: the agent keeps the server's config_revision as sent.
        await using AdversarialScriptedServer server = await AdversarialScriptedServer.StartAsync((kind, nth, _) => kind != "active checks" ? Success
            : nth == 0 ? """{"response":"success","data":[{"itemid":"100","key":"adv.one","delay":"1h"}],"config_revision":"7"}"""
            : """{"response":"success","data":[{"itemid":"101","key":"adv.two","delay":"1h"}],"config_revision":"3"}""");
        var time = new WatchedClock();
        using ServiceProvider provider = ActiveAgent(server.Node, time, o => o.RefreshInterval = TimeSpan.FromMinutes(5));
        IHostedLifecycleService hosted = Hosted(provider);
        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await server.NextAsync("active checks");

        await time.WaitForTimerAsync(TimeSpan.FromMinutes(5), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(5));
        await server.NextAsync("active checks");
        time.Advance(TimeSpan.FromMinutes(5));                                                // the periodic timer is armed already
        JsonElement third = await server.NextAsync("active checks").WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        Assert.Equal(3, third.GetProperty("config_revision").GetInt64());
        Assert.Equal([101L], provider.GetRequiredKeyedService<ZabbixActiveAgent>("adv").Sessions[0].Assignments.Keys);
        await StopAsync(hosted);
    }

    // ----- the clock --------------------------------------------------------------------------------------------------

    private static ServiceProvider ActiveAgent(ZabbixServerNode node, TimeProvider time, Action<ZabbixActiveAgentOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(time);
        services.AddLogging();
        services.AddMetrics();
        services.AddZabbixActiveAgent("adv").AddServer(new DnsEndPoint(node.Host, node.Port)).AddItem("adv.one", _ => "1").AddItem("adv.two", _ => "2")
            .Configure(o =>
            {
                o.Hostname = "adv-01";
                o.Connection.Timeout = TimeSpan.FromDays(1);
                o.RefreshInterval = TimeSpan.FromDays(1);
                o.HeartbeatInterval = TimeSpan.Zero;
                o.Send.BatchSize = 1;
                o.Send.FlushInterval = TimeSpan.FromMinutes(7);
                o.CheckTimeout = TimeSpan.FromMinutes(11);
                configure(o);
            });
        return services.BuildValidated();
    }

    private static readonly TimeSpan[] NotACheck = [TimeSpan.FromDays(1), TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(11), Timeout.InfiniteTimeSpan];

    /// <summary>The next timer the check loop creates (its wait for the next due check), skipping the other loops' timers.</summary>
    private static async Task<TimeSpan> NextCheckWaitAsync(AdversarialSteppedClock clock)
    {
        await foreach (TimeSpan due in clock.Created.ReadAllAsync(TestContext.Current.CancellationToken))
            if (!NotACheck.Contains(due)) return due;
        throw new InvalidOperationException("the clock stopped");
    }

    private static async Task<(AdversarialScriptedServer Server, ServiceProvider Provider, IHostedLifecycleService Hosted, AdversarialSteppedClock Clock)> ActiveAgentOnSteppedClockAsync()
    {
        AdversarialScriptedServer server = await AdversarialScriptedServer.StartAsync((kind, _, _) => kind == "active checks"
            ? """{"response":"success","data":[{"itemid":"100","key":"adv.one","delay":"1s"}],"config_revision":"1"}"""
            : Success);
        var clock = new AdversarialSteppedClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        ServiceProvider provider = ActiveAgent(server.Node, clock, o => { });
        IHostedLifecycleService hosted = Hosted(provider);
        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        await server.NextAsync("agent data");                                               // the first collection went out
        Assert.Equal(TimeSpan.FromSeconds(1), await NextCheckWaitAsync(clock));              // and the loop waits a second for the next
        return (server, provider, hosted, clock);
    }

    [Fact]
    public async Task A_wall_clock_stepped_back_an_hour_does_not_stop_collection_for_an_hour()
    {
        // active_checks.c 2062: "the system time has been pushed back, adjusting active check schedule" (update_schedule).
        (AdversarialScriptedServer server, ServiceProvider provider, IHostedLifecycleService hosted, AdversarialSteppedClock clock) = await ActiveAgentOnSteppedClockAsync();
        await using (server)
        using (provider)
        {
            clock.Step(TimeSpan.FromHours(-1));
            clock.Advance(TimeSpan.FromSeconds(1));

            TimeSpan next = await NextCheckWaitAsync(clock).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.True(next <= TimeSpan.FromSeconds(1), $"the next check waits {next} after the clock stepped back an hour");
            await StopAsync(hosted);
        }
    }

    [Fact]
    public async Task A_wall_clock_jumped_a_day_forward_collects_once_and_keeps_the_one_second_cadence_without_spinning()
    {
        (AdversarialScriptedServer server, ServiceProvider provider, IHostedLifecycleService hosted, AdversarialSteppedClock clock) = await ActiveAgentOnSteppedClockAsync();
        await using (server)
        using (provider)
        {
            clock.Step(TimeSpan.FromDays(1));
            clock.Advance(TimeSpan.FromSeconds(1));

            TimeSpan next = await NextCheckWaitAsync(clock).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            await server.NextAsync("agent data").WaitAsync(Failsafe, TestContext.Current.CancellationToken);
            Assert.InRange(next, TimeSpan.FromTicks(1), TimeSpan.FromSeconds(1));             // one second, not zero (a spin), not a day
            Assert.Equal(2, server.Seen("agent data"));                                      // one catch-up collection, not a burst
            await StopAsync(hosted);
        }
    }

    [Fact]
    public async Task A_health_check_reports_a_dead_destination_even_after_the_wall_clock_stepped_back()
    {
        var clock = new AdversarialSteppedClock(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var status = new ZabbixAgentStatus("adv");
        await using var store = new ZabbixMemoryStore<ZabbixValue>();
        status.Attach(store);
        status.Report(new ZabbixSendProgress(ZabbixSendOutcome.Delivered, 1, clock.GetUtcNow()));
        store.TryWrite(new ZabbixValue("h", "k", "1", clock.GetUtcNow()));
        var check = new ZabbixAgentHealthCheck(status, clock) { StaleAfter = TimeSpan.FromMinutes(5) };
        clock.Step(TimeSpan.FromHours(-1));                                                 // NTP steps the wall clock back
        clock.Advance(TimeSpan.FromMinutes(10));                                            // ten real minutes with nothing delivered

        HealthCheckResult result = await check.CheckHealthAsync(new HealthCheckContext(), TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    // ----- a hostile server: uploads disabled every other cycle, refused and cut pages ----------------------------------

    private sealed record Proxy(ServiceProvider Provider, ZabbixActiveProxy Host) : IAsyncDisposable
    {
        public async Task<ZabbixSenderResponse> SendAsync(params ZabbixValue[] values)
        {
            var bound = (IPEndPoint)Host.Trapper!.LocalEndPoint!;
            using ZabbixSender sender = new ZabbixSender(bound.Address.ToString(), bound.Port);
            return await sender.SendAsync(values, TestContext.Current.CancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync(Hosted(Provider));
            await Provider.DisposeAsync();
        }
    }

    private static async Task<Proxy> ProxyAsync(ZabbixServerNode server, Action<ZabbixProxyOptions>? configure = null, Action<IServiceCollection>? services = null, Action<IZabbixActiveProxyBuilder>? compose = null, bool start = true)
    {
        var collection = new ServiceCollection();
        collection.AddLogging();
        collection.AddMetrics();
        services?.Invoke(collection);
        IZabbixActiveProxyBuilder proxy = collection.AddZabbixActiveProxy("adv-proxy")
            .AddServer(server.ToDnsEndPoint())
            .AddTrapper(0, o => o.Address = "127.0.0.1")
            .AllowPeer(System.Net.IPAddress.Loopback)
            .Configure(o => { o.DataInterval = TimeSpan.FromMilliseconds(20); o.ConfigInterval = TimeSpan.FromMinutes(1); configure?.Invoke(o); });
        compose?.Invoke(proxy);
        ServiceProvider provider = collection.BuildValidated();
        if (start)
        {
            await Hosted(provider).StartingAsync(default);
            await Hosted(provider).StartAsync(default);
        }
        return new Proxy(provider, provider.GetRequiredKeyedService<ZabbixActiveProxy>("adv-proxy"));
    }

    private static async Task<FakeZabbixServer> ServerAsync()
    {
        var server = new FakeZabbixServer();
        server.Configure("[10,\"web-01\",0]", "[100,2,10,\"cpu\",0,\"\",0]");
        await server.StartAsync();
        return server;
    }

    private static ZabbixValue[] Values(int from, int count)
        => [.. Enumerable.Range(from, count).Select(i => new ZabbixValue("web-01", "cpu", i.ToString(CultureInfo.InvariantCulture), DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + i)))];

    /// <summary>Reads the server's frames, calling <paramref name="onFrame"/> on each, until it has stored <paramref name="count"/> values.</summary>
    private static async Task UntilStoredAsync(FakeZabbixServer server, int count, Action<JsonElement>? onFrame = null)
    {
        using var failsafe = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        failsafe.CancelAfter(Failsafe);
        int frames = 0;
        try
        {
            await foreach (JsonElement frame in server.Frames.Reader.ReadAllAsync(failsafe.Token))
            {
                frames++;
                onFrame?.Invoke(frame);
                lock (server.Stored) if (server.Stored.Count >= count) return;
            }
        }
        catch (OperationCanceledException) when (failsafe.IsCancellationRequested && !TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            int stored;
            lock (server.Stored) stored = server.Stored.Count;
            throw new TimeoutException($"{stored} of {count} values stored within {Failsafe}; {frames} frames seen");
        }
    }

    [Fact]
    public async Task Uploads_disabled_on_every_other_cycle_hold_the_data_and_deliver_every_value_exactly_once()
    {
        await using FakeZabbixServer server = await ServerAsync();
        await using Proxy proxy = await ProxyAsync(server.Node, o => o.PageSize = 3);
        int flips = 0;
        Task<ZabbixSenderResponse>? second = null;

        // The flip happens as each proxy data frame is answered (not from the frame stream, which is published after the
        // reply), and it is installed before the first value is trapped: the proxy uploads at once, so a hook installed
        // after the send can find the whole first batch already delivered on a loaded runner, after which an idle proxy
        // sends no frame and the second batch is never triggered.
        server.OnProxyData = _ =>
        {
            if (++flips < 30) server.UploadDisabled = !server.UploadDisabled;               // flapping: disabled, enabled, disabled, …
            else server.UploadDisabled = false;
            if (flips == 5) second = proxy.SendAsync(Values(10, 10));
        };
        await proxy.SendAsync(Values(0, 10));
        await UntilStoredAsync(server, 20);
        Assert.NotNull(second);
        Assert.Equal(0, (await second).Failed);

        List<string> stored;
        lock (server.Stored) stored = [.. server.Stored.Select(value => value.Value)];
        Assert.Equal(Enumerable.Range(0, 20).Select(i => i.ToString(CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal), stored.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Refused_and_cut_pages_interleaved_deliver_every_value_exactly_once_with_the_same_ids()
    {
        await using FakeZabbixServer server = await ServerAsync();
        server.RefusePages = 2;
        server.DropPages = 2;
        await using Proxy proxy = await ProxyAsync(server.Node, o => o.PageSize = 3);
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        var moved = new List<string>();

        await proxy.SendAsync(Values(0, 10));
        await UntilStoredAsync(server, 10, frame =>
        {
            if (!frame.TryGetProperty("history data", out JsonElement rows)) return;
            foreach (JsonElement row in rows.EnumerateArray())
            {
                string value = row.GetProperty("value").GetString()!;
                long id = row.GetProperty("id").GetInt64();
                if (ids.TryGetValue(value, out long first) && first != id) moved.Add(value);   // a re-send must carry the same id
                ids[value] = id;
            }
        });

        Assert.Empty(moved);
        lock (server.Stored) Assert.Equal(10, server.Stored.Select(value => value.Value).Distinct(StringComparer.Ordinal).Count());
    }

    // ----- stop and start ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_stop_while_the_server_holds_an_upload_returns_within_the_drain_window_and_the_durable_store_keeps_the_page()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        string file = Path.Combine(Path.GetTempPath(), $"zbx-adv-stop-{Guid.NewGuid():N}.db");
        try
        {
            await using AdversarialScriptedServer server = await AdversarialScriptedServer.StartAsync((kind, _, frame) => kind switch
            {
                "proxy config" => Task.FromResult(FullSync),
                _ when frame.TryGetProperty("history data", out JsonElement _) => release.Task,                  // the server never answers the page
                _ => Task.FromResult("""{"upload":"enabled","response":"success"}"""),
            });
            try
            {
                await using (Proxy proxy = await ProxyAsync(server.Node, o => o.DrainTimeout = TimeSpan.FromMilliseconds(200),
                    compose: builder => builder.UseStore((policy, services) => new ZabbixEntityFrameworkStore<ZabbixValue>(o => o.UseSqlite($"Data Source={file};Pooling=False"), policy, services))))
                {
                    await proxy.SendAsync(Values(0, 3));
                    JsonElement page;
                    do page = await server.NextAsync("proxy data"); while (!page.TryGetProperty("history data", out _));

                    Task stopping = StopAsync(Hosted(proxy.Provider));

                    await stopping.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
                }

                await using var reopened = new ZabbixEntityFrameworkStore<ZabbixValue>(o => o.UseSqlite($"Data Source={file};Pooling=False"), services: new ZabbixStoreServices { Name = "adv-proxy" });
                reopened.TryPeek(out _);                                                   // open the table
                await reopened.FlushAsync();
                Assert.Equal(3, reopened.Count);
            }
            finally
            {
                release.TrySetResult("""{"upload":"enabled","response":"success"}""");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public async Task A_stop_while_the_server_holds_agent_data_returns_within_the_drain_window_and_counts_the_batch_abandoned()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using AdversarialScriptedServer server = await AdversarialScriptedServer.StartAsync((kind, _, _) => kind == "active checks"
            ? Task.FromResult("""{"response":"success","data":[{"itemid":"100","key":"adv.one","delay":"1h"}],"config_revision":"1"}""")
            : release.Task);                                                                   // agent data is never answered
        try
        {
            using ServiceProvider provider = ActiveAgent(server.Node, TimeProvider.System, o => { o.Send.DrainTimeout = TimeSpan.FromMilliseconds(200); o.Connection.Timeout = TimeSpan.FromMinutes(5); });
            IHostedLifecycleService hosted = Hosted(provider);
            await hosted.StartingAsync(TestContext.Current.CancellationToken);
            await hosted.StartAsync(TestContext.Current.CancellationToken);
            await server.NextAsync("agent data");

            await StopAsync(hosted).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.Equal(1, provider.GetRequiredKeyedService<ZabbixActiveAgent>("adv").Sessions[0].Status.Abandoned);   // counted, not silent
        }
        finally
        {
            release.TrySetResult(Success);
        }
    }

    [Fact]
    public async Task Starting_a_proxy_twice_runs_one_configuration_loop_not_two()
    {
        await using FakeZabbixServer server = await ServerAsync();
        var clock = new WatchedClock();
        await using Proxy proxy = await ProxyAsync(server.Node, o =>
        {
            o.ConfigInterval = TimeSpan.FromMinutes(7);
            o.DataInterval = TimeSpan.FromSeconds(3);
            o.Connection.Timeout = TimeSpan.FromDays(1);
        }, services: s => s.AddSingleton<TimeProvider>(clock));

        await Hosted(proxy.Provider).StartAsync(TestContext.Current.CancellationToken);     // a host, or a caller, runs StartAsync again

        var created = new List<TimeSpan>();
        while (clock.Created.TryRead(out TimeSpan due)) created.Add(due);
        Assert.Single(created, due => due == TimeSpan.FromMinutes(7));
    }

    [Fact]
    public async Task Starting_an_agent_twice_reads_each_source_once()
    {
        var (port, _) = MockTrapper.Collect(int.MaxValue);
        int enumerations = 0;
        async IAsyncEnumerable<ZabbixValue> Source([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token = default)
        {
            Interlocked.Increment(ref enumerations);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            yield break;
        }
        var services = new ServiceCollection();
        services.AddZabbixPushAgent("adv").AddSource(_ => Source()).AddServer(new DnsEndPoint("127.0.0.1", port));
        using ServiceProvider provider = services.BuildValidated();
        IHostedLifecycleService hosted = Hosted(provider);
        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        await hosted.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, Volatile.Read(ref enumerations));
        await StopAsync(hosted);
    }

    [Fact]
    public async Task Starting_an_active_agent_twice_runs_one_refresh_loop_not_two()
    {
        await using AdversarialScriptedServer server = await AdversarialScriptedServer.StartAsync((kind, _, _) => kind == "active checks"
            ? """{"response":"success","data":[],"config_revision":"1"}""" : Success);
        var clock = new WatchedClock();
        using ServiceProvider provider = ActiveAgent(server.Node, clock, o => o.RefreshInterval = TimeSpan.FromMinutes(5));
        IHostedLifecycleService hosted = Hosted(provider);
        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        await hosted.StartAsync(TestContext.Current.CancellationToken);

        await hosted.StartAsync(TestContext.Current.CancellationToken);

        var created = new List<TimeSpan>();
        while (clock.Created.TryRead(out TimeSpan due)) created.Add(due);
        Assert.Single(created, due => due == TimeSpan.FromMinutes(5));
        await StopAsync(hosted);
    }

    [Fact]
    public async Task Values_a_trapper_acknowledged_are_delivered_when_the_host_stops_while_the_agent_is_busy()
    {
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        static string Taken(JsonElement frame) => MockTrapper.Success(frame.GetProperty("data").GetArrayLength(), 0);
        await using AdversarialScriptedServer destination = await AdversarialScriptedServer.StartAsync((_, nth, frame) => nth == 0 ? release.Task : Task.FromResult(Taken(frame)));
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddZabbixPushAgent("adv")
            .AddTrapper(0, o => { o.Address = "127.0.0.1"; o.AllowedPeers.Add("127.0.0.1"); })
            .AddServer(new DnsEndPoint(destination.Node.Host, destination.Node.Port))
            .UseStore((_, _) => new AdversarialRowStore<ZabbixValue>(new ZabbixStorePolicy<ZabbixValue> { MaxItems = 1, Overflow = System.Threading.Channels.BoundedChannelFullMode.Wait }))
            .Configure(o => { o.Send.BatchSize = 1; o.Send.DrainTimeout = TimeSpan.FromSeconds(10); });
        using IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var bound = (IPEndPoint)host.Services.GetRequiredKeyedService<ZabbixTrapperSource>(new ZabbixTrapperKey("adv")).Listener.LocalEndPoint!;
        using ZabbixSender sender = new ZabbixSender(bound.Address.ToString(), bound.Port);
        ZabbixSenderResponse taken = await sender.SendAsync(Values(0, 10), TestContext.Current.CancellationToken);
        Assert.Equal(10, taken.Processed);                                                    // the trapper told the sender it has all ten
        await destination.NextAsync("sender data");                                          // the first value is on the wire; the agent is busy

        Task stopping = host.StopAsync(CancellationToken.None);
        release.TrySetResult(Taken(JsonDocument.Parse("""{"data":[{}]}""").RootElement));
        await stopping.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        int delivered = 1;
        while (destination.Frames("sender data").TryRead(out JsonElement frame)) delivered += frame.GetProperty("data").GetArrayLength();
        Assert.Equal(10, delivered);
    }

    // ----- a store fault in the middle of a trapped frame ---------------------------------------------------------------

    /// <summary>A writer whose backend fails on the <paramref name="failAt"/>-th write (a disk that fills mid-frame).</summary>
    private sealed class FailingWriter(int failAt) : System.Threading.Channels.ChannelWriter<ZabbixValue>
    {
        public List<ZabbixValue> Kept { get; } = [];

        public override bool TryWrite(ZabbixValue item)
        {
            if (Kept.Count + 1 == failAt) throw new IOException("No space left on device");
            Kept.Add(item);
            return true;
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    [Fact]
    public async Task A_store_fault_mid_frame_answers_what_was_kept_so_the_sender_does_not_resend_it()
    {
        var writer = new FailingWriter(failAt: 3);
        var trapper = new ZabbixTrapper(writer);
        byte[] frame = Encoding.UTF8.GetBytes("""{"request":"sender data","data":[{"host":"h","key":"k","value":"1"},{"host":"h","key":"k","value":"2"},{"host":"h","key":"k","value":"3"},{"host":"h","key":"k","value":"4"}]}""");

        Exception? thrown = await Record.ExceptionAsync(async () => await trapper.Handler(new ZabbixRequest(frame, null), TestContext.Current.CancellationToken));

        Assert.Null(thrown);                                                                  // a fault that escapes is answered "failed" for all four
        Assert.Equal(2, writer.Kept.Count);
        Assert.Equal(2, trapper.Received);
        Assert.Equal(2, trapper.Failed);
    }
}
