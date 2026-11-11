// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Net;
using System.Text.Json;
using Community.Zabbix.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Community.Zabbix.Tests;

/// <summary>
/// The SDK's proxies registered on every live server. The active proxy pulls its configuration and uploads what a
/// sender and an SDK active agent hand its trapper, its discovery results and an autoregistration; a relay cuts its
/// connection after the server took an upload, and the re-sent page must not duplicate a value. The passive proxy is
/// polled by the server across the pod boundary: configuration pushed in, data pulled out.
/// </summary>
public sealed class LiveProxyTests
{
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task The_active_proxy_receives_its_configuration_and_uploads_what_its_trapper_and_an_active_agent_hand_it(string version)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "proxy");
        (string proxyName, string proxyId) = await ProxyAsync(scope, "proxy", active: true);
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "proxied", [("zbxtest.trap", ZabbixItemValueType.Float)], r => r with { MonitoredBy = ZabbixMonitoredBy.Proxy, ProxyId = ulong.Parse(proxyId, System.Globalization.CultureInfo.InvariantCulture) });
        LiveFixtures.LiveItem active = await LiveFixtures.ItemAsync(scope, host, "zbxtest.answer", ZabbixItemValueType.Unsigned, ZabbixItemType.ZabbixActive, ""","delay":"1s" """);

        IHost app = await LiveHosting.StartAsync(services => services.AddZabbixActiveProxy(proxyName)
            .AddServer(new System.Net.DnsEndPoint(trapper.Host, trapper.Port))
            .AddTrapper(0, o => o.Address = "127.0.0.1")
            .AllowPeer(System.Net.IPAddress.Loopback)
            .Configure(o => { o.ConfigInterval = TimeSpan.FromSeconds(1); o.DataInterval = TimeSpan.FromSeconds(1); }));
        IHost? agent = null;
        try
        {
            ZabbixActiveProxy proxy = app.Services.GetRequiredKeyedService<ZabbixActiveProxy>(proxyName);
            await LiveApi.EventuallyAsync(() => Task.FromResult(proxy.Config.HostsByName.ContainsKey(host.Host) ? "configured" : null), $"the proxy's configuration to hold {host.Host}");
            int port = ((IPEndPoint)proxy.Trapper!.LocalEndPoint!).Port;

            // The proxy accepting a value says the PROXY knows the item; the server stores it only once its own configuration
            // cache does, and drops it otherwise (answering the proxy with success) — so fresh values are sent per attempt
            // until one is in history (one value sent once can be dropped, and the wait for it would then time out).
            using var sender = new ZabbixSender("127.0.0.1", port);
            ZabbixSenderResponse sent = default!;
            IReadOnlyList<ZabbixHistoryRecord> trapped = await LiveApi.EventuallyAsync<IReadOnlyList<ZabbixHistoryRecord>>(async () =>
            {
                sent = await LiveFixtures.SendWhenKnownAsync(sender, [new ZabbixValue(host.Host, "zbxtest.trap", "2.5", DateTimeOffset.UtcNow)]);
                IReadOnlyList<ZabbixHistoryRecord> rows = await scope.Server.History.GetAsync(new ZabbixHistoryGetParameters
                {
                    History = host["zbxtest.trap"].Type,
                    ItemIds = [host["zbxtest.trap"].ItemId],
                    Output = ZabbixOutput.Extend,
                }, TestContext.Current.CancellationToken);
                return rows.Count > 0 ? rows : null;
            }, "a proxied zbxtest.trap value in history.get");
            agent = await LiveHosting.StartAsync(services => services.AddZabbixActiveAgent("zbx")
                .AddServer(new DnsEndPoint("127.0.0.1", port))
                .AddItem("zbxtest.answer", _ => "44")
                .Configure(o => { o.Hostname = host.Host; o.RefreshInterval = TimeSpan.FromSeconds(1); o.HeartbeatInterval = TimeSpan.Zero; o.Send.FlushInterval = TimeSpan.FromSeconds(1); }));

            Assert.Equal((1, 0), (sent.Processed, sent.Failed));
            Assert.Equal("2.5", trapped[0].Value);
            Assert.Equal("44", (await LiveFixtures.HistoryAsync(scope.Server, active, 1))[0].Value);
            // The server rates the proxy by the version it announced: 1 is "current" (proxy.get compatibility, 7.0+). Its
            // lastaccess reaches proxy_rtdata, which proxy.get reads, only when the configuration cache flushes it: no
            // sooner than ZBX_PROXY_LASTACCESS_UPDATE_FREQUENCY (5 s, zbxcfg.h; dbconfig.c:15584), so it is awaited.
            string compatibility = await LiveApi.EventuallyAsync(async () =>
            {
                JsonElement row = (await scope.Server.RpcAsync("proxy.get", new { proxyids = new[] { proxyId }, output = new[] { "lastaccess", "version", "compatibility" } }))[0];
                return row.GetProperty("lastaccess").GetString() is not "0" ? row.GetProperty("compatibility").GetString() : null;
            }, $"proxy {proxyName}'s lastaccess to reach the API");
            Assert.Equal("1", compatibility);
        }
        finally
        {
            if (agent is not null) await agent.ShutdownAsync();
            await app.ShutdownAsync();
        }
    }

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_page_the_server_took_before_the_connection_was_cut_is_re_sent_with_its_ids_and_not_duplicated(string version)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "proxy-cut");
        (string proxyName, string proxyId) = await ProxyAsync(scope, "proxy-cut", active: true);
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "cut", [("zbxtest.trap", ZabbixItemValueType.Unsigned)], r => r with { MonitoredBy = ZabbixMonitoredBy.Proxy, ProxyId = ulong.Parse(proxyId, System.Globalization.CultureInfo.InvariantCulture) });
        LiveFixtures.LiveItem probe = await LiveFixtures.ProxyProbeAsync(scope, host);
        // The first upload carrying history reaches the server, which stores it; its reply never reaches the proxy.
        int cuts = 0;
        await using var relay = new LiveRelay(trapper, payload => payload.Contains("\"history data\"", StringComparison.Ordinal) && payload.Contains("\"itemid\":" + host["zbxtest.trap"].ItemId, StringComparison.Ordinal) && Interlocked.Exchange(ref cuts, 1) == 0);

        IHost app = await LiveHosting.StartAsync(services => services.AddZabbixActiveProxy(proxyName)
            .AddServer(new System.Net.DnsEndPoint(relay.Node.Host, relay.Node.Port))
            .AddTrapper(0, o => o.Address = "127.0.0.1")
            .AllowPeer(System.Net.IPAddress.Loopback)
            .Configure(o => { o.ConfigInterval = TimeSpan.FromSeconds(1); o.DataInterval = TimeSpan.FromSeconds(1); }));
        try
        {
            ZabbixActiveProxy proxy = app.Services.GetRequiredKeyedService<ZabbixActiveProxy>(proxyName);
            await LiveApi.EventuallyAsync(() => Task.FromResult(proxy.Config.HostsByName.ContainsKey(host.Host) ? "configured" : null), $"the proxy's configuration to hold {host.Host}");
            using var sender = new ZabbixSender("127.0.0.1", ((IPEndPoint)proxy.Trapper!.LocalEndPoint!).Port);
            // A page sent before the cache holds zbxtest.trap would be dropped with its ids recorded, so the re-sent page would pass for a duplicate.
            await LiveFixtures.UntilProxiedValuesAreStoredAsync(scope, host, probe, sender);
            long second = DateTimeOffset.UtcNow.AddSeconds(-30).ToUnixTimeSeconds();
            ZabbixValue[] values = [.. Enumerable.Range(1, 5).Select(i => new ZabbixValue(host.Host, "zbxtest.trap", i.ToString(System.Globalization.CultureInfo.InvariantCulture), new ZabbixClock(second, i)))];

            await LiveFixtures.SendWhenKnownAsync(sender, values);
            await LiveApi.EventuallyAsync(() => Task.FromResult(relay.Dropped > 0 ? "cut" : null), "the relay to cut the first upload");
            // A sentinel sent after the cut is uploaded after the re-sent page: once it is stored, any duplicate would be too.
            await sender.SendAsync([new ZabbixValue(host.Host, "zbxtest.trap", "99", new ZabbixClock(second + 1, 0))], TestContext.Current.CancellationToken);
            IReadOnlyList<ZabbixHistoryRecord> stored = await LiveApi.EventuallyAsync(async () =>
            {
                IReadOnlyList<ZabbixHistoryRecord> rows = await LiveFixtures.HistoryAsync(scope.Server, host["zbxtest.trap"], 1);
                return rows.Any(r => r.Value == "99") ? rows : null;
            }, "the sentinel in history.get");

            Assert.Equal(["1", "2", "3", "4", "5", "99"], stored.Select(r => r.Value));
            Assert.Equal(1, relay.Dropped);
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task An_autoregistration_the_active_proxy_uploads_runs_the_server_s_autoregistration_action(string version)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "autoreg");
        (string proxyName, _) = await ProxyAsync(scope, "autoreg", active: true);
        IReadOnlyList<ulong> groups = await scope.Server.HostGroups.CreateAsync([new ZabbixHostGroupCreateRequest { Name = scope.Name("autoreg-group") }], TestContext.Current.CancellationToken);
        scope.Delete("hostgroup", groups);
        string registered = scope.Name("registered");
        scope.Defer(async () =>
        {
            JsonElement hosts = await scope.Server.RpcAsync("host.get", new { filter = new { host = new[] { registered } }, output = new[] { "hostid" } });
            if (hosts.GetArrayLength() > 0) await scope.Server.RpcAsync("host.delete", hosts.EnumerateArray().Select(h => h.GetProperty("hostid").GetString()).ToArray());
        });
        JsonElement action = await scope.Server.RpcAsync("action.create", new
        {
            name = scope.Name("autoreg-action"),
            eventsource = 2,
            filter = new { evaltype = 0, conditions = new[] { new { conditiontype = 24, @operator = 2, value = scope.Run } } },
            operations = new object[] { new { operationtype = 2 }, new { operationtype = 4, opgroup = new[] { new { groupid = groups[0] } } } },
        });
        scope.Delete("action", action.Id("actionids"));
        // The server runs the action only once its configuration cache holds it; an autoregistration processed before that
        // creates no host and is not run again. A trapper item created after the action is the observable: once the
        // server's trapper takes a value of it, the cache was synchronised after both were committed.
        LiveFixtures.LiveHost cached = await LiveFixtures.HostAsync(scope, "autoreg-probe", [("zbxtest.probe", ZabbixItemValueType.Unsigned)]);
        using (var direct = new ZabbixSender([trapper]))
            await LiveFixtures.SendWhenKnownAsync(direct, [new ZabbixValue(cached.Host, "zbxtest.probe", "1", DateTimeOffset.UtcNow)]);

        IHost app = await LiveHosting.StartAsync(services => services.AddZabbixActiveProxy(proxyName)
            .AddServer(new System.Net.DnsEndPoint(trapper.Host, trapper.Port))
            .AddTrapper(0, o => o.Address = "127.0.0.1")
            .AllowPeer(System.Net.IPAddress.Loopback)
            .Configure(o => { o.ConfigInterval = TimeSpan.FromSeconds(1); o.DataInterval = TimeSpan.FromSeconds(1); }));
        try
        {
            ZabbixActiveProxy proxy = app.Services.GetRequiredKeyedService<ZabbixActiveProxy>(proxyName);
            await LiveApi.EventuallyAsync(() => Task.FromResult(proxy.Config.ConfigRevision > 0 ? "configured" : null), "the proxy's first configuration");
            await proxy.AutoregistrationStore!.WriteAsync(new ZabbixProxyAutoregistrationItem(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), registered, "192.0.2.77", "", 10050, scope.Run), TestContext.Current.CancellationToken);

            JsonElement host = await LiveApi.EventuallyAsync<JsonElement?>(async () =>
            {
                JsonElement rows = await scope.Server.RpcAsync("host.get", new { filter = new { host = new[] { registered } }, output = new[] { "hostid", "host", "proxyid" }, selectHostGroups = new[] { "groupid" }, selectInterfaces = new[] { "ip", "port" } });
                return rows.GetArrayLength() > 0 ? rows[0] : null;
            }, $"the autoregistration action to create {registered}") ?? default;

            // The "add host" operation files it under the discovery group too (settings.discovery_groupid); ours is added.
            Assert.Contains(groups[0].ToString(CultureInfo.InvariantCulture), host.GetProperty("hostgroups").EnumerateArray().Select(g => g.GetProperty("groupid").GetString()));
            Assert.Equal("192.0.2.77", host.GetProperty("interfaces")[0].GetProperty("ip").GetString());
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_discovery_result_the_active_proxy_uploads_becomes_a_discovered_host_and_service(string version)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "discovery");
        (string proxyName, string proxyId) = await ProxyAsync(scope, "discovery", active: true);
        JsonElement rule = await scope.Server.RpcAsync("drule.create", new
        {
            name = scope.Name("drule"),
            iprange = "192.0.2.10",
            proxyid = proxyId,
            delay = "1h",
            dchecks = new[] { new { type = 9, key_ = "system.uname", ports = "10050", uniq = 0 } },
        });
        string druleId = rule.Id("druleids");
        scope.Delete("drule", druleId);
        JsonElement check = await scope.Server.RpcAsync("dcheck.get", new { druleids = new[] { druleId }, output = new[] { "dcheckid" } });
        ulong dcheckId = ulong.Parse(check[0].GetProperty("dcheckid").GetString()!, System.Globalization.CultureInfo.InvariantCulture);

        IHost app = await LiveHosting.StartAsync(services => services.AddZabbixActiveProxy(proxyName)
            .AddServer(new System.Net.DnsEndPoint(trapper.Host, trapper.Port))
            .AddTrapper(0, o => o.Address = "127.0.0.1")
            .AllowPeer(System.Net.IPAddress.Loopback)
            .Configure(o => { o.ConfigInterval = TimeSpan.FromSeconds(1); o.DataInterval = TimeSpan.FromSeconds(1); }));
        try
        {
            ZabbixActiveProxy proxy = app.Services.GetRequiredKeyedService<ZabbixActiveProxy>(proxyName);
            await LiveApi.EventuallyAsync(() => Task.FromResult(proxy.Config.ConfigRevision > 0 ? "configured" : null), "the proxy's first configuration");
            long clock = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ulong ruleId = ulong.Parse(druleId, System.Globalization.CultureInfo.InvariantCulture);
            // Upstream's row protocol (proxy.c process_services): the IP's service rows, then one row with dcheck 0 that
            // closes the host; without it the server parks the services in proxy_dhistory and waits.
            await proxy.DiscoveryStore!.WriteAsync(new ZabbixProxyDiscoveryItem(DateTimeOffset.FromUnixTimeSeconds(clock), ruleId, dcheckId, "192.0.2.10", "", 10050, "Linux zbx", 0), TestContext.Current.CancellationToken);
            await proxy.DiscoveryStore!.WriteAsync(new ZabbixProxyDiscoveryItem(DateTimeOffset.FromUnixTimeSeconds(clock), ruleId, 0UL, "192.0.2.10", "", 0, "", 0), TestContext.Current.CancellationToken);

            JsonElement found = await LiveApi.EventuallyAsync<JsonElement?>(async () =>
            {
                JsonElement rows = await scope.Server.RpcAsync("dservice.get", new { druleids = new[] { druleId }, output = new[] { "ip", "port", "value", "status" } });
                return rows.GetArrayLength() > 0 ? rows[0] : null;
            }, "the discovered service in dservice.get") ?? default;

            Assert.Equal("192.0.2.10", found.GetProperty("ip").GetString());
            Assert.Equal("10050", found.GetProperty("port").GetString());
            Assert.Equal("Linux zbx", found.GetProperty("value").GetString());
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task The_server_pushes_configuration_to_the_passive_proxy_and_polls_its_data(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "passive-proxy");
        string proxyName = scope.Name("passive-proxy");
        IHost app = await LiveHosting.StartAsync(services => services.AddZabbixPassiveProxy(proxyName)
            .AddTrapper(0, o => o.Address = LiveHosting.ListenAddress)
            .AllowPeer(System.Net.IPNetwork.Parse("0.0.0.0/0"))
            .AllowPeer(System.Net.IPNetwork.Parse("::/0"))
            .WithVersion(version));                                                            // the server's major.minor: it never tells a passive proxy
        try
        {
            ZabbixPassiveProxy proxy = app.Services.GetRequiredKeyedService<ZabbixPassiveProxy>(proxyName);
            int port = ((IPEndPoint)proxy.Listener!.LocalEndPoint!).Port;
            (_, string proxyId) = await ProxyAsync(scope, "passive-proxy", active: false, port: port, name: proxyName);
            LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "passive-proxied", [("zbxtest.trap", ZabbixItemValueType.Float)], r => r with { MonitoredBy = ZabbixMonitoredBy.Proxy, ProxyId = ulong.Parse(proxyId, System.Globalization.CultureInfo.InvariantCulture) });
            LiveFixtures.LiveItem probe = await LiveFixtures.ProxyProbeAsync(scope, host);

            await LiveApi.EventuallyAsync(() => Task.FromResult(proxy.Config.HostsByName.ContainsKey(host.Host) ? "configured" : null), $"the server to push a configuration holding {host.Host}", TimeSpan.FromSeconds(90));
            using ZabbixSender sender = new ZabbixSender("127.0.0.1", port);
            await LiveFixtures.UntilProxiedValuesAreStoredAsync(scope, host, probe, sender);
            ZabbixSenderResponse sent = await LiveFixtures.SendWhenKnownAsync(sender, [new ZabbixValue(host.Host, "zbxtest.trap", "6.5", DateTimeOffset.UtcNow)]);

            Assert.Equal((1, 0), (sent.Processed, sent.Failed));
            Assert.Equal("6.5", (await LiveFixtures.HistoryAsync(scope.Server, host["zbxtest.trap"], 1))[0].Value);
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    /// <summary>A proxy on the server: active (it connects in), or passive at <see cref="LiveMatrix.HostFromServer"/>:<paramref name="port"/>.</summary>
    private static async Task<(string Name, string Id)> ProxyAsync(LiveScope scope, string what, bool active, int port = 0, string? name = null)
    {
        name ??= scope.Name(what);
        JsonElement created = active
            ? await scope.Server.RpcAsync("proxy.create", new { name, operating_mode = 0 })
            : await scope.Server.RpcAsync("proxy.create", new { name, operating_mode = 1, address = LiveMatrix.HostFromServer, port = port.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        string id = created.Id("proxyids");
        scope.Delete("proxy", id);
        return (name, id);
    }
}
