// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>Specifies which of an object's frontend pages <see cref="ZabbixFrontend.UriFor"/> addresses.</summary>
public enum ZabbixPageView
{
    /// <summary>
    /// The page the frontend opens the object on: the monitoring page of a dashboard, map, service, problem or event, the
    /// configuration form of every other object (<see cref="Edit"/>).
    /// </summary>
    Default = 0,

    /// <summary>The configuration form: the map editor for a map, the service in edit mode for a service.</summary>
    Edit,

    /// <summary><c>Monitoring → Latest data</c> for a host, a host group, or a host's item.</summary>
    Latest,

    /// <summary><c>Monitoring → Problems</c> for a host, a host group, or a trigger.</summary>
    Problems,

    /// <summary>An item's history as a graph (<c>history.php?action=showgraph</c>; numeric items).</summary>
    Graph,

    /// <summary>An item's history as a list of values (<c>history.php?action=showvalues</c>).</summary>
    Values,
}
