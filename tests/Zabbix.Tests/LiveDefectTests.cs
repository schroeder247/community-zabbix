// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using Community.Zabbix.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Community.Zabbix.Tests;

/// <summary>
/// Regression tests, against real servers: a typed request must not demand another item type's fields, a discovery row
/// the server cannot read must not wedge the proxy's uploads, and a watcher must not drop values collected before its first
/// poll. Each reproduces the scenario on every lab server and fails if the behaviour returns. They run only in the live
/// matrix, not in the default suite.
/// </summary>
public sealed class LiveDefectTests
{
    /// <summary>
    /// A trapper item needs no <c>master_itemid</c>, <c>url</c> or <c>snmp_oid</c>: every server creates one without
    /// them. The typed request therefore marks none of the three <c>required</c>, so the typed client creates a trapper,
    /// agent or calculated item without inventing values for another item type's fields.
    /// </summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_trapper_item_needs_no_field_of_another_item_type_and_the_typed_request_demands_none(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "trap-fields");
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "trap-fields", [("zbxtest.trap", ZabbixItemValueType.Float)]);   // raw JSON: no master_itemid, url or snmp_oid

        string[] demanded = [.. typeof(ZabbixItemCreateRequest).GetProperties()
            .Where(p => p.GetCustomAttribute<RequiredMemberAttribute>() is not null)
            .Select(p => p.Name)
            .Where(name => name is "MasterItemId" or "Url" or "SnmpOid")];

        Assert.NotEmpty(host.Items);
        Assert.Empty(demanded);
    }

    /// <summary>
    /// Every server refuses a <c>proxy data</c> message with a discovery row it cannot read ("received invalid proxy
    /// data … cannot find pair with name …", <c>proxy.c</c> process_discovery_data_contents), after it stored the history
    /// that came before the row. <see cref="ZabbixProxyDiscoveryItem.NetworkDiscoveryCheckId"/> is a <see langword="long"/>, so a row
    /// without its <c>dcheck</c> cannot be written; the test writes the other row the server cannot read, one without its
    /// <c>ip</c>, and requires the proxy to take the refused page apart rather than re-send it every turn, so the history
    /// written after it is still uploaded.
    /// </summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_discovery_row_the_type_allows_does_not_wedge_the_proxy_s_history(string version)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "discovery-row");
        string proxyName = scope.Name("proxy");
        string proxyId = (await scope.Server.RpcAsync("proxy.create", new { name = proxyName, operating_mode = 0 })).Id("proxyids");
        scope.Delete("proxy", proxyId);
        // The server reads a row's dcheck only for a rule this proxy monitors (a row of any other rule is skipped first).
        string druleId = (await scope.Server.RpcAsync("drule.create", new { name = scope.Name("drule"), iprange = "192.0.2.11", proxyid = proxyId, delay = "1h", dchecks = new[] { new { type = 9, key_ = "system.uname", ports = "10050", uniq = 0 } } })).Id("druleids");
        scope.Delete("drule", druleId);
        // Created after the rule: once the proxy's configuration holds the host, the server's cache holds the rule.
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "discovery-row", [("zbxtest.trap", ZabbixItemValueType.Float)], r => r with { MonitoredBy = ZabbixMonitoredBy.Proxy, ProxyId = ulong.Parse(proxyId, System.Globalization.CultureInfo.InvariantCulture) });
        LiveFixtures.LiveItem probe = await LiveFixtures.ProxyProbeAsync(scope, host);

        IHost app = await LiveHosting.StartAsync(services => services.AddZabbixActiveProxy(proxyName)
            .WithServer(new System.Net.DnsEndPoint(trapper.Host, trapper.Port))
            .AddTrapper(0, o => o.Address = "127.0.0.1")
            .AllowPeer(System.Net.IPAddress.Loopback)
            .Configure(o => { o.ConfigInterval = TimeSpan.FromSeconds(1); o.DataInterval = TimeSpan.FromSeconds(1); o.DrainTimeout = TimeSpan.FromSeconds(1); o.PageSize = 1; }));
        try
        {
            ZabbixActiveProxy proxy = app.Services.GetRequiredKeyedService<ZabbixActiveProxy>(proxyName);
            await LiveApi.EventuallyAsync(() => Task.FromResult(proxy.Config.HostsByName.ContainsKey(host.Host) ? "configured" : null), $"the proxy's configuration to hold {host.Host}");
            using var sender = new ZabbixSender("127.0.0.1", ((IPEndPoint)proxy.Trapper!.LocalEndPoint!).Port);
            await LiveFixtures.UntilProxiedValuesAreStoredAsync(scope, host, probe, sender);
            await proxy.DiscoveryStore!.WriteAsync(new ZabbixProxyDiscoveryItem(DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()), ulong.Parse(druleId, System.Globalization.CultureInfo.InvariantCulture), 0UL, null!, null, 0, null, 0), TestContext.Current.CancellationToken);
            await LiveFixtures.SendWhenKnownAsync(sender, [new ZabbixValue(host.Host, "zbxtest.trap", "8.5", DateTimeOffset.UtcNow)]);
            await LiveFixtures.HistoryAsync(scope.Server, host["zbxtest.trap"], 1);                // the server stores the history of the refused message
            await sender.SendAsync([new ZabbixValue(host.Host, "zbxtest.trap", "9.5", DateTimeOffset.UtcNow)], TestContext.Current.CancellationToken);

            // The proxy, told "failed", takes the page apart: the value written after the refused row reaches the server even
            // with a page of one value (PageSize 1), where a proxy that re-sent the whole page would never upload it.
            Assert.Equal(["8.5", "9.5"], (await LiveFixtures.HistoryAsync(scope.Server, host["zbxtest.trap"], 2)).Select(r => r.Value));
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    /// <summary>
    /// A watcher runs from construction and yields every value the watched items collect (<c>IZabbixNode.Watch</c>,
    /// <c>IZabbixWatcher</c>), including the values collected between <c>Watch</c> and its first poll: seeding an item
    /// from <c>item.get</c>'s <c>lastclock</c> and reading history only after it would collapse those values to the latest
    /// one. The test holds the first <c>item.get</c> until three values of one second are in history, so the window is
    /// certain rather than a race.
    /// </summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task Values_collected_between_watch_and_its_first_poll_are_all_yielded(string version)
    {
        (LiveTarget target, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        CancellationToken cancel = TestContext.Current.CancellationToken;
        await using LiveScope scope = LiveScope.Open(version, "watch-window");
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "watch-window", [("zbxtest.src", ZabbixItemValueType.Float)]);
        using var sender = new ZabbixSender([trapper]);
        await LiveFixtures.SendWhenKnownAsync(sender, [new ZabbixValue(host.Host, "zbxtest.src", "0", new ZabbixClock(DateTimeOffset.UtcNow.AddHours(-1).ToUnixTimeSeconds(), 0))]);

        var gate = new ItemGetGate();
        var services = new ServiceCollection();
        services.AddZabbixClient().Configure(options => { options.Endpoint = target.Endpoint; options.ApiToken = target.Token; });
        services.AddHttpClient(ZabbixHttp.GetClientName()).AddHttpMessageHandler(() => new ItemGetGate.Handler(gate));
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using IZabbixWatcher watcher = provider.GetRequiredService<ZabbixClient>().Hosts[host.Host].Items["zbxtest.src"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(1)));
        await using IAsyncEnumerator<ZabbixValue> reader = watcher.GetAsyncEnumerator(cancel);         // attached before anything is sent
        await gate.Held.Task.WaitAsync(cancel);                                                          // the first poll's item.get is in flight

        long second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        ZabbixValue[] sent = [.. Enumerable.Range(1, 3).Select(i => new ZabbixValue(host.Host, "zbxtest.src", (i * 1.5).ToString(System.Globalization.CultureInfo.InvariantCulture), new ZabbixClock(second, i * 111_111_111)))];
        await sender.SendAsync(sent, cancel);
        await LiveFixtures.HistoryAsync(scope.Server, host["zbxtest.src"], sent.Length + 1);             // collected, before the first poll reads
        gate.Release.TrySetResult();

        var yielded = new List<ZabbixClock>();
        try
        {
            while (yielded.Count < sent.Length && await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(15), TimeProvider.System, cancel))
                if (reader.Current.Clock.Seconds >= second) yielded.Add(reader.Current.Clock);
        }
        catch (TimeoutException)
        {
            // what was yielded is what the assertion reports
        }

        Assert.Equal(sent.Select(v => v.Clock), yielded);
    }

    /// <summary>Holds the first <c>item.get</c> until <see cref="Release"/>; <see cref="Held"/> says it is being held.</summary>
    private sealed class ItemGetGate
    {
        private int _first;

        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public sealed class Handler(ItemGetGate gate) : DelegatingHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                string body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                if (body.Contains("\"item.get\"", StringComparison.Ordinal) && Interlocked.Exchange(ref gate._first, 1) == 0)
                {
                    gate.Held.TrySetResult();
                    await gate.Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
