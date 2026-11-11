// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Text.Json;
using Community.Zabbix.Listening;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Community.Zabbix.Tests;

/// <summary>
/// The agents' edges against every live server: what the server shows for a handler that throws and for a key nobody
/// answers, and handlers registered in two calls for one agent (two keyed sets, merged when the agent starts).
/// </summary>
public sealed class LiveAgentEdgeTests
{
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task An_active_agent_s_throwing_handler_makes_its_item_not_supported_with_the_handler_s_message_and_the_rest_still_deliver(string version)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "agent-throws");
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "agent-throws", []);
        LiveFixtures.LiveItem broken = await LiveFixtures.ItemAsync(scope, host, "zbxtest.broken", ZabbixItemValueType.Unsigned, ZabbixItemType.ZabbixActive, ""","delay":"1s" """);
        LiveFixtures.LiveItem answered = await LiveFixtures.ItemAsync(scope, host, "zbxtest.answer", ZabbixItemValueType.Unsigned, ZabbixItemType.ZabbixActive, ""","delay":"1s" """);

        IHost app = await LiveHosting.StartAsync(services => services.AddZabbixActiveAgent("zbx")
            .AddServer(new DnsEndPoint(trapper.Host, trapper.Port))
            .AddItem("zbxtest.broken", string (_) => throw new InvalidOperationException("The probe's device is gone."))
            .AddItem("zbxtest.answer", _ => "42")
            .Configure(o =>
            {
                o.Hostname = host.Host;
                o.RefreshInterval = TimeSpan.FromSeconds(2);
                o.Send.FlushInterval = TimeSpan.FromSeconds(1);
            }));
        try
        {
            string error = await NotSupportedAsync(scope, broken);
            IReadOnlyList<ZabbixHistoryRecord> values = await LiveFixtures.HistoryAsync(scope.Server, answered, 1);

            Assert.Contains("The probe's device is gone", error, StringComparison.Ordinal);
            Assert.Equal("42", values[0].Value);
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task The_server_polling_a_passive_agent_for_a_key_it_does_not_answer_marks_the_item_not_supported(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "passive-unknown");
        IHost app = await LiveHosting.StartAsync(services => services.AddZabbixPassiveAgent("zbx")
            .Configure(o =>
            {
                o.Address = LiveHosting.ListenAddress;
                o.Port = 0;
                o.AllowedPeers.Add("0.0.0.0/0");
                o.AllowedPeers.Add("::/0");
            })
            .AddItem("zbxtest.passive", _ => "17"));
        try
        {
            ZabbixListener listener = app.Services.GetRequiredKeyedService<ZabbixPassiveAgent>("zbx").Listener!;
            int port = ((IPEndPoint)listener.LocalEndPoint!).Port;
            LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "passive-unknown", [], r => r with
            {
                Interfaces = [new ZabbixHostInterfaceRequest { Type = ZabbixInterfaceType.Agent, Main = ZabbixInterfacePriority.Primary, UseIp = ZabbixHostInterfaceUseIp.Dns, Ip = "", Dns = LiveMatrix.HostFromServer, Port = port.ToString(System.Globalization.CultureInfo.InvariantCulture) }],
            });
            JsonElement interfaces = await scope.Server.RpcAsync("hostinterface.get", new { hostids = new[] { host.HostId }, output = new[] { "interfaceid" } });
            string interfaceId = interfaces[0].GetProperty("interfaceid").GetString()!;
            LiveFixtures.LiveItem unknown = await LiveFixtures.ItemAsync(scope, host, "zbxtest.nobody[1]", ZabbixItemValueType.Unsigned, ZabbixItemType.Zabbix, $$""","delay":"1s","interfaceid":"{{interfaceId}}" """);
            LiveFixtures.LiveItem polled = await LiveFixtures.ItemAsync(scope, host, "zbxtest.passive", ZabbixItemValueType.Unsigned, ZabbixItemType.Zabbix, $$""","delay":"1s","interfaceid":"{{interfaceId}}" """);

            string error = await NotSupportedAsync(scope, unknown);
            IReadOnlyList<ZabbixHistoryRecord> values = await LiveFixtures.HistoryAsync(scope.Server, polled, 1);

            Assert.Contains("This agent does not answer the key", error, StringComparison.Ordinal);
            Assert.Equal("17", values[0].Value);                                            // the interface stays usable
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task Handlers_registered_for_one_active_agent_in_two_calls_both_deliver(string version)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "agent-two-calls");
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "agent-two-calls", []);
        LiveFixtures.LiveItem first = await LiveFixtures.ItemAsync(scope, host, "zbxtest.first", ZabbixItemValueType.Unsigned, ZabbixItemType.ZabbixActive, ""","delay":"1s" """);
        LiveFixtures.LiveItem second = await LiveFixtures.ItemAsync(scope, host, "zbxtest.second", ZabbixItemValueType.Unsigned, ZabbixItemType.ZabbixActive, ""","delay":"1s" """);

        IHost app = await LiveHosting.StartAsync(services =>
        {
            services.AddZabbixActiveAgent("zbx")
                .AddServer(new DnsEndPoint(trapper.Host, trapper.Port))
                .AddItem("zbxtest.first", _ => "1")
                .Configure(o =>
                {
                    o.Hostname = host.Host;
                    o.RefreshInterval = TimeSpan.FromSeconds(2);
                    o.Send.FlushInterval = TimeSpan.FromSeconds(1);
                });
            services.AddZabbixActiveAgent("zbx").AddItem("zbxtest.second", _ => "2");   // a second module adds its key
        });
        try
        {
            IReadOnlyList<ZabbixHistoryRecord> firsts = await LiveFixtures.HistoryAsync(scope.Server, first, 1);
            IReadOnlyList<ZabbixHistoryRecord> seconds = await LiveFixtures.HistoryAsync(scope.Server, second, 1);

            Assert.Equal("1", firsts[0].Value);
            Assert.Equal("2", seconds[0].Value);
        }
        finally
        {
            await app.ShutdownAsync();
        }
    }

    // item.state 1 is not supported; item.error is the reason the agent (or the server) gave.
    private static Task<string> NotSupportedAsync(LiveScope scope, LiveFixtures.LiveItem item)
        => LiveApi.EventuallyAsync(async () =>
        {
            JsonElement rows = await scope.Server.RpcAsync("item.get", new { itemids = new[] { item.ItemId }, output = new[] { "state", "error" } });
            return rows[0].GetProperty("state").GetString() == "1" ? rows[0].GetProperty("error").GetString() : null;
        }, $"item {item.Key} not supported");
}
