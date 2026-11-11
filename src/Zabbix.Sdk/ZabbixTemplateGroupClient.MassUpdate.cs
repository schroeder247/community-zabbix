// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json.Nodes;

namespace Community.Zabbix;

public partial class ZabbixTemplateGroupClient
{
    /// <remarks>
    /// Not atomic against a concurrent membership edit — the same shape as <see cref="ZabbixHostGroupClient"/>'s own
    /// mass update: two <c>template.get</c> reads (<see cref="ZabbixEmulation.ReplaceMembershipAsync"/>) then one batched
    /// <c>template.update</c>, where the removed <c>templategroup.massupdate</c> was one server call. A template moved in
    /// or out of the group between the reads and the write is not seen, and the groupless refusal is checked against the
    /// read, so it does not catch a removal that happens after it.
    /// </remarks>
    private partial async Task<IReadOnlyList<ulong>> MassUpdateEmulatedAsync(ZabbixTemplateGroupMassUpdateRequest request, Version server, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> groups = [.. request.Groups.Select(g => g.GroupId.ToString(CultureInfo.InvariantCulture))];
        IReadOnlyList<string> templates = [.. request.Templates.Select(t => t.TemplateId.ToString(CultureInfo.InvariantCulture))];
        JsonArray updates = await ZabbixEmulation.ReplaceMembershipAsync(Transport, "template", "templateid", "templategroups", groups, templates, cancellationToken).ConfigureAwait(false);
        if (updates.Count > 0) await ZabbixEmulation.SendAsync(Transport, "template.update", updates, cancellationToken).ConfigureAwait(false);
        return [.. request.Groups.Select(g => g.GroupId)];
    }
}
