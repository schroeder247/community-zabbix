// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Community.Zabbix.Listening;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Community.Zabbix.Tests;

/// <summary>
/// The SDK's agents against every live server: an active agent session (items assigned by the server, collected on their
/// delay, uploaded as <c>agent data</c>, heartbeats making the host's active checks available), the same over a
/// pre-shared key set through the API, and the passive agent polled by the server itself across the pod boundary.
/// </summary>
public sealed class LiveAgentTests
{
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task An_active_agent_session_delivers_its_items_and_its_heartbeat_makes_the_host_available(string version)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "agent");
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "agent", []);
        LiveFixtures.LiveItem answered = await LiveFixtures.ItemAsync(scope, host, "zbxtest.answer", ZabbixItemValueType.Unsigned, ZabbixItemType.ZabbixActive, ""","delay":"1s" """);
        LiveFixtures.LiveItem ping = await LiveFixtures.ItemAsync(scope, host, "agent.ping", ZabbixItemValueType.Unsigned, ZabbixItemType.ZabbixActive, ""","delay":"1s" """);

        IHost app = await LiveHosting.StartAsync(services => services.AddZabbixActiveAgent("zbx")
            .AddServer(new DnsEndPoint(trapper.Host, trapper.Port))
            .AddItem("zbxtest.answer", _ => "42")
            .Configure(o =>
            {
                o.HostName = host.Host;
                o.RefreshInterval = TimeSpan.FromSeconds(2);                               // the host reaches the cache within a refresh or two
                o.HeartbeatInterval = TimeSpan.FromSeconds(2);
                o.Send.FlushInterval = TimeSpan.FromSeconds(1);
            }));
        try
        {
            IReadOnlyList<ZabbixHistoryRecord> values = await LiveFixtures.HistoryAsync(scope.Server, answered, 1);
            IReadOnlyList<ZabbixHistoryRecord> pings = await LiveFixtures.HistoryAsync(scope.Server, ping, 1);
            string available = await ActiveAvailableAsync(scope, host, "1");

            Assert.Equal("42", values[0].Value);
            Assert.Equal("1", pings[0].Value);
            Assert.Equal("1", available);
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    /// <summary>
    /// The killed upload, for the agent: the relay forwards the first <c>agent data</c> that carries values, the server
    /// stores them, and the agent's connection is cut before the reply. The agent must re-send that batch with the same
    /// <c>session</c> and ids, and the server must drop what it already has (<c>active.c</c>: a row whose id is not above
    /// the session's <c>last_id</c> is skipped). What history holds is then one run of the counter, no value twice, none lost.
    /// </summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task An_agent_upload_cut_after_the_server_stored_it_is_re_sent_with_its_ids_and_not_duplicated(string version)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "agent-cut");
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "agent-cut", []);
        LiveFixtures.LiveItem counter = await LiveFixtures.ItemAsync(scope, host, "zbxtest.seq", ZabbixItemValueType.Unsigned, ZabbixItemType.ZabbixActive, ""","delay":"1s" """);
        int cuts = 0, collected = 0;
        // Heartbeats and active-check requests pass; the first agent data (sent only when it carries values) is cut after the server.
        await using var relay = new LiveRelay(trapper, payload => payload.Contains("\"agent data\"", StringComparison.Ordinal) && Interlocked.Exchange(ref cuts, 1) == 0);

        IHost app = await LiveHosting.StartAsync(services => services.AddZabbixActiveAgent("zbx")
            .AddServer(new DnsEndPoint(relay.Node.Host, relay.Node.Port))
            .AddItem("zbxtest.seq", _ => Interlocked.Increment(ref collected).ToString(System.Globalization.CultureInfo.InvariantCulture))
            .Configure(o =>
            {
                o.HostName = host.Host;
                o.RefreshInterval = TimeSpan.FromSeconds(2);
                o.HeartbeatInterval = TimeSpan.FromSeconds(2);
                o.Send.FlushInterval = TimeSpan.FromSeconds(1);
            }));
        try
        {
            await LiveApi.EventuallyAsync(() => Task.FromResult(relay.Dropped > 0 ? "cut" : null), "the relay to cut the first agent data");
            int cutAt = Volatile.Read(ref collected);
            IReadOnlyList<ZabbixHistoryRecord> stored = await LiveApi.EventuallyAsync(async () =>
            {
                IReadOnlyList<ZabbixHistoryRecord> rows = await LiveFixtures.HistoryAsync(scope.Server, counter, 1);
                return rows.Any(r => int.Parse(r.Value!, System.Globalization.CultureInfo.InvariantCulture) > cutAt + 2) ? rows : null;
            }, "values collected after the cut in history.get");

            int[] values = [.. stored.Select(r => int.Parse(r.Value!, System.Globalization.CultureInfo.InvariantCulture))];
            Assert.Equal(Enumerable.Range(values[0], values.Length), values);                   // no value twice, none lost, in order
            Assert.Equal(1, relay.Dropped);
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task An_active_agent_delivers_over_a_pre_shared_key_the_host_was_given_through_the_api(string version)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "agent-psk");
        string identity = scope.Name("identity");
        string psk = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "agent-psk", [],
            r => r with { TlsAccept = ZabbixTlsAcceptModes.Psk, TlsConnect = ZabbixTlsConnectMode.Psk, TlsPskIdentity = identity, TlsPsk = psk });
        LiveFixtures.LiveItem answered = await LiveFixtures.ItemAsync(scope, host, "zbxtest.answer", ZabbixItemValueType.Unsigned, ZabbixItemType.ZabbixActive, ""","delay":"1s" """);

        IHost app = await LiveHosting.StartAsync(services => services.AddZabbixActiveAgent("zbx")
            .AddServer(new DnsEndPoint(trapper.Host, trapper.Port))
            .AddItem("zbxtest.answer", _ => "43")
            .Configure(o =>
            {
                o.HostName = host.Host;
                o.RefreshInterval = TimeSpan.FromSeconds(2);
                o.HeartbeatInterval = TimeSpan.Zero;
                o.Send.FlushInterval = TimeSpan.FromSeconds(1);
                o.Connection.Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = identity, Psk = psk };
            }));
        try
        {
            Assert.Equal("43", (await LiveFixtures.HistoryAsync(scope.Server, answered, 1))[0].Value);
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task The_server_polls_the_passive_agent_across_the_pod_boundary(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "passive-agent");
        IHost app = await LiveHosting.StartAsync(services => services.AddZabbixPassiveAgent("zbx")
            .Configure(o =>
            {
                o.Address = LiveHosting.ListenAddress;
                o.Port = 0;
            })
            .AllowPeer(IPNetwork.Parse("0.0.0.0/0"))                                       // the pod's address as this host sees it varies by network mode
            .AllowPeer(IPNetwork.Parse("::/0"))
            .AddItem("zbxtest.passive", _ => "17"));
        try
        {
            ZabbixListener listener = app.Services.GetRequiredKeyedService<ZabbixPassiveAgent>("zbx").Listener!;
            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "passive-agent", [], r => r with
            {
                Interfaces = [new ZabbixHostInterfaceRequest { Type = ZabbixInterfaceType.Agent, Main = ZabbixInterfacePriority.Primary, UseIP = ZabbixHostInterfaceUseIP.Dns, IP = "", Dns = LiveMatrix.HostFromServer, Port = port }],
            });
            JsonElement interfaces = await scope.Server.RpcAsync("hostinterface.get", new { hostids = new[] { host.HostId }, output = new[] { "interfaceid" } });
            string interfaceId = interfaces[0].GetProperty("interfaceid").GetString()!;
            LiveFixtures.LiveItem polled = await LiveFixtures.ItemAsync(scope, host, "zbxtest.passive", ZabbixItemValueType.Unsigned, ZabbixItemType.Zabbix, $$""","delay":"1s","interfaceid":"{{interfaceId}}" """);

            IReadOnlyList<ZabbixHistoryRecord> values = await LiveFixtures.HistoryAsync(scope.Server, polled, 1);

            Assert.Equal("17", values[0].Value);
            // The availability manager marks the interface after the poll, asynchronously: wait for it, do not read once.
            await LiveApi.EventuallyAsync(async () =>
            {
                JsonElement available = await scope.Server.RpcAsync("hostinterface.get", new { interfaceids = new[] { interfaceId }, output = new[] { "available" } });
                return available[0].GetProperty("available").GetString() == "1" ? "available" : null;
            }, "the agent interface to be available");
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    // host.active_available: 0 unknown, 1 available, 2 not available (7.0+, driven by the agent's active checks and heartbeats).
    private static Task<string> ActiveAvailableAsync(LiveScope scope, LiveFixtures.LiveHost host, string expected)
        => LiveApi.EventuallyAsync(async () =>
        {
            JsonElement rows = await scope.Server.RpcAsync("host.get", new { hostids = new[] { host.HostId }, output = new[] { "active_available" } });
            string? state = rows[0].GetProperty("active_available").GetString();
            return state == expected ? state : null;
        }, $"host {host.Host} active_available = {expected}");
}
