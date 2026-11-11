// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Community.Zabbix.Protocol;
using Community.Zabbix.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The passive proxy's two conversations as the server holds them (<c>proxypoller.c</c>): for
/// <c>proxy config</c> the proxy answers with its revisions, the configuration follows on the same connection and is
/// acknowledged; for <c>proxy data</c> the proxy answers with a page carrying its session, the server's acknowledgement
/// follows, and only that acknowledgement takes the page out of the store.
/// </summary>
public sealed class PassiveProxyTests
{
    private const string Config = """
        {"full_sync":1,"config_revision":9,"data":{"hosts":{"fields":["hostid","host","status"],"data":[[10,"web-01",0]]},
         "items":{"fields":["itemid","type","hostid","key_","status","trapper_hosts","allow_traps"],"data":[[100,2,10,"cpu",0,"",0]]}}}
        """;

    // agent-01 is monitored: an enabled active item (answered), a disabled one (not) and a trapper item (not active, not answered).
    // agent-02 is not monitored.
    private const string ActiveConfig = """
        {"full_sync":1,"config_revision":11,"data":{"hosts":{"fields":["hostid","host","status"],"data":[[20,"agent-01",0],[21,"agent-02",1]]},
         "items":{"fields":["itemid","type","hostid","key_","status","trapper_hosts","allow_traps","delay"],
          "data":[[200,7,20,"agent.ping",0,"",0,"30s"],[201,7,20,"agent.disabled",1,"",0,"1m"],[202,2,20,"trap.only",0,"",0,"0"]]}}}
        """;

    private sealed record Running(ServiceProvider Provider, ZabbixPassiveProxy Proxy, FakeLogCollector Logs) : IAsyncDisposable
    {
        public ZabbixServerNode Node => new(((IPEndPoint)Proxy.Listener!.LocalEndPoint!).Address.ToString(), ((IPEndPoint)Proxy.Listener!.LocalEndPoint!).Port);

        public async ValueTask DisposeAsync()
        {
            var hosted = (IHostedLifecycleService)Provider.GetServices<IHostedService>().Single();
            await hosted.StoppingAsync(default);
            await hosted.StopAsync(default);
            await hosted.StoppedAsync(default);
            await Provider.DisposeAsync();
        }
    }

    private static async Task<Running> StartAsync(Action<IZabbixPassiveProxyBuilder>? compose = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddFakeLogging().SetMinimumLevel(LogLevel.Debug));
        IZabbixPassiveProxyBuilder proxy = services.AddZabbixPassiveProxy("proxy-02")
            .AddTrapper(0, o => o.Address = "127.0.0.1")
            .AllowPeer(IPAddress.Loopback)
            .WithVersion("7.4.0")
            .Configure(o => o.PageSize = 2);
        compose?.Invoke(proxy);
        ServiceProvider provider = services.BuildValidated();
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();
        await hosted.StartingAsync(default);
        await hosted.StartAsync(default);
        return new Running(provider, provider.GetRequiredKeyedService<ZabbixPassiveProxy>("proxy-02"), provider.GetFakeLogCollector());
    }

    /// <summary>The server's side of one connection: frames written and read in turn.</summary>
    private sealed class ServerSide : IAsyncDisposable
    {
        private readonly TcpClient _client = new();
        private NetworkStream? _stream;

        public static async Task<ServerSide> ConnectAsync(ZabbixServerNode node)
        {
            var side = new ServerSide();
            await side._client.ConnectAsync(node.Host, node.Port, TestContext.Current.CancellationToken);
            side._stream = side._client.GetStream();
            return side;
        }

        public Task SendAsync(string json) => ZabbixHeader.WriteAsync(_stream!, Encoding.UTF8.GetBytes(json), TestContext.Current.CancellationToken);

        public async Task<JsonElement> ReceiveAsync() => JsonDocument.Parse(await ZabbixHeader.ReadAsync(_stream!, TestContext.Current.CancellationToken)).RootElement.Clone();

        public async ValueTask DisposeAsync()
        {
            if (_stream is not null) await _stream.DisposeAsync();
            _client.Dispose();
        }
    }

    private static async Task ConfigureAsync(Running proxy, string config = Config)
    {
        await using ServerSide server = await ServerSide.ConnectAsync(proxy.Node);
        await server.SendAsync("""{"request":"proxy config"}""");
        await server.ReceiveAsync();
        await server.SendAsync(config);
        await server.ReceiveAsync();
    }

    private static async Task<JsonElement> PullAsync(Running proxy, string? ack)
    {
        await using ServerSide server = await ServerSide.ConnectAsync(proxy.Node);
        await server.SendAsync("""{"request":"proxy data"}""");
        JsonElement page = await server.ReceiveAsync();
        if (ack is not null)
        {
            await server.SendAsync(ack);
            await server.ReceiveAsync().ContinueWith(static _ => { }, TaskScheduler.Default);   // the proxy closes; nothing more comes
        }
        return page;
    }

    private static long[] Ids(JsonElement page)
        => page.TryGetProperty("history data", out JsonElement rows) ? [.. rows.EnumerateArray().Select(row => row.GetProperty("id").GetInt64())] : [];

    [Fact]
    public async Task The_config_request_is_answered_with_the_revisions_held_and_the_configuration_that_follows_is_merged_and_acknowledged()
    {
        await using Running proxy = await StartAsync();
        await using ServerSide server = await ServerSide.ConnectAsync(proxy.Node);

        await server.SendAsync("""{"request":"proxy config"}""");
        JsonElement state = await server.ReceiveAsync();
        await server.SendAsync(Config);
        JsonElement ack = await server.ReceiveAsync();

        Assert.Equal(0, state.GetProperty("config_revision").GetInt64());                    // nothing held yet: a full sync
        Assert.Equal("7.4.0", state.GetProperty("version").GetString());
        Assert.Equal(proxy.Proxy.Session, state.GetProperty("session").GetString());
        Assert.Equal("success", ack.GetProperty("response").GetString());
        Assert.Equal(9, proxy.Proxy.Config.ConfigRevision);
        Assert.Single(proxy.Proxy.Config.Items);                                              // the request itself was never taken as the config
    }

    [Fact]
    public async Task A_page_carries_the_session_and_leaves_the_store_only_on_the_server_s_acknowledgement()
    {
        await using Running proxy = await StartAsync();
        await ConfigureAsync(proxy);
        using ZabbixSender sender = new ZabbixSender([proxy.Node]);
        ZabbixSenderResponse trapped = await sender.SendAsync([new ZabbixValue("web-01", "cpu", "1", DateTimeOffset.UnixEpoch), new ZabbixValue("web-01", "cpu", "2", DateTimeOffset.UnixEpoch), new ZabbixValue("web-01", "cpu", "3", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);
        Assert.Equal(3, trapped.Processed);

        JsonElement unacknowledged = await PullAsync(proxy, ack: null);                     // the server went away before its ack
        JsonElement again = await PullAsync(proxy, """{"response":"success"}""");
        int heldAfterAck = proxy.Proxy.Store!.Count;
        JsonElement last = await PullAsync(proxy, """{"response":"success"}""");

        Assert.Matches("^[0-9a-f]{32}$", unacknowledged.GetProperty("session").GetString());
        Assert.Equal(1, unacknowledged.GetProperty("more").GetInt32());
        Assert.Equal(Ids(unacknowledged), Ids(again));                                       // the same ids: the server's last_id de-duplicates
        Assert.Equal(1, heldAfterAck);
        Assert.Equal(["3"], last.GetProperty("history data").EnumerateArray().Select(row => row.GetProperty("value").GetString()));
        Assert.False(last.TryGetProperty("more", out _));
        Assert.True(Ids(last)[0] > Ids(again)[^1]);
        Assert.Contains(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7031 && record.Level == LogLevel.Warning);
    }

    /// <summary>
    /// The passive proxy takes a refused page apart as the active one does (the shared data sender): a page refused for a
    /// discovery row the server cannot read is followed, one pull at a time, by the history alone (taken), then the
    /// discovery row alone (refused, so dropped, logged and counted), and the values behind it flow on the next pull
    /// instead of the same page being re-sent for ever.
    /// </summary>
    [Fact]
    public async Task A_refused_page_is_taken_apart_over_the_next_pulls_and_the_history_behind_it_flows()
    {
        await using Running proxy = await StartAsync();
        await ConfigureAsync(proxy);
        const string Unreadable = """{"response":"failed","info":"cannot find pair with name \"ip\""}""";
        proxy.Proxy.DiscoveryStore!.TryWrite(new ZabbixProxyDiscoveryItem(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), DruleId: 5, DcheckId: 6, Ip: null!, Dns: null, Port: 22, Value: "ssh", Status: 0));
        using ZabbixSender sender2 = new ZabbixSender([proxy.Node]);
        await sender2.SendAsync([new ZabbixValue("web-01", "cpu", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);

        JsonElement refused = await PullAsync(proxy, Unreadable);
        JsonElement control = await PullAsync(proxy, """{"response":"success"}""");
        JsonElement alone = await PullAsync(proxy, Unreadable);
        using ZabbixSender sender3 = new ZabbixSender([proxy.Node]);
        await sender3.SendAsync([new ZabbixValue("web-01", "cpu", "2", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);
        JsonElement next = await PullAsync(proxy, """{"response":"success"}""");

        Assert.True(refused.TryGetProperty("discovery data", out _));
        Assert.Equal(Ids(refused), Ids(control));                                                 // the history alone, the same ids
        Assert.False(control.TryGetProperty("discovery data", out _));
        Assert.Empty(Ids(alone));
        Assert.Equal(1, alone.GetProperty("discovery data").GetArrayLength());
        Assert.Equal(["2"], next.GetProperty("history data").EnumerateArray().Select(row => row.GetProperty("value").GetString()));
        Assert.Equal(0, proxy.Proxy.DiscoveryStore.Count);
        Assert.Equal(0, proxy.Proxy.Store!.Count);
        Assert.Equal(LogLevel.Error, Assert.Single(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7040).Level);
    }

    [Fact]
    public async Task Upload_disabled_holds_the_pages_until_a_pull_s_empty_probe_is_taken()
    {
        await using Running proxy = await StartAsync();
        await ConfigureAsync(proxy);
        using ZabbixSender sender2 = new ZabbixSender([proxy.Node]);
        await sender2.SendAsync([new ZabbixValue("web-01", "cpu", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);

        JsonElement disabled = await PullAsync(proxy, """{"response":"failed","upload":"disabled","info":"history upload is disabled"}""");
        JsonElement probe = await PullAsync(proxy, """{"response":"success"}""");
        JsonElement resumed = await PullAsync(proxy, """{"response":"success"}""");

        Assert.Single(Ids(disabled));
        Assert.Empty(Ids(probe));                                                                 // held: the pull is answered with the probe
        Assert.Equal(Ids(disabled), Ids(resumed));                                                // the held page, with its ids
        Assert.Equal(0, proxy.Proxy.Store!.Count);
        Assert.Contains(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7012);
        Assert.Contains(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7013);
    }

    [Fact]
    public async Task Every_provider_added_is_asked_for_each_pull_in_the_order_it_was_added()
    {
        await using Running proxy = await StartAsync(builder => builder
            .AddProvider((_, _) => ValueTask.FromResult<ZabbixProxyDataPage?>(
                new ZabbixProxyDataPage(History: [new ZabbixProxyHistoryItem(1, 100, new ZabbixClock(1_700_000_000, 0), "first")])))
            .AddProvider((_, _) => ValueTask.FromResult<ZabbixProxyDataPage?>(
                new ZabbixProxyDataPage(History: [new ZabbixProxyHistoryItem(2, 100, new ZabbixClock(1_700_000_000, 0), "second")]))));
        await ConfigureAsync(proxy);

        JsonElement provided = await PullAsync(proxy, """{"response":"success"}""");
        JsonElement again = await PullAsync(proxy, """{"response":"success"}""");

        Assert.Equal(["first", "second"], provided.GetProperty("history data").EnumerateArray().Select(row => row.GetProperty("value").GetString()));
        Assert.Equal(2, again.GetProperty("history data").GetArrayLength());                  // asked again for the next pull
    }

    [Fact]
    public async Task Discovery_and_availability_entries_written_to_their_stores_are_pulled_under_their_own_wire_keys()
    {
        await using Running proxy = await StartAsync();
        proxy.Proxy.DiscoveryStore!.TryWrite(new ZabbixProxyDiscoveryItem(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), DruleId: 5, DcheckId: 6, Ip: "10.0.0.1", Dns: null, Port: 22, Value: "ssh", Status: 0));
        proxy.Proxy.AvailabilityStore!.TryWrite(new ZabbixProxyAvailabilityItem(InterfaceId: 7, Available: 1, Error: null));

        JsonElement page = await PullAsync(proxy, """{"response":"success"}""");

        Assert.Equal(5, page.GetProperty("discovery data")[0].GetProperty("drule").GetInt64());
        Assert.Equal(7, page.GetProperty("interface availability")[0].GetProperty("interfaceid").GetInt64());
        Assert.Equal(0, proxy.Proxy.DiscoveryStore!.Count);
        Assert.Equal(0, proxy.Proxy.AvailabilityStore!.Count);
    }

    [Fact]
    public async Task Tasks_in_the_server_s_acknowledgement_are_read_and_reported()
    {
        await using Running proxy = await StartAsync();

        await PullAsync(proxy, """{"response":"success","tasks":[{"type":2,"clock":1,"ttl":0,"command_type":0,"command":"echo","execute_on":1}]}""");

        Assert.Contains(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7015 && record.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_configuration_that_is_not_json_is_answered_failed_and_logged()
    {
        await using Running proxy = await StartAsync();
        await using ServerSide server = await ServerSide.ConnectAsync(proxy.Node);

        await server.SendAsync("""{"request":"proxy config"}""");
        await server.ReceiveAsync();
        await server.SendAsync("""{"data":{"hosts":{"fields":["hostid","host"],"data":[[10]]}}}""");
        JsonElement ack = await server.ReceiveAsync();
        // One conversation per connection (ZabbixListener): the proxy closes right after logging the failure, so waiting
        // for that close — not just the reply — is what makes the log observable below, without a sleep-then-assert race.
        await server.ReceiveAsync().ContinueWith(static _ => { }, TaskScheduler.Default);

        Assert.Equal("failed", ack.GetProperty("response").GetString());
        Assert.Equal(0, proxy.Proxy.Config.ConfigRevision);
        Assert.Contains(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7031 && record.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_stored_value_whose_item_left_the_configuration_is_dropped_and_counted()
    {
        await using Running proxy = await StartAsync();
        await ConfigureAsync(proxy);
        using ZabbixSender sender = new ZabbixSender([proxy.Node]);
        await sender.SendAsync([new ZabbixValue("web-01", "cpu", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);
        await ConfigureAsync(proxy, """{"data":{},"del_hostids":[10],"config_revision":10}""");   // proxyconfigread.c's delta shape

        JsonElement page = await PullAsync(proxy, """{"response":"success"}""");

        Assert.Empty(Ids(page));
        Assert.Equal(0, proxy.Proxy.Store!.Count);
        Assert.Contains(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7014 && record.Level == LogLevel.Warning);
    }

    // ----- active checks: a real agent polling the passive proxy directly ----------------------------------------------

    [Fact]
    public async Task Active_checks_answers_the_enabled_active_items_of_a_monitored_host_and_nothing_else()
    {
        await using Running proxy = await StartAsync();
        await ConfigureAsync(proxy, ActiveConfig);
        await using ServerSide agent = await ServerSide.ConnectAsync(proxy.Node);

        await agent.SendAsync("""{"request":"active checks","host":"agent-01"}""");
        JsonElement answer = await agent.ReceiveAsync();

        Assert.Equal("success", answer.GetProperty("response").GetString());
        Assert.Equal(11, answer.GetProperty("config_revision").GetInt64());
        JsonElement item = Assert.Single(answer.GetProperty("data").EnumerateArray());
        Assert.Equal(200, item.GetProperty("itemid").GetInt64());
        Assert.Equal("agent.ping", item.GetProperty("key").GetString());
        Assert.Equal("30s", item.GetProperty("delay").GetString());
    }

    // agent-03's items carry a timeout column, one delay a host macro (active.c send_list_of_active_checks_json).
    private const string ActiveTimeoutConfig = """
        {"full_sync":1,"config_revision":12,"data":{"hosts":{"fields":["hostid","host","status"],"data":[[30,"agent-03",0]]},
         "items":{"fields":["itemid","type","hostid","key_","status","trapper_hosts","allow_traps","delay","timeout"],
          "data":[[300,7,30,"agent.ping",0,"",0,"{$PING.DELAY}","{$PING.TIMEOUT}"],[301,7,30,"agent.version",0,"",0,"1m",""]]},
         "hostmacro":{"fields":["hostmacroid","hostid","macro","value","type","automatic"],"data":[[1,30,"{$PING.DELAY}","15s",0,0],[2,30,"{$PING.TIMEOUT}","5s",0,0]]},
         "settings":{"fields":["name","type","value_str","value_int"],"data":[["timeout_zabbix_agent",1,"4s",0]]}}}
        """;

    [Fact]
    public async Task Active_checks_carries_lastlogsize_mtime_and_the_item_s_timeout_with_its_macros_resolved()
    {
        await using Running proxy = await StartAsync();
        await ConfigureAsync(proxy, ActiveTimeoutConfig);
        await using ServerSide agent = await ServerSide.ConnectAsync(proxy.Node);

        await agent.SendAsync("""{"request":"active checks","host":"agent-03"}""");
        Dictionary<long, JsonElement> items = (await agent.ReceiveAsync()).GetProperty("data").EnumerateArray().ToDictionary(item => item.GetProperty("itemid").GetInt64());

        // A C agent skips an item without lastlogsize and Agent 2 refuses the list without mtime (active_checks.c, serverconnector.go).
        Assert.All(items.Values, item => Assert.Equal((0L, 0), (item.GetProperty("lastlogsize").GetInt64(), item.GetProperty("mtime").GetInt32())));
        Assert.Equal(("15s", "5s"), (items[300].GetProperty("delay").GetString(), items[300].GetProperty("timeout").GetString()));
        Assert.Equal("4s", items[301].GetProperty("timeout").GetString());                  // none of its own: the proxy's agent timeout, as dbconfig.c timeout_orig
    }

    [Fact]
    public void A_passive_proxy_without_a_version_fails_at_start()
    {
        var services = new ServiceCollection();
        services.AddZabbixPassiveProxy("proxy-02").AddTrapper(0).AllowPeer(IPAddress.Loopback);
        using ServiceProvider provider = services.BuildValidated();

        // The server never tells a passive proxy its version (proxy.c zbx_get_proxy_compatibility), so none is guessed.
        var failure = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ZabbixProxyOptions>>().Get("proxy-02"));

        Assert.Contains(failure.Failures, message => message.Contains("Version", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Active_checks_for_an_unknown_host_is_refused_by_name_as_the_server_refuses_it()
    {
        await using Running proxy = await StartAsync();
        await ConfigureAsync(proxy, ActiveConfig);
        await using ServerSide agent = await ServerSide.ConnectAsync(proxy.Node);

        await agent.SendAsync("""{"request":"active checks","host":"nowhere"}""");
        JsonElement answer = await agent.ReceiveAsync();

        Assert.Equal("failed", answer.GetProperty("response").GetString());
        Assert.Equal("host [nowhere] not found", answer.GetProperty("info").GetString());
    }

    [Fact]
    public async Task Active_checks_for_a_host_not_monitored_is_refused_without_its_items()
    {
        await using Running proxy = await StartAsync();
        await ConfigureAsync(proxy, ActiveConfig);
        await using ServerSide agent = await ServerSide.ConnectAsync(proxy.Node);

        await agent.SendAsync("""{"request":"active checks","host":"agent-02"}""");
        JsonElement answer = await agent.ReceiveAsync();

        Assert.Equal("failed", answer.GetProperty("response").GetString());
        Assert.Equal("""host "agent-02" not monitored""", answer.GetProperty("info").GetString());
    }

    // ----- the extension points: OnConfig, providers, OnStats ---------------------------------------------------------------

    [Fact]
    public async Task A_registered_config_handler_can_veto_a_pushed_configuration()
    {
        await using Running proxy = await StartAsync(builder => builder.OnConfig((_, _, _) => ValueTask.FromResult<string?>("not today")));
        await using ServerSide server = await ServerSide.ConnectAsync(proxy.Node);

        await server.SendAsync("""{"request":"proxy config"}""");
        await server.ReceiveAsync();
        await server.SendAsync(Config);
        JsonElement ack = await server.ReceiveAsync();

        Assert.Equal("failed", ack.GetProperty("response").GetString());
        Assert.Equal("not today", ack.GetProperty("info").GetString());
        Assert.Equal(0, proxy.Proxy.Config.ConfigRevision);
        Assert.Contains(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7032 && record.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_registered_config_handler_that_accepts_lets_the_configuration_through()
    {
        await using Running proxy = await StartAsync(builder => builder.OnConfig((_, _, _) => ValueTask.FromResult<string?>(null)));
        await ConfigureAsync(proxy);

        Assert.Equal(9, proxy.Proxy.Config.ConfigRevision);
    }

    [Fact]
    public async Task A_registered_data_provider_s_page_is_merged_into_the_pulled_page()
    {
        await using Running proxy = await StartAsync(builder => builder.AddProvider((_, _) => ValueTask.FromResult<ZabbixProxyDataPage?>(
            new ZabbixProxyDataPage(History: [new ZabbixProxyHistoryItem(1, 100, new ZabbixClock(1_700_000_000, 0), "provided")]))));

        JsonElement page = await PullAsync(proxy, """{"response":"success"}""");

        JsonElement item = Assert.Single(page.GetProperty("history data").EnumerateArray());
        Assert.Equal("provided", item.GetProperty("value").GetString());
    }

    [Fact]
    public async Task Zabbix_stats_with_no_provider_answers_success_with_an_empty_data_object()
    {
        await using Running proxy = await StartAsync();
        await using ServerSide server = await ServerSide.ConnectAsync(proxy.Node);

        await server.SendAsync("""{"request":"zabbix.stats"}""");
        JsonElement answer = await server.ReceiveAsync();

        Assert.Equal("success", answer.GetProperty("response").GetString());
        Assert.Empty(answer.GetProperty("data").EnumerateObject());
    }

    [Fact]
    public async Task Zabbix_stats_is_answered_by_the_registered_provider()
    {
        await using Running proxy = await StartAsync(builder => builder.OnStats((_, _) => ValueTask.FromResult<IReadOnlyDictionary<string, JsonElement>?>(
            new Dictionary<string, JsonElement> { ["uptime"] = JsonSerializer.SerializeToElement(123) })));
        await using ServerSide server = await ServerSide.ConnectAsync(proxy.Node);

        await server.SendAsync("""{"request":"zabbix.stats"}""");
        JsonElement answer = await server.ReceiveAsync();

        Assert.Equal("success", answer.GetProperty("response").GetString());
        Assert.Equal(123, answer.GetProperty("data").GetProperty("uptime").GetInt32());
    }

    [Fact]
    public async Task A_stats_provider_that_throws_is_logged_and_the_reply_carries_no_data()
    {
        await using Running proxy = await StartAsync(builder => builder.OnStats((_, _) => throw new InvalidOperationException("stats boom")));
        await using ServerSide server = await ServerSide.ConnectAsync(proxy.Node);

        await server.SendAsync("""{"request":"zabbix.stats"}""");
        JsonElement answer = await server.ReceiveAsync();

        Assert.Equal("success", answer.GetProperty("response").GetString());
        Assert.Empty(answer.GetProperty("data").EnumerateObject());
        Assert.Contains(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7019 && record.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Tasks_in_the_server_s_acknowledgement_reach_a_registered_handler_and_its_result_is_pulled_next()
    {
        var seen = new TaskCompletionSource<ZabbixProxyTask>();
        await using Running proxy = await StartAsync(builder => builder.OnTask((task, _) =>
        {
            seen.TrySetResult(task);
            return ValueTask.FromResult(new ZabbixProxyTaskResult(ZabbixProxyTaskType.RemoteCommandResult, task.Clock, task.Ttl, Status: 0, "done", task.ParentTaskId ?? 0UL));
        }));

        await PullAsync(proxy, """{"response":"success","tasks":[{"type":2,"clock":9,"ttl":0,"command_type":0,"command":"ls","execute_on":1,"parent_taskid":3}]}""");
        ZabbixProxyTask task = await seen.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("ls", task.Command);

        JsonElement next = await PullAsync(proxy, """{"response":"success"}""");
        JsonElement result = Assert.Single(next.GetProperty("tasks").EnumerateArray());
        Assert.Equal("done", result.GetProperty("info").GetString());
        Assert.Equal(3, result.GetProperty("parent_taskid").GetInt64());
    }

    // ----- proxy tasks: the server's first question to a passive proxy (proxypoller.c proxy_get_tasks) -------------------

    private static async Task<JsonElement> TasksAsync(Running proxy, string? ack)
    {
        await using ServerSide server = await ServerSide.ConnectAsync(proxy.Node);
        await server.SendAsync("""{"request":"proxy tasks"}""");
        JsonElement answer = await server.ReceiveAsync();
        if (ack is not null)
        {
            await server.SendAsync(ack);
            await server.ReceiveAsync().ContinueWith(static _ => { }, TaskScheduler.Default);   // the proxy closes; nothing more comes
        }
        return answer;
    }

    [Fact]
    public async Task Proxy_tasks_is_answered_with_the_proxy_s_version_and_clock_so_the_server_rates_it_current()
    {
        await using Running proxy = await StartAsync();

        JsonElement answer = await TasksAsync(proxy, """{"response":"success"}""");

        Assert.Equal("7.4.0", answer.GetProperty("version").GetString());                     // not empty: an empty answer is a pre-3.4 proxy
        Assert.True(answer.GetProperty("clock").GetInt64() > 0);
        Assert.True(answer.TryGetProperty("ns", out _));
        Assert.False(answer.TryGetProperty("tasks", out _));                                 // nothing to report
        Assert.False(answer.TryGetProperty("history data", out _));                          // tasks only: history is proxy data's
        Assert.DoesNotContain(proxy.Logs.GetSnapshot(), record => record.Id.Id == 6101);    // not handed to the trapper
    }

    [Fact]
    public async Task Task_results_travel_with_proxy_tasks_and_leave_their_store_only_on_the_acknowledgement()
    {
        await using Running proxy = await StartAsync();
        proxy.Proxy.TasksStore!.TryWrite(new ZabbixProxyTaskResult(ZabbixProxyTaskType.RemoteCommandResult, DateTimeOffset.FromUnixTimeSeconds(9), 0, Status: 0, "done", 3));

        JsonElement unacknowledged = await TasksAsync(proxy, ack: null);                    // the server went away before its ack
        JsonElement again = await TasksAsync(proxy, """{"response":"success"}""");

        Assert.Equal("done", Assert.Single(unacknowledged.GetProperty("tasks").EnumerateArray()).GetProperty("info").GetString());
        Assert.Equal(3, Assert.Single(again.GetProperty("tasks").EnumerateArray()).GetProperty("parent_taskid").GetInt64());
        Assert.Equal(0, proxy.Proxy.TasksStore!.Count);
        Assert.False((await TasksAsync(proxy, """{"response":"success"}""")).TryGetProperty("tasks", out _));
    }

    [Fact]
    public async Task Tasks_in_the_proxy_tasks_acknowledgement_reach_the_registered_handler()
    {
        var seen = new TaskCompletionSource<ZabbixProxyTask>();
        await using Running proxy = await StartAsync(builder => builder.OnTask((task, _) =>
        {
            seen.TrySetResult(task);
            return ValueTask.FromResult(new ZabbixProxyTaskResult(ZabbixProxyTaskType.RemoteCommandResult, task.Clock, task.Ttl, Status: 0, "ran", task.ParentTaskId ?? 0UL));
        }));

        await TasksAsync(proxy, """{"response":"success","tasks":[{"type":2,"clock":9,"ttl":0,"command_type":0,"command":"uptime","execute_on":1,"parent_taskid":4}]}""");
        ZabbixProxyTask task = await seen.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        JsonElement next = await TasksAsync(proxy, """{"response":"success"}""");

        Assert.Equal("uptime", task.Command);
        Assert.Equal("ran", Assert.Single(next.GetProperty("tasks").EnumerateArray()).GetProperty("info").GetString());
    }
}
