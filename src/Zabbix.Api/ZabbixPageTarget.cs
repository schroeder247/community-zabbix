// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents an object the web frontend has a page for: its kind, its id, and what the frontend's routes ask besides
/// (the context of an item, the event source of an action, the trigger of an event). Every API record with a page gives
/// one (<see cref="IZabbixFrontendPage.ToPageTarget"/>), filled with what the record carries; a bare id is
/// <c>new ZabbixPageTarget(ZabbixPageKind.Host, 10084UL)</c>.
/// </summary>
/// <remarks>
/// A record read without its id gives a target whose <see cref="Id"/> is <c>0</c>, which <c>ZabbixFrontend.UriFor</c>
/// refuses. What a route needs and the target lacks (an item read without <c>selectHosts</c> doesn't say whether it's on
/// a template) is refused there too, with the field to read.
/// </remarks>
public sealed record ZabbixPageTarget : IZabbixFrontendPage
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

    /// <summary>The target of a record that may have been read without its id, which <c>0</c> stands for.</summary>
    /// <param name="kind">What the object is.</param>
    /// <param name="id">The object's id, when the record carries it.</param>
    /// <param name="name">The object's name, when the record carries it.</param>
    internal ZabbixPageTarget(ZabbixPageKind kind, ulong? id, string? name)
    {
        Kind = kind;
        Id = id ?? 0UL;
        Name = name;
    }

    /// <summary>What the object is.</summary>
    public ZabbixPageKind Kind { get; }

    /// <summary>The object's id; <c>0</c> when it comes from a record read without it.</summary>
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

    /// <summary>The target itself.</summary>
    /// <returns>This target.</returns>
    ZabbixPageTarget IZabbixFrontendPage.ToPageTarget() => this;

    /// <summary>The host or template the objects are on: the first, the one the frontend's own forms are opened from.</summary>
    /// <param name="hosts">What <c>selectHosts</c> returned.</param>
    /// <returns>The first host or template; <see langword="null"/> when none was read.</returns>
    internal static ZabbixHost? OnlyHost(IReadOnlyList<ZabbixHost>? hosts) => hosts is { Count: > 0 } ? hosts[0] : null;

    /// <summary>
    /// Whether the objects are on a host or a template. A template is a hosts row with status 3 (<c>HOST_STATUS_TEMPLATE</c>);
    /// <c>selectHosts</c> returns templates too (<c>templated_hosts</c> in <c>CItemGeneral</c>, <c>CTriggerGeneral</c>,
    /// <c>CGraphGeneral</c> and <c>CHttpTest</c>), so the status says which.
    /// </summary>
    /// <param name="hosts">What <c>selectHosts</c> returned.</param>
    /// <returns>The context; <see langword="null"/> when no host was read.</returns>
    internal static ZabbixPageContext? ContextOf(IReadOnlyList<ZabbixHost>? hosts)
        => OnlyHost(hosts)?.Status switch
        {
            ZabbixHostStatus.Template => ZabbixPageContext.Template,
            null => null,
            _ => ZabbixPageContext.Host,
        };

    /// <summary>
    /// The trigger whose event page is a problem's or an event's: <c>tr_events.php</c> is the page of a trigger's event,
    /// and an internal, discovery, autoregistration or service event has none.
    /// </summary>
    /// <param name="source">The event's source.</param>
    /// <param name="objectType">What the event is about.</param>
    /// <param name="objectId">The object the event is about.</param>
    /// <returns>The trigger's id; <see langword="null"/> when the event isn't a trigger's.</returns>
    internal static ulong? TriggerOf(ZabbixEventSource? source, ZabbixEventObject? objectType, ulong? objectId)
        => source == ZabbixEventSource.Triggers && objectType == ZabbixEventObject.Trigger ? objectId : null;
}
