// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Net;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// Shared provisioning over the typed <c>Hosts</c>/<c>HostInterfaces</c>/<c>Items</c> clients, idempotent by host
/// name: a second call reuses whatever already exists rather than reshaping it,
/// and only ever removes what a <see cref="ZabbixAgentHostRecord"/> says this bookkeeping itself put there.
/// </summary>
internal static class ZabbixAgentHostProvisioning
{
    /// <summary>The host group every agent host lands in unless told otherwise.</summary>
    public const string DefaultGroup = "PowerShell agents";

    /// <summary>The agent port Zabbix's own agent listens on by default.</summary>
    public const int DefaultInterfacePort = 10050;

    /// <summary>
    /// The value type of a created item whose key is not one of the built-in four, unless <c>-ValueType</c> says
    /// otherwise: text, which stores whatever a handler answers (a number too), where a numeric type turns the item
    /// unsupported on the first answer that is not a number.
    /// </summary>
    public const ZabbixItemValueType DefaultValueType = ZabbixItemValueType.Text;

    /// <summary>
    /// The value type an item created for <paramref name="key"/> gets: a built-in key's own (<c>agent.ping</c>
    /// unsigned; <c>agent.version</c>, <c>agent.hostname</c> and <c>system.uname</c> character), any other key
    /// <paramref name="valueType"/>.
    /// </summary>
    /// <remarks>
    /// The built-in four's types are those Zabbix's own agent templates ("Linux by Zabbix agent", "Windows by Zabbix
    /// agent") give them. A built-in key always gets its own, whatever <c>-ValueType</c> says: agent.version's "7.4.0"
    /// sent to a numeric item is "not suitable for value type Numeric (unsigned)", and the item goes unsupported.
    /// </remarks>
    internal static ZabbixItemValueType ValueTypeOf(string key, ZabbixItemValueType valueType) => key switch
    {
        "agent.ping" => ZabbixItemValueType.Unsigned,
        "agent.version" or "agent.hostname" or "system.uname" => ZabbixItemValueType.Character,
        _ => valueType,
    };

    /// <summary>
    /// Ensures the host and its agent interface exist, reusing either that already does. The interface is reached at
    /// <paramref name="interfaceAddress"/>: an IP address, or a DNS name the server resolves (<c>useip</c> 0).
    /// </summary>
    public static async Task<ZabbixAgentHostRecord> EnsureHostAsync(
        ZabbixClient client, string hostName, string hostGroup, string interfaceAddress, int interfacePort, CancellationToken cancellationToken)
    {
        IReadOnlyList<ZabbixHost> existing = await client.Hosts.GetAsync(new ZabbixHostGetParameters
        {
            Filter = new() { ["host"] = hostName },
            SelectInterfaces = ZabbixOutput.Extend,
        }, cancellationToken).ConfigureAwait(false);

        if (existing.Count > 0)
        {
            ZabbixHost host = existing[0];
            ulong hostId = host.HostId!.Value;
            ulong? interfaceId = host.Interfaces?.FirstOrDefault(candidate => candidate.Type == ZabbixInterfaceType.Agent)?.InterfaceId;
            bool interfaceCreated = false;
            if (interfaceId is null)
            {
                interfaceId = await client.HostInterfaces.CreateAsync(hostId, AgentInterface(interfaceAddress, interfacePort), cancellationToken).ConfigureAwait(false);
                interfaceCreated = true;
            }
            return new ZabbixAgentHostRecord(hostId, hostName, interfaceId, hostCreated: false, interfaceCreated);
        }

        ulong groupId = await EnsureGroupAsync(client, hostGroup, cancellationToken).ConfigureAwait(false);
        ulong createdHostId = await client.Hosts.CreateAsync(hostName, [groupId], host => host.Interfaces = [AgentInterface(interfaceAddress, interfacePort)], cancellationToken).ConfigureAwait(false);

        IReadOnlyList<ZabbixHostInterface> interfaces = await client.HostInterfaces.GetAsync(
            new ZabbixHostInterfaceGetParameters { HostIds = [createdHostId] }, cancellationToken).ConfigureAwait(false);
        return new ZabbixAgentHostRecord(createdHostId, hostName, interfaces.Count > 0 ? interfaces[0].InterfaceId : null, hostCreated: true, interfaceCreated: true);
    }

    /// <summary>Ensures the host group named <paramref name="name"/> exists and returns its id.</summary>
    public static async Task<ulong> EnsureGroupAsync(ZabbixClient client, string name, CancellationToken cancellationToken)
    {
        IReadOnlyList<ZabbixHostGroup> existing = await client.HostGroups.GetAsync(new ZabbixHostGroupGetParameters
        {
            Filter = new() { ["name"] = name },
        }, cancellationToken).ConfigureAwait(false);
        if (existing.Count > 0) return existing[0].GroupId!.Value;

        return await client.HostGroups.CreateAsync(name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Ensures one item per distinct key in <paramref name="keys"/> on <paramref name="hostId"/>, reusing an item whose
    /// key already matches and creating the rest as <paramref name="type"/> (<see cref="ItemTypeOf"/>: a passive
    /// agent's keys as Zabbix agent items, polled on <paramref name="interfaceId"/>, an active agent's as active
    /// checks, which name no interface), each with the value type <see cref="ValueTypeOf"/> gives its key.
    /// </summary>
    /// <remarks>
    /// The conditionally-required fields send the API's own unset value, since the request record's <c>required</c>
    /// members do not accept <see langword="null"/>: <c>""</c> for the plain-text <c>url</c>/<c>snmp_oid</c>, but
    /// <c>"0"</c> for the id reference <c>master_itemid</c> (Zabbix 7.0 answers <c>""</c> with "a number is
    /// expected"). <c>value_type</c> is always sent: 8.0's <c>CItemGeneral.php</c> requires it on every branch.
    /// </remarks>
    /// <param name="client">The server.</param>
    /// <param name="hostId">The host.</param>
    /// <param name="keys">The item keys.</param>
    /// <param name="valueType">The value type of a created item whose key is not a built-in one.</param>
    /// <param name="type">The item type of a created item.</param>
    /// <param name="interfaceId">The agent interface a polled item is checked on; the server requires one for a Zabbix agent item.</param>
    /// <param name="cancellationToken">The operation's token.</param>
    public static async Task<Dictionary<string, ulong>> EnsureItemsAsync(
        ZabbixClient client, ulong hostId, IReadOnlyList<string> keys, ZabbixItemValueType valueType, ZabbixItemType type, ulong? interfaceId, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, ulong>(StringComparer.Ordinal);
        if (keys.Count == 0) return result;

        IReadOnlyList<ZabbixItem> existing = await client.Items.GetAsync(
            new ZabbixItemGetParameters { HostIds = [hostId] }, cancellationToken).ConfigureAwait(false);
        Dictionary<string, ulong> byKey = new(StringComparer.Ordinal);
        foreach (ZabbixItem item in existing)
            if (item.Key is { } key && item.ItemId is { } id) byKey[key.ToString()] = id;

        foreach (string key in keys)
        {
            if (byKey.TryGetValue(key, out ulong itemId))
            {
                result[key] = itemId;
                continue;
            }

            ulong created = await client.Items.CreateAsync(new ZabbixItemCreateRequest
            {
                HostId = hostId,
                Name = DeriveName(key),
                Type = type,
                Key = ZabbixItemKey.Parse(key),
                InterfaceId = type == ZabbixItemType.Zabbix ? interfaceId : null,
                ValueType = ValueTypeOf(key, valueType),
                Delay = TimeSpan.FromMinutes(1),
                // master_itemid is an id reference: Zabbix 7.0 accepts "0" (unset) but rejects "" ("a number is
                // expected"), unlike url/snmp_oid, which are plain text fields.
                MasterItemId = 0UL,
                Url = string.Empty,
                SnmpOid = string.Empty,
            }, cancellationToken).ConfigureAwait(false);
            result[key] = byKey[key] = created;
        }
        return result;
    }

    /// <summary>The item type a session's keys are checked with: a passive agent is polled (Zabbix agent), an active one sends (active checks).</summary>
    internal static ZabbixItemType ItemTypeOf(ZabbixAgentSession? session) => session?.Kind == "passive" ? ZabbixItemType.Zabbix : ZabbixItemType.ZabbixActive;

    /// <summary>Removes the items in <paramref name="record"/> not in <paramref name="stillHandled"/> and stops tracking them.</summary>
    public static async Task RemoveStaleAsync(ZabbixClient client, ZabbixAgentHostRecord record, IReadOnlyList<string> stillHandled, CancellationToken cancellationToken)
    {
        var keep = new HashSet<string>(stillHandled, StringComparer.Ordinal);
        List<ulong> stale = [.. record.ItemIds.Where(pair => !keep.Contains(pair.Key)).Select(pair => pair.Value)];
        if (stale.Count == 0) return;
        await client.Items.DeleteAsync(stale, cancellationToken).ConfigureAwait(false);
        foreach (string key in record.ItemIds.Where(pair => stale.Contains(pair.Value)).Select(pair => pair.Key).ToList())
            record.ItemIds.Remove(key);
    }

    /// <summary>Removes everything <paramref name="record"/> says this bookkeeping created: its items always, its interface and host only if it created them.</summary>
    public static async Task ClearAsync(ZabbixClient client, ZabbixAgentHostRecord record, CancellationToken cancellationToken)
    {
        if (record.ItemIds.Count > 0)
            await client.Items.DeleteAsync([.. record.ItemIds.Values], cancellationToken).ConfigureAwait(false);
        record.ItemIds.Clear();
        if (record.HostCreated)
        {
            // Deleting the host removes its interfaces with it; no separate interface delete needed.
            await client.Hosts.DeleteAsync([record.HostId], cancellationToken).ConfigureAwait(false);
        }
        else if (record.InterfaceCreated && record.InterfaceId is { } interfaceId)
        {
            await client.HostInterfaces.DeleteAsync([interfaceId], cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Builds the status object <c>Initialize-</c>/<c>Publish-</c>/<c>Get-ZbxAgentHost</c> emit with <c>-PassThru</c>.</summary>
    public static async Task<ZabbixAgentHostStatus> ReadStatusAsync(ZabbixClient client, ulong hostId, IReadOnlySet<string>? localHandlerKeys, CancellationToken cancellationToken)
    {
        IReadOnlyList<ZabbixHost> hosts = await client.Hosts.GetAsync(new ZabbixHostGetParameters
        {
            HostIds = [hostId],
            SelectItems = ZabbixOutput.Extend,
            SelectInterfaces = ZabbixOutput.Extend,
        }, cancellationToken).ConfigureAwait(false);
        if (hosts.Count == 0) throw new ItemNotFoundException($"host '{hostId}' no longer exists on the server");
        ZabbixHost host = hosts[0];
        List<ZabbixAgentHostItemStatus> items =
        [
            .. (host.Items ?? []).Where(item => item.Key is not null && item.ItemId is not null)
                .Select(item => new ZabbixAgentHostItemStatus(item.Key!.Value.ToString(), item.ItemId!.Value, localHandlerKeys?.Contains(item.Key!.Value.ToString()) ?? false)),
        ];
        return new ZabbixAgentHostStatus(host, items);
    }

    // An agent interface: an IP address is polled as one (useip 1), anything else as a DNS name the server resolves
    // (useip 0, ip empty), as the frontend's host form sends them. The first of its type on a host is the main one.
    private static ZabbixHostInterfaceRequest AgentInterface(string address, int port)
        => IPAddress.TryParse(address, out IPAddress? ip) ? ZabbixInterface.Agent(ip, port) : ZabbixInterface.Agent(address, port);

    /// <summary>A readable item name from a key: <c>ops.queue.depth</c> -&gt; <c>Ops queue depth</c>.</summary>
    private static string DeriveName(string key)
    {
        int bracket = key.IndexOf('[', StringComparison.Ordinal);
        string stem = bracket >= 0 ? key[..bracket] : key;
        string words = stem.Replace('.', ' ').Replace('_', ' ').Trim();
        if (words.Length == 0) return key;
        return char.ToUpperInvariant(words[0]) + words[1..];
    }
}
