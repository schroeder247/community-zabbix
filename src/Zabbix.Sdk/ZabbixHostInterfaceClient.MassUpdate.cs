// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Community.Zabbix;

public partial class ZabbixHostInterfaceClient
{
    private partial async Task<IReadOnlyList<ulong>> ReplaceHostInterfacesEmulatedAsync(ZabbixHostInterfaceReplaceHostInterfacesRequest request, Version server, CancellationToken cancellationToken)
    {
        string host = (request.HostId ?? throw new ArgumentException("A replacehostinterfaces request must name its hostid.", nameof(request))).ToString(CultureInfo.InvariantCulture);
        JsonObject body = ZabbixEmulation.Fields(ZabbixEmulation.Object(request, ZabbixApiJson.Default.ZabbixHostInterfaceReplaceHostInterfacesRequest));
        JsonArray update = [new JsonObject { ["hostid"] = host, ["interfaces"] = body["interfaces"]?.DeepClone() ?? new JsonArray() }];
        await ZabbixEmulation.SendAsync(Transport, "host.update", update, cancellationToken).ConfigureAwait(false);
        // replacehostinterfaces answered with the interfaces' ids; host.update answers with the host's, so read them back.
        JsonElement found = await ZabbixEmulation.SendAsync(Transport, "hostinterface.get", new JsonObject { ["hostids"] = new JsonArray(host), ["output"] = new JsonArray("interfaceid") }, cancellationToken).ConfigureAwait(false);
        return [.. ZabbixEmulation.Rows(found, "hostinterface.get").Select(row => ZabbixEmulation.Id(row, "interfaceid", "hostinterface.get"))];
    }
}
