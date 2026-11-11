// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>Specifies which kind of object a <see cref="ZabbixPageTarget"/> names: one per API object the frontend has a page for.</summary>
public enum ZabbixPageKind
{
    /// <summary>A host (<c>host.get</c>, <see cref="ZabbixHost"/>).</summary>
    Host,

    /// <summary>A host group (<c>hostgroup.get</c>, <see cref="ZabbixHostGroup"/>).</summary>
    HostGroup,

    /// <summary>A template (<c>template.get</c>, <see cref="ZabbixTemplate"/>).</summary>
    Template,

    /// <summary>A template group (<c>templategroup.get</c>, <see cref="ZabbixTemplateGroup"/>).</summary>
    TemplateGroup,

    /// <summary>An item on a host or a template (<c>item.get</c>, <see cref="ZabbixItem"/>).</summary>
    Item,

    /// <summary>A trigger on a host or a template (<c>trigger.get</c>, <see cref="ZabbixTrigger"/>).</summary>
    Trigger,

    /// <summary>A graph on a host or a template (<c>graph.get</c>, <see cref="ZabbixGraph"/>).</summary>
    Graph,

    /// <summary>A low-level discovery rule on a host or a template (<c>discoveryrule.get</c>, <see cref="ZabbixDiscoveryRule"/>).</summary>
    DiscoveryRule,

    /// <summary>A web scenario on a host or a template (<c>httptest.get</c>, <see cref="ZabbixHttpTest"/>).</summary>
    WebScenario,

    /// <summary>A dashboard (<c>dashboard.get</c>, <see cref="ZabbixDashboard"/>).</summary>
    Dashboard,

    /// <summary>A network map (<c>map.get</c>, <see cref="ZabbixMap"/>).</summary>
    Map,

    /// <summary>A problem (<c>problem.get</c>, <see cref="ZabbixProblem"/>).</summary>
    Problem,

    /// <summary>An event (<c>event.get</c>, <see cref="ZabbixEvent"/>).</summary>
    Event,

    /// <summary>A maintenance period (<c>maintenance.get</c>, <see cref="ZabbixMaintenanceRecord"/>).</summary>
    Maintenance,

    /// <summary>A user (<c>user.get</c>, <see cref="ZabbixUser"/>).</summary>
    User,

    /// <summary>A user group (<c>usergroup.get</c>, <see cref="ZabbixUserGroup"/>).</summary>
    UserGroup,

    /// <summary>A proxy (<c>proxy.get</c>, <see cref="ZabbixProxy"/>).</summary>
    Proxy,

    /// <summary>A network discovery rule (<c>drule.get</c>, <see cref="ZabbixNetworkDiscoveryRule"/>).</summary>
    NetworkDiscoveryRule,

    /// <summary>An action (<c>action.get</c>, <see cref="ZabbixAction"/>).</summary>
    Action,

    /// <summary>A media type (<c>mediatype.get</c>, <see cref="ZabbixMediaType"/>).</summary>
    MediaType,

    /// <summary>A global script (<c>script.get</c>, <see cref="ZabbixScript"/>).</summary>
    Script,

    /// <summary>A service (<c>service.get</c>, <see cref="ZabbixServiceRecord"/>).</summary>
    Service,

    /// <summary>An SLA (<c>sla.get</c>, <see cref="ZabbixSlaRecord"/>).</summary>
    Sla,
}
