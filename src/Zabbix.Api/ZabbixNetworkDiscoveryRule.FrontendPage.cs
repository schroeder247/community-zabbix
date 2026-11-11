// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

public sealed partial record ZabbixNetworkDiscoveryRule : IZabbixFrontendPage
{
    /// <inheritdoc/>
    ZabbixPageTarget IZabbixFrontendPage.ToPageTarget()
        => new(ZabbixPageKind.NetworkDiscoveryRule, NetworkDiscoveryRuleId, Name);
}
