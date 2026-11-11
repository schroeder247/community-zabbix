// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json.Nodes;

namespace Community.Zabbix;

public partial class ZabbixHostGroupClient
{
    /// <remarks>
    /// Not atomic against a concurrent membership edit. The removed <c>hostgroup.massupdate</c> was one server call;
    /// this is two <c>host.get</c> reads (<see cref="ZabbixEmulation.ReplaceMembershipAsync"/>: the named hosts' current
    /// groups, then the group's current members) followed by one batched <c>host.update</c>. A host moved in or out of the
    /// group by someone else between the reads and the write is not seen — the write goes out with the membership computed
    /// from the stale read and can undo that other change. The "would be left without a group" refusal races the same
    /// window: it is checked against the read, before any write, but a concurrent removal after the read is not caught.
    /// </remarks>
    private partial async Task<IReadOnlyList<ulong>> MassUpdateEmulatedAsync(ZabbixHostGroupMassUpdateRequest request, Version server, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> groups = [.. request.Groups.Select(g => g.GroupId.ToString(CultureInfo.InvariantCulture))];
        IReadOnlyList<string> hosts = [.. request.Hosts.Select(h => h.HostId.ToString(CultureInfo.InvariantCulture))];
        JsonArray updates = await ZabbixEmulation.ReplaceMembershipAsync(Transport, "host", "hostid", "hostgroups", groups, hosts, cancellationToken).ConfigureAwait(false);
        if (updates.Count > 0) await ZabbixEmulation.SendAsync(Transport, "host.update", updates, cancellationToken).ConfigureAwait(false);
        return [.. request.Groups.Select(g => g.GroupId)];
    }
}
