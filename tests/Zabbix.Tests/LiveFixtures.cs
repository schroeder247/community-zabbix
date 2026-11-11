// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;

namespace Community.Zabbix.Tests;

/// <summary>What a live scenario builds on a server: a group, a host in it, and the host's items.</summary>
internal static class LiveFixtures
{
    /// <summary>A trapper item on the fixture host: its key, value type and id.</summary>
    internal sealed record LiveItem(string Key, ZabbixItemValueType Type, ulong ItemId);

    /// <summary>The group, the host and its items, all deleted by the scope.</summary>
    internal sealed record LiveHost(ulong GroupId, ulong HostId, string Host, IReadOnlyList<LiveItem> Items)
    {
        public LiveItem this[string key] => Items.Single(i => i.Key == key);
    }

    /// <summary>
    /// Creates a group and a host through the typed clients (so every scenario also exercises them), then its items as
    /// raw JSON, so a scenario's items do not depend on the typed <see cref="ZabbixItemCreateRequest"/> (whose shape for a
    /// trapper item <c>LiveDefectTests</c> checks on its own).
    /// </summary>
    public static async Task<LiveHost> HostAsync(LiveScope scope, string what, IReadOnlyList<(string Key, ZabbixItemValueType Type)> items, Func<ZabbixHostCreateRequest, ZabbixHostCreateRequest>? shape = null, string? itemExtra = null)
    {
        CancellationToken cancel = TestContext.Current.CancellationToken;
        IReadOnlyList<ulong> groups = await scope.Server.HostGroups.CreateAsync([new ZabbixHostGroupCreateRequest { Name = scope.Name(what + "-group") }], cancel);
        scope.Delete("hostgroup", groups);
        string host = scope.Name(what);
        var request = new ZabbixHostCreateRequest { TechnicalName = host, Groups = [new ZabbixHostGroupRequest { GroupId = groups[0] }] };
        IReadOnlyList<ulong> hosts = [await scope.Server.Hosts.CreateAsync(shape is null ? request : shape(request), cancel)];
        scope.Delete("host", hosts);
        var created = new List<LiveItem>();
        foreach ((string key, ZabbixItemValueType type) in items)
        {
            // trapper_hosts is explicit, never inherited: 8.0 defaults it to {$TRAPPER.ALLOWED_HOSTS} = "127.0.0.1,::1",
            // and rootless podman presents this host to the server as its LAN address (LiveServerFactTests pins both).
            string json = string.Create(CultureInfo.InvariantCulture, $$"""{"hostid":"{{hosts[0]}}","name":"{{scope.Name(key)}}","key_":"{{key}}","type":2,"value_type":{{(int)type}},"trapper_hosts":"0.0.0.0/0,::/0"{{itemExtra}}}""");
            JsonElement result = await scope.Server.RpcJsonAsync("item.create", json);
            created.Add(new LiveItem(key, type, result.IdOf("itemids")));
        }
        return new LiveHost(groups[0], hosts[0], host, created);
    }

    /// <summary>One more item on <paramref name="host"/>, of any type; <paramref name="extra"/> is raw JSON members (<c>,"delay":"1s"</c>).</summary>
    public static async Task<LiveItem> ItemAsync(LiveScope scope, LiveHost host, string key, ZabbixItemValueType valueType, ZabbixItemType type, string extra = "")
    {
        string json = string.Create(CultureInfo.InvariantCulture, $$"""{"hostid":"{{host.HostId}}","name":"{{scope.Name(key)}}","key_":"{{key}}","type":{{(int)type}},"value_type":{{(int)valueType}}{{extra}}}""");
        JsonElement result = await scope.Server.RpcJsonAsync("item.create", json);
        return new LiveItem(key, valueType, result.IdOf("itemids"));
    }

    /// <summary>
    /// An item for <see cref="UntilProxiedValuesAreStoredAsync"/>. Create it last: a cache that holds it holds what came before.
    /// </summary>
    public static Task<LiveItem> ProxyProbeAsync(LiveScope scope, LiveHost host)
        => ItemAsync(scope, host, "zbxtest.probe", ZabbixItemValueType.Unsigned, ZabbixItemType.Trapper, ""","trapper_hosts":"0.0.0.0/0" """);

    /// <summary>
    /// Waits until the server stores what a proxy uploads for <paramref name="host"/>. A proxy can hold an item before the
    /// server's configuration cache does: <c>proxy config</c> reads a host's items from the database once the cache holds the
    /// host, and a sync can fall between creating the host and creating its item. Until the cache holds the item, the server
    /// answers an upload with success, drops the value and records its id, so sending the value again doesn't help. The
    /// proxy accepting a value says only that the proxy knows the item, so this sends <paramref name="probe"/> until one is in
    /// history.
    /// </summary>
    public static Task UntilProxiedValuesAreStoredAsync(LiveScope scope, LiveHost host, LiveItem probe, ZabbixSender sender)
        => LiveApi.EventuallyAsync(async () =>
        {
            await SendWhenKnownAsync(sender, [new ZabbixValue(host.Host, probe.Key, "1", DateTimeOffset.UtcNow)]);
            IReadOnlyList<ZabbixHistoryRecord> rows = await scope.Server.History.GetAsync(new ZabbixHistoryGetParameters { History = probe.Type, ItemIds = [probe.ItemId], Output = ZabbixOutput.Extend }, TestContext.Current.CancellationToken);
            return rows.Count > 0 ? rows : null;
        }, "the server to store a probe value through the proxy");

    /// <summary>
    /// Sends <paramref name="values"/> once the server's configuration cache has their items. The first value of each item
    /// goes alone until it is taken: a reply that refused it is the cache not yet holding that item (nothing was taken, so
    /// a re-send duplicates nothing), and items created one after another can reach the cache in different syncs. The rest
    /// then go in one call, which the sender splits into its frames; the answer is the replies summed. Probing item by item
    /// keeps the wait honest when the rest takes several frames or several items. Over TLS a handshake the server refuses
    /// is the same wait: until the cache holds the host, the server knows neither its PSK identity nor its certificate
    /// policy.
    /// </summary>
    public static async Task<ZabbixSenderResponse> SendWhenKnownAsync(ZabbixSender sender, IReadOnlyCollection<ZabbixValue> values)
    {
        ZabbixValue[] all = [.. values];
        int[] probes = [.. all.Select((value, index) => (Item: (value.Host, Key: value.Key.ToString()), Index: index))
            .GroupBy(entry => entry.Item).Select(group => group.First().Index)];
        List<ZabbixSenderResponse> answers = [];
        foreach (int probe in probes) answers.Add(await ProbeAsync(sender, all[probe]));
        ZabbixValue[] rest = [.. all.Where((_, index) => !probes.Contains(index))];
        if (rest.Length > 0) answers.Add(await sender.SendAsync(rest, TestContext.Current.CancellationToken));
        return ZabbixSenderResponse.Sum(answers);
    }

    private static async Task<ZabbixSenderResponse> ProbeAsync(ZabbixSender sender, ZabbixValue value)
    {
        Exception? last = null;
        try
        {
            return await LiveApi.EventuallyAsync<ZabbixSenderResponse>(async () =>
            {
                try
                {
                    ZabbixSenderResponse response = await sender.SendAsync([value], TestContext.Current.CancellationToken);
                    return response.Processed == 0 && response.Failed == response.Total ? null : response;
                }
                catch (System.Security.Authentication.AuthenticationException refused)
                {
                    last = refused;
                    return null;
                }
            }, $"the trapper to know {value.Host} {value.Key}", TimeSpan.FromSeconds(30));
        }
        catch (Xunit.Sdk.FailException) when (last is not null)
        {
            throw new System.Security.Authentication.AuthenticationException("the handshake was still refused after the configuration cache had time to load the host: " + last.Message, last);
        }
    }

    /// <summary>Polls <c>history.get</c> until <paramref name="count"/> values of <paramref name="item"/> are stored, in (clock, ns) order.</summary>
    public static Task<IReadOnlyList<ZabbixHistoryRecord>> HistoryAsync(ZabbixClient client, LiveItem item, int count, TimeSpan? budget = null)
        => LiveApi.EventuallyAsync<IReadOnlyList<ZabbixHistoryRecord>>(async () =>
        {
            IReadOnlyList<ZabbixHistoryRecord> rows = await client.History.GetAsync(new ZabbixHistoryGetParameters
            {
                History = item.Type,
                ItemIds = [item.ItemId],
                Output = ZabbixOutput.Extend,
                Sort = [ZabbixHistoryRecordFields.Clock, ZabbixHistoryRecordFields.ClockNanoseconds],
            }, TestContext.Current.CancellationToken);
            return rows.Count >= count ? rows : null;
        }, $"{count} value(s) of {item.Key} in history.get", budget);
}
