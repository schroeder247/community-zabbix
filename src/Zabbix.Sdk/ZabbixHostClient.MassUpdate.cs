// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json.Nodes;

namespace Community.Zabbix;

// Zabbix 8.0 removed host/template/hostgroup/templategroup.massupdate and hostinterface.replacehostinterfaces. The 8.0
// frontend makes the same changes with the methods it kept (CControllerPopupMassupdateHost.php:477,
// CControllerTemplateMassupdate.php:353), and so do these: one update call each, never an add-then-remove pair, so a
// change is as atomic as the removed method was. The generated member routes here only
// when the server's version lacks the method; the request is the removed method's own record, rewritten at the JSON
// level so every field it carries reaches the replacement unchanged.
//
// "As atomic as the removed method was" holds for host.update/template.update themselves (one call, one server-side
// transaction per target list) but not for the two compositions that must first read what they are about to change —
// the macro-id attachment (host.get/template.get before the update) and the hostgroup/templategroup membership merge
// (ReplaceMembershipAsync, two get calls before the update): a read-then-write is never atomic against a write that
// lands on the server in between, where the removed single-call method was. Each such composition says so on its own
// member below.

public partial class ZabbixHostClient
{
    /// <remarks>
    /// Not atomic against a concurrent macro edit when macros are named. With no <c>Macros</c> in
    /// <paramref name="request"/> this is exactly one <c>host.update</c>, as atomic as the removed method. Naming macros
    /// adds one <c>host.get</c> first, to attach the existing <c>hostmacroid</c> of each macro matched by name (host.update
    /// treats an id-less macro as new, where massupdate matched by name); a macro added, removed or renamed on the server
    /// between that read and the write is not seen, and the write can create a duplicate or miss the rename.
    /// </remarks>
    private partial async Task<IReadOnlyList<ulong>> MassUpdateEmulatedAsync(ZabbixHostMassUpdateRequest request, Version server, CancellationToken cancellationToken)
    {
        JsonObject fields = ZabbixEmulation.Fields(ZabbixEmulation.Object(request, ZabbixApiJson.Default.ZabbixHostMassUpdateRequest), "hosts");
        List<string> ids = [.. request.Hosts.Select(h => h.HostId.ToString(CultureInfo.InvariantCulture))];
        // host.update treats a macro without an id as new, where massupdate matched the existing one by name: attach the ids.
        Dictionary<string, Dictionary<string, string>> macros = fields["macros"] is null
            ? []
            : await ZabbixEmulation.MacroIdsAsync(Transport, "host.get", "hostids", "hostid", ids, cancellationToken).ConfigureAwait(false);
        JsonArray updates = [.. ids.Select(id => ZabbixEmulation.Update(fields, "hostid", id, macros.GetValueOrDefault(id)))];
        return ZabbixIds.Read(await ZabbixEmulation.SendAsync(Transport, "host.update", updates, cancellationToken).ConfigureAwait(false), "hostids");
    }
}
