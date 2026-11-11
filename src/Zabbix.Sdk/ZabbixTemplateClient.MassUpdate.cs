// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json.Nodes;

namespace Community.Zabbix;

public partial class ZabbixTemplateClient
{
    /// <remarks>
    /// Not atomic against a concurrent macro edit when macros are named — the same shape as
    /// <see cref="ZabbixHostClient"/>'s own mass update: no <c>Macros</c>, one <c>template.update</c>; named macros add a
    /// <c>template.get</c> read first, to attach existing <c>hostmacroid</c>s by name, and a macro changed on the server
    /// between that read and the write is not seen.
    /// </remarks>
    private partial async Task<IReadOnlyList<ulong>> MassUpdateEmulatedAsync(ZabbixTemplateMassUpdateRequest request, Version server, CancellationToken cancellationToken)
    {
        JsonObject fields = ZabbixEmulation.Fields(ZabbixEmulation.Object(request, ZabbixApiJson.Default.ZabbixTemplateMassUpdateRequest), "templates");
        // template.update names the linked templates "templates" where massupdate said "templates_link".
        if (fields.Remove("templates_link", out JsonNode? linked)) fields["templates"] = linked;
        List<string> ids = [.. request.Templates.Select(t => t.TemplateId.ToString(CultureInfo.InvariantCulture))];
        Dictionary<string, Dictionary<string, string>> macros = fields["macros"] is null
            ? []
            : await ZabbixEmulation.MacroIdsAsync(Transport, "template.get", "templateids", "templateid", ids, cancellationToken).ConfigureAwait(false);
        JsonArray updates = [.. ids.Select(id => ZabbixEmulation.Update(fields, "templateid", id, macros.GetValueOrDefault(id)))];
        return ZabbixIds.Read(await ZabbixEmulation.SendAsync(Transport, "template.update", updates, cancellationToken).ConfigureAwait(false), "templateids");
    }
}
