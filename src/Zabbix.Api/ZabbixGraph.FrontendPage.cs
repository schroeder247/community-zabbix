// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

public sealed partial record ZabbixGraph : IZabbixFrontendPage
{
    /// <inheritdoc/>
    ZabbixPageTarget IZabbixFrontendPage.ToPageTarget()
        => new(ZabbixPageKind.Graph, GraphId, Name)
        {
            HostId = ZabbixPageTarget.OnlyHost(Hosts)?.HostId ?? (Templates is { Count: > 0 } templates ? templates[0].TemplateId : null),
            Context = Templates is { Count: > 0 } ? ZabbixPageContext.Template : ZabbixPageTarget.ContextOf(Hosts),
        };
}
