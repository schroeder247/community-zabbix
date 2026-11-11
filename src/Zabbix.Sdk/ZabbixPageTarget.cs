// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;

namespace Community.Zabbix;

/// <summary>
/// Represents an object the web frontend has a page for: its kind, its id, and what the frontend's routes ask besides
/// (the context of an item, the event source of an action, the trigger of an event). The one currency
/// <see cref="ZabbixFrontend.UriFor"/> takes: every API record with a page converts to it implicitly, filling what the
/// record carries, so <c>frontend.UriFor(host)</c> and <c>frontend.UriFor(item)</c> are the same call; a bare id is
/// <c>new ZabbixPageTarget(ZabbixPageKind.Host, 10084UL)</c>.
/// </summary>
/// <remarks>
/// A conversion never fails: a record read without its id converts to a target whose <see cref="Id"/> is
/// <c>0</c>, which
/// <see cref="ZabbixFrontend.UriFor"/> refuses. What a route needs and the target lacks (an item read without
/// <c>selectHosts</c> does not say whether it is on a template) is refused there too, with the field to read.
/// </remarks>
public sealed record ZabbixPageTarget
{
    /// <summary>The object of <paramref name="kind"/> whose id is <paramref name="id"/>.</summary>
    /// <param name="kind">What the object is.</param>
    /// <param name="id">The object's id; not <c>0</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is <c>0</c>.</exception>
    public ZabbixPageTarget(ZabbixPageKind kind, ulong id)
    {
        if (id == 0) throw new ArgumentException("An object's page needs its id; 0 names no object.", nameof(id));
        Kind = kind;
        Id = id;
    }

    private ZabbixPageTarget(ZabbixPageKind kind, ulong? id, string? name)
    {
        Kind = kind;
        Id = id ?? 0UL;
        Name = name;
    }

    /// <summary>What the object is.</summary>
    public ZabbixPageKind Kind { get; }

    /// <summary>The object's id; <c>0</c> when it was converted from a record read without it.</summary>
    public ulong Id { get; }

    /// <summary>The object's name: Zabbix 7.0 opens some forms only from their list, which the link filters by it.</summary>
    public string? Name { get; init; }

    /// <summary>Whether an item, trigger, graph, discovery rule or web scenario is on a host or a template; <see langword="null"/> when not known.</summary>
    public ZabbixPageContext? Context { get; init; }

    /// <summary>The host or template an item, trigger, graph, discovery rule or web scenario is on.</summary>
    public ulong? HostId { get; init; }

    /// <summary>An action's event source: its form and list are per source.</summary>
    public ZabbixEventSource? EventSource { get; init; }

    /// <summary>The trigger a problem or event is about: the event page is the trigger's.</summary>
    public ulong? TriggerId { get; init; }

    /// <summary>The host's page.</summary>
    /// <param name="host">The host.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixHost host) => FromZabbixHost(host);

    /// <summary>The host group's page.</summary>
    /// <param name="group">The host group.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixHostGroup group) => FromZabbixHostGroup(group);

    /// <summary>The template's page.</summary>
    /// <param name="template">The template.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixTemplate template) => FromZabbixTemplate(template);

    /// <summary>The template group's page.</summary>
    /// <param name="group">The template group.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixTemplateGroup group) => FromZabbixTemplateGroup(group);

    /// <summary>The item's page.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixItem item) => FromZabbixItem(item);

    /// <summary>The trigger's page.</summary>
    /// <param name="trigger">The trigger.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixTrigger trigger) => FromZabbixTrigger(trigger);

    /// <summary>The graph's page.</summary>
    /// <param name="graph">The graph.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixGraph graph) => FromZabbixGraph(graph);

    /// <summary>The discovery rule's page.</summary>
    /// <param name="rule">The low-level discovery rule.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixDiscoveryRule rule) => FromZabbixDiscoveryRule(rule);

    /// <summary>The web scenario's page.</summary>
    /// <param name="scenario">The web scenario.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixHttpTest scenario) => FromZabbixHttpTest(scenario);

    /// <summary>The dashboard's page.</summary>
    /// <param name="dashboard">The dashboard.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixDashboard dashboard) => FromZabbixDashboard(dashboard);

    /// <summary>The map's page.</summary>
    /// <param name="map">The map.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixMap map) => FromZabbixMap(map);

    /// <summary>The problem's page.</summary>
    /// <param name="problem">The problem.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixProblem problem) => FromZabbixProblem(problem);

    /// <summary>The event's page.</summary>
    /// <param name="event">The event.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixEvent @event) => FromZabbixEvent(@event);

    /// <summary>The maintenance's page.</summary>
    /// <param name="maintenance">The maintenance.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixMaintenanceRecord maintenance) => FromZabbixMaintenanceRecord(maintenance);

    /// <summary>The user's page.</summary>
    /// <param name="user">The user.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixUser user) => FromZabbixUser(user);

    /// <summary>The user group's page.</summary>
    /// <param name="group">The user group.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixUserGroup group) => FromZabbixUserGroup(group);

    /// <summary>The proxy's page.</summary>
    /// <param name="proxy">The proxy.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixProxy proxy) => FromZabbixProxy(proxy);

    /// <summary>The network discovery rule's page.</summary>
    /// <param name="rule">The network discovery rule.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixNetworkDiscoveryRule rule) => FromZabbixNetworkDiscoveryRule(rule);

    /// <summary>The action's page.</summary>
    /// <param name="action">The action.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixAction action) => FromZabbixAction(action);

    /// <summary>The media type's page.</summary>
    /// <param name="mediaType">The media type.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixMediaType mediaType) => FromZabbixMediaType(mediaType);

    /// <summary>The script's page.</summary>
    /// <param name="script">The script.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixScript script) => FromZabbixScript(script);

    /// <summary>The service's page.</summary>
    /// <param name="service">The service.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixServiceRecord service) => FromZabbixServiceRecord(service);

    /// <summary>The SLA's page.</summary>
    /// <param name="sla">The SLA.</param>
    /// <returns>The target of the object's page.</returns>
    public static implicit operator ZabbixPageTarget(ZabbixSlaRecord sla) => FromZabbixSlaRecord(sla);

    /// <summary>The host's page (the named form of the implicit conversion).</summary>
    /// <param name="host">The host.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(host))]
    public static ZabbixPageTarget? FromZabbixHost(ZabbixHost? host)
        => host is null ? null : new(ZabbixPageKind.Host, host.HostId, host.VisibleName ?? host.HostName);

    /// <summary>The host group's page (the named form of the implicit conversion).</summary>
    /// <param name="group">The host group.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(group))]
    public static ZabbixPageTarget? FromZabbixHostGroup(ZabbixHostGroup? group)
        => group is null ? null : new(ZabbixPageKind.HostGroup, group.GroupId, group.Name);

    /// <summary>The template's page (the named form of the implicit conversion).</summary>
    /// <param name="template">The template.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(template))]
    public static ZabbixPageTarget? FromZabbixTemplate(ZabbixTemplate? template)
        => template is null ? null : new(ZabbixPageKind.Template, template.TemplateId, template.VisibleName ?? template.HostName);

    /// <summary>The template group's page (the named form of the implicit conversion).</summary>
    /// <param name="group">The template group.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(group))]
    public static ZabbixPageTarget? FromZabbixTemplateGroup(ZabbixTemplateGroup? group)
        => group is null ? null : new(ZabbixPageKind.TemplateGroup, group.GroupId, group.Name);

    /// <summary>The item's page (the named form of the implicit conversion); its context is known when it was read with <c>selectHosts</c>.</summary>
    /// <param name="item">The item.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(item))]
    public static ZabbixPageTarget? FromZabbixItem(ZabbixItem? item)
        => item is null ? null : new(ZabbixPageKind.Item, item.ItemId, item.Name)
        {
            HostId = item.HostId ?? OnlyHost(item.Hosts)?.HostId,
            Context = ContextOf(item.Hosts),
        };

    /// <summary>The trigger's page (the named form of the implicit conversion); its host and context are known when it was read with <c>selectHosts</c>.</summary>
    /// <param name="trigger">The trigger.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(trigger))]
    public static ZabbixPageTarget? FromZabbixTrigger(ZabbixTrigger? trigger)
        => trigger is null ? null : new(ZabbixPageKind.Trigger, trigger.TriggerId, trigger.Name)
        {
            HostId = OnlyHost(trigger.Hosts)?.HostId,
            Context = ContextOf(trigger.Hosts),
        };

    /// <summary>The graph's page (the named form of the implicit conversion); its context is known when it was read with <c>selectHosts</c> or <c>selectTemplates</c>.</summary>
    /// <param name="graph">The graph.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(graph))]
    public static ZabbixPageTarget? FromZabbixGraph(ZabbixGraph? graph)
        => graph is null ? null : new(ZabbixPageKind.Graph, graph.GraphId, graph.Name)
        {
            HostId = OnlyHost(graph.Hosts)?.HostId ?? (graph.Templates is { Count: > 0 } templates ? templates[0].TemplateId : null),
            Context = graph.Templates is { Count: > 0 } ? ZabbixPageContext.Template : ContextOf(graph.Hosts),
        };

    /// <summary>The discovery rule's page (the named form of the implicit conversion); its context is known when it was read with <c>selectHosts</c>.</summary>
    /// <param name="rule">The low-level discovery rule.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(rule))]
    public static ZabbixPageTarget? FromZabbixDiscoveryRule(ZabbixDiscoveryRule? rule)
        => rule is null ? null : new(ZabbixPageKind.DiscoveryRule, rule.ItemId, rule.Name)
        {
            HostId = rule.HostId ?? OnlyHost(rule.Hosts)?.HostId,
            Context = ContextOf(rule.Hosts),
        };

    /// <summary>The web scenario's page (the named form of the implicit conversion); its context is known when it was read with <c>selectHosts</c>.</summary>
    /// <param name="scenario">The web scenario.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(scenario))]
    public static ZabbixPageTarget? FromZabbixHttpTest(ZabbixHttpTest? scenario)
        => scenario is null ? null : new(ZabbixPageKind.WebScenario, scenario.HttpTestId, scenario.Name)
        {
            HostId = scenario.HostId ?? OnlyHost(scenario.Hosts)?.HostId,
            Context = ContextOf(scenario.Hosts),
        };

    /// <summary>The dashboard's page (the named form of the implicit conversion).</summary>
    /// <param name="dashboard">The dashboard.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(dashboard))]
    public static ZabbixPageTarget? FromZabbixDashboard(ZabbixDashboard? dashboard)
        => dashboard is null ? null : new(ZabbixPageKind.Dashboard, dashboard.DashboardId, dashboard.Name);

    /// <summary>The map's page (the named form of the implicit conversion).</summary>
    /// <param name="map">The map.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(map))]
    public static ZabbixPageTarget? FromZabbixMap(ZabbixMap? map)
        => map is null ? null : new(ZabbixPageKind.Map, map.MapId, map.Name);

    /// <summary>The problem's page (the named form of the implicit conversion): a trigger's problem has one.</summary>
    /// <param name="problem">The problem.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(problem))]
    public static ZabbixPageTarget? FromZabbixProblem(ZabbixProblem? problem)
        => problem is null ? null : new(ZabbixPageKind.Problem, problem.EventId, problem.Name)
        {
            EventSource = problem.Source,
            TriggerId = TriggerOf(problem.Source, problem.ObjectType, problem.ObjectId),
        };

    /// <summary>The event's page (the named form of the implicit conversion): a trigger's event has one.</summary>
    /// <param name="event">The event.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(@event))]
    public static ZabbixPageTarget? FromZabbixEvent(ZabbixEvent? @event)
        => @event is null ? null : new(ZabbixPageKind.Event, @event.EventId, @event.Name)
        {
            EventSource = @event.Source,
            TriggerId = TriggerOf(@event.Source, @event.ObjectType, @event.ObjectId),
        };

    /// <summary>The maintenance's page (the named form of the implicit conversion).</summary>
    /// <param name="maintenance">The maintenance.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(maintenance))]
    public static ZabbixPageTarget? FromZabbixMaintenanceRecord(ZabbixMaintenanceRecord? maintenance)
        => maintenance is null ? null : new(ZabbixPageKind.Maintenance, maintenance.MaintenanceId, maintenance.Name);

    /// <summary>The user's page (the named form of the implicit conversion).</summary>
    /// <param name="user">The user.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(user))]
    public static ZabbixPageTarget? FromZabbixUser(ZabbixUser? user)
        => user is null ? null : new(ZabbixPageKind.User, user.UserId, user.UserName);

    /// <summary>The user group's page (the named form of the implicit conversion).</summary>
    /// <param name="group">The user group.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(group))]
    public static ZabbixPageTarget? FromZabbixUserGroup(ZabbixUserGroup? group)
        => group is null ? null : new(ZabbixPageKind.UserGroup, group.UserGroupId, group.Name);

    /// <summary>The proxy's page (the named form of the implicit conversion).</summary>
    /// <param name="proxy">The proxy.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(proxy))]
    public static ZabbixPageTarget? FromZabbixProxy(ZabbixProxy? proxy)
        => proxy is null ? null : new(ZabbixPageKind.Proxy, proxy.ProxyId, proxy.Name);

    /// <summary>The network discovery rule's page (the named form of the implicit conversion).</summary>
    /// <param name="rule">The network discovery rule.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(rule))]
    public static ZabbixPageTarget? FromZabbixNetworkDiscoveryRule(ZabbixNetworkDiscoveryRule? rule)
        => rule is null ? null : new(ZabbixPageKind.NetworkDiscoveryRule, rule.NetworkDiscoveryRuleId, rule.Name);

    /// <summary>The action's page (the named form of the implicit conversion).</summary>
    /// <param name="action">The action.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(action))]
    public static ZabbixPageTarget? FromZabbixAction(ZabbixAction? action)
        => action is null ? null : new(ZabbixPageKind.Action, action.ActionId, action.Name) { EventSource = action.EventSource };

    /// <summary>The media type's page (the named form of the implicit conversion).</summary>
    /// <param name="mediaType">The media type.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(mediaType))]
    public static ZabbixPageTarget? FromZabbixMediaType(ZabbixMediaType? mediaType)
        => mediaType is null ? null : new(ZabbixPageKind.MediaType, mediaType.MediaTypeId, mediaType.Name);

    /// <summary>The script's page (the named form of the implicit conversion).</summary>
    /// <param name="script">The script.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(script))]
    public static ZabbixPageTarget? FromZabbixScript(ZabbixScript? script)
        => script is null ? null : new(ZabbixPageKind.Script, script.ScriptId, script.Name);

    /// <summary>The service's page (the named form of the implicit conversion).</summary>
    /// <param name="service">The service.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(service))]
    public static ZabbixPageTarget? FromZabbixServiceRecord(ZabbixServiceRecord? service)
        => service is null ? null : new(ZabbixPageKind.Service, service.ServiceId, service.Name);

    /// <summary>The SLA's page (the named form of the implicit conversion).</summary>
    /// <param name="sla">The SLA.</param>
    /// <returns>The target of the object's page; <see langword="null"/> when the object is <see langword="null"/>.</returns>
    [return: NotNullIfNotNull(nameof(sla))]
    public static ZabbixPageTarget? FromZabbixSlaRecord(ZabbixSlaRecord? sla)
        => sla is null ? null : new(ZabbixPageKind.Sla, sla.SlaId, sla.Name);

    /// <summary>
    /// Reads any API record that has a page as a target, by its runtime type: what a caller holding an
    /// <see cref="object"/> (a shell's pipeline) uses where code holding a typed record converts implicitly.
    /// </summary>
    /// <param name="value">The record, or a target as is.</param>
    /// <param name="target">The target.</param>
    /// <returns>Whether <paramref name="value"/> is a record with a page.</returns>
    public static bool TryFrom(object? value, [NotNullWhen(true)] out ZabbixPageTarget? target)
    {
        target = value switch
        {
            ZabbixPageTarget given => given,
            ZabbixHost host => FromZabbixHost(host),
            ZabbixHostGroup group => FromZabbixHostGroup(group),
            ZabbixTemplate template => FromZabbixTemplate(template),
            ZabbixTemplateGroup group => FromZabbixTemplateGroup(group),
            ZabbixItem item => FromZabbixItem(item),
            ZabbixTrigger trigger => FromZabbixTrigger(trigger),
            ZabbixGraph graph => FromZabbixGraph(graph),
            ZabbixDiscoveryRule rule => FromZabbixDiscoveryRule(rule),
            ZabbixHttpTest scenario => FromZabbixHttpTest(scenario),
            ZabbixDashboard dashboard => FromZabbixDashboard(dashboard),
            ZabbixMap map => FromZabbixMap(map),
            ZabbixProblem problem => FromZabbixProblem(problem),
            ZabbixEvent @event => FromZabbixEvent(@event),
            ZabbixMaintenanceRecord maintenance => FromZabbixMaintenanceRecord(maintenance),
            ZabbixUser user => FromZabbixUser(user),
            ZabbixUserGroup group => FromZabbixUserGroup(group),
            ZabbixProxy proxy => FromZabbixProxy(proxy),
            ZabbixNetworkDiscoveryRule rule => FromZabbixNetworkDiscoveryRule(rule),
            ZabbixAction action => FromZabbixAction(action),
            ZabbixMediaType mediaType => FromZabbixMediaType(mediaType),
            ZabbixScript script => FromZabbixScript(script),
            ZabbixServiceRecord service => FromZabbixServiceRecord(service),
            ZabbixSlaRecord sla => FromZabbixSlaRecord(sla),
            _ => null,
        };
        return target is not null;
    }

    // The objects are on one host or template; the first is the one the frontend's own forms are opened from.
    private static ZabbixHost? OnlyHost(IReadOnlyList<ZabbixHost>? hosts) => hosts is { Count: > 0 } ? hosts[0] : null;

    // A template is a hosts row with status 3 (HOST_STATUS_TEMPLATE); selectHosts returns templates too
    // (templated_hosts in CItemGeneral, CTriggerGeneral, CGraphGeneral and CHttpTest), so the status says which.
    private static ZabbixPageContext? ContextOf(IReadOnlyList<ZabbixHost>? hosts)
        => OnlyHost(hosts)?.Status switch
        {
            ZabbixHostStatus.Template => ZabbixPageContext.Template,
            null => null,
            _ => ZabbixPageContext.Host,
        };

    // tr_events.php is the page of a trigger's event; an internal, discovery, autoregistration or service event has none.
    private static ulong? TriggerOf(ZabbixEventSource? source, ZabbixEventObject? objectType, ulong? objectId)
        => source == ZabbixEventSource.Triggers && objectType == ZabbixEventObject.Trigger ? objectId : null;
}
