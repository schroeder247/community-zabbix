// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Frozen;
using System.Globalization;

namespace Community.Zabbix;

/// <summary>
/// Represents a Zabbix web frontend of one version: the page links of its objects. <see cref="UriFor"/> maps an object
/// (any API record with a page, or a <see cref="ZabbixPageTarget"/> for a bare id) to the address the frontend itself
/// links it by, by the frontend's own routes for that version. Get one from a connected server with
/// <see cref="ZabbixClient.GetFrontendAsync"/>, or build one for a known base and version. Immutable and thread-safe.
/// </summary>
/// <remarks>
/// <para>
/// The routes are read from the frontend sources of each version (<c>CRouter.php</c>, <c>CControllerPopup.php</c>, the
/// controllers' <c>checkInput</c>, the legacy pages' <c>check_fields</c>): 7.0 serves most configuration forms only as
/// dialogs it opens from their list, so a 7.0 link to such a form is its list filtered to the object; 7.4 and later
/// address those dialogs directly (<c>zabbix.php?action=popup&amp;popup=item.edit…</c>).
/// </para>
/// <para>
/// A link needs a signed-in browser: opened without a session, the frontend shows its login page or a warning.
/// </para>
/// </remarks>
public sealed class ZabbixFrontend
{
    private static readonly Version s_v70 = new(7, 0);
    private static readonly Version s_v74 = new(7, 4);
    private static readonly Version s_v80 = new(8, 0);

    /// <summary>Each (kind, view) the frontend has a page for, and per version the page: the newest entry whose version is not above the server's applies.</summary>
    private static readonly FrozenDictionary<(ZabbixPageKind Kind, ZabbixPageView View), Route[]> s_routes = BuildRoutes();

    /// <summary>A frontend at <paramref name="baseUri"/> that runs Zabbix <paramref name="version"/>.</summary>
    /// <param name="baseUri">The folder that holds <c>zabbix.php</c>, e.g. <c>https://zabbix.example/</c>; absolute, <c>http</c> or <c>https</c>.</param>
    /// <param name="version">The frontend's version (<c>apiinfo.version</c>); 7.0 or later.</param>
    /// <exception cref="ArgumentException"><paramref name="baseUri"/> is not an absolute http or https URI.</exception>
    /// <exception cref="ZabbixVersionException"><paramref name="version"/> is below 7.0 (a <see cref="NotSupportedException"/>, as every version refusal is).</exception>
    public ZabbixFrontend(Uri baseUri, Version version)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(version);
        if (!baseUri.IsAbsoluteUri || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
            throw new ArgumentException($"The frontend's base must be an absolute http or https URI, not '{baseUri}'.", nameof(baseUri));
        if (version < s_v70)
            throw new ZabbixVersionException($"Zabbix {version} is older than 7.0, the oldest frontend whose routes are known.", version);
        BaseUri = AsFolder(ZabbixHttp.WithoutUserInfo(baseUri));
        Version = new Version(version.Major, version.Minor);
    }

    /// <summary>The folder that holds <c>zabbix.php</c>, ending in <c>/</c>.</summary>
    public Uri BaseUri { get; }

    /// <summary>The frontend's version, major and minor: the one its routes are chosen by.</summary>
    public Version Version { get; }

    /// <summary>
    /// The address of <paramref name="target"/>'s page: <paramref name="view"/> of it, by this version's route.
    /// </summary>
    /// <param name="target">The object: any API record with a page converts implicitly (<c>UriFor(host)</c>).</param>
    /// <param name="view">Which page; <see cref="ZabbixPageView.Default"/> is the one the frontend opens the object on.</param>
    /// <returns>The absolute page address.</returns>
    /// <exception cref="ArgumentException">
    /// The target's id is not a decimal id; the kind has no such view (a dashboard has no <see cref="ZabbixPageView.Edit"/>,
    /// a template's item no <see cref="ZabbixPageView.Latest"/>); or the route needs what the target does not carry (an
    /// item's context, an action's event source, the trigger of an event), which the message names.
    /// </exception>
    public Uri UriFor(ZabbixPageTarget target, ZabbixPageView view = ZabbixPageView.Default)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Id == 0)
            throw new ArgumentException($"The {Describe(target.Kind)} carries no id: read it with an output that includes the id.", nameof(target));
        if (!s_routes.TryGetValue((target.Kind, view), out Route[]? routes)
            && !(view == ZabbixPageView.Default && s_routes.TryGetValue((target.Kind, ZabbixPageView.Edit), out routes)))
        {
            throw new ArgumentException($"A {Describe(target.Kind)} has no {view} page; it has {string.Join(", ", ViewsOf(target.Kind))}.", nameof(view));
        }
        Route route = routes.Last(candidate => candidate.Since <= Version);
        string query = string.Join('&', route.Query(target).Select(pair => Uri.EscapeDataString(pair.Name) + "=" + Uri.EscapeDataString(pair.Value)));
        return new Uri(BaseUri, route.Page + "?" + query);
    }

    /// <summary>The views <paramref name="kind"/> has pages for, <see cref="ZabbixPageView.Default"/> first.</summary>
    /// <param name="kind">The kind of object.</param>
    /// <returns>The views the kind's page has in this frontend's version.</returns>
    public static IReadOnlyList<ZabbixPageView> ViewsOf(ZabbixPageKind kind)
        => [ZabbixPageView.Default, .. s_routes.Keys.Where(key => key.Kind == kind && key.View != ZabbixPageView.Default).Select(key => key.View).Order()];

    /// <summary>
    /// The frontend's base for a server reached at <paramref name="endpoint"/>: <paramref name="configured"/> when given,
    /// else the endpoint's folder (the frontend serves <c>api_jsonrpc.php</c> beside <c>zabbix.php</c>).
    /// </summary>
    internal static Uri BaseOf(Uri endpoint, Uri? configured)
        => configured is not null ? AsFolder(configured) : new Uri(new UriBuilder(endpoint) { Query = string.Empty, Fragment = string.Empty }.Uri, ".");

    private static Uri AsFolder(Uri uri)
        => uri.AbsolutePath.EndsWith('/') ? uri : new UriBuilder(uri) { Path = uri.AbsolutePath + "/" }.Uri;

    private static string Describe(ZabbixPageKind kind) => kind switch
    {
        ZabbixPageKind.HostGroup => "host group",
        ZabbixPageKind.TemplateGroup => "template group",
        ZabbixPageKind.DiscoveryRule => "discovery rule",
        ZabbixPageKind.WebScenario => "web scenario",
        ZabbixPageKind.UserGroup => "user group",
        ZabbixPageKind.NetworkDiscoveryRule => "network discovery rule",
        ZabbixPageKind.MediaType => "media type",
        ZabbixPageKind.Sla => "SLA",
        ZabbixPageKind.Host => "host",
        ZabbixPageKind.Template => "template",
        ZabbixPageKind.Item => "item",
        ZabbixPageKind.Trigger => "trigger",
        ZabbixPageKind.Graph => "graph",
        ZabbixPageKind.Dashboard => "dashboard",
        ZabbixPageKind.Map => "map",
        ZabbixPageKind.Problem => "problem",
        ZabbixPageKind.Event => "event",
        ZabbixPageKind.Maintenance => "maintenance",
        ZabbixPageKind.User => "user",
        ZabbixPageKind.Proxy => "proxy",
        ZabbixPageKind.Action => "action",
        ZabbixPageKind.Script => "script",
        ZabbixPageKind.Service => "service",
        _ => kind.ToString(),
    };

    /// <summary>One page of one (kind, view), from <see cref="Since"/> on: the script under the base and its arguments.</summary>
    private sealed record Route(Version Since, string Page, Func<ZabbixPageTarget, IEnumerable<(string Name, string Value)>> Query);

    // The table. Each line is verified against the pinned frontend sources of 7.0, 7.4 and 8.0 (tools/zabbix-sources.lock):
    // an action is a CRouter.php route whose layout is an HTML page, or a CControllerPopup.php supported popup (7.4 on;
    // 7.0's supports acknowledge.edit only, so its JSON-layout forms are opened from their list by script and have no
    // address); the arguments are the ones the controller's checkInput or the legacy page's check_fields reads, named as
    // the frontend's own links name them (array arguments as name[]). A 7.0 list link sets filter_set=1, as the list's
    // own filter form does.
    private static FrozenDictionary<(ZabbixPageKind Kind, ZabbixPageView View), Route[]> BuildRoutes()
    {
        const string Zabbix = "zabbix.php";
        var table = new Dictionary<(ZabbixPageKind, ZabbixPageView), Route[]>
        {
            [(ZabbixPageKind.Host, ZabbixPageView.Edit)] =
            [
                new(s_v70, Zabbix, t => [("action", "host.edit"), ("hostid", t.Id.ToString(CultureInfo.InvariantCulture))]),
                new(s_v74, Zabbix, t => [.. Popup("host.edit"), ("hostid", t.Id.ToString(CultureInfo.InvariantCulture))]),
            ],
            [(ZabbixPageKind.Host, ZabbixPageView.Latest)] = [new(s_v70, Zabbix, t => [("action", "latest.view"), ("hostids[]", t.Id.ToString(CultureInfo.InvariantCulture)), ("filter_set", "1")])],
            [(ZabbixPageKind.Host, ZabbixPageView.Problems)] = [new(s_v70, Zabbix, t => [("action", "problem.view"), ("hostids[]", t.Id.ToString(CultureInfo.InvariantCulture)), ("filter_set", "1")])],

            [(ZabbixPageKind.HostGroup, ZabbixPageView.Edit)] =
            [
                new(s_v70, Zabbix, t => [("action", "hostgroup.edit"), ("groupid", t.Id.ToString(CultureInfo.InvariantCulture))]),
                new(s_v74, Zabbix, t => [.. Popup("hostgroup.edit"), ("groupid", t.Id.ToString(CultureInfo.InvariantCulture))]),
            ],
            [(ZabbixPageKind.HostGroup, ZabbixPageView.Latest)] = [new(s_v70, Zabbix, t => [("action", "latest.view"), ("groupids[]", t.Id.ToString(CultureInfo.InvariantCulture)), ("filter_set", "1")])],
            [(ZabbixPageKind.HostGroup, ZabbixPageView.Problems)] = [new(s_v70, Zabbix, t => [("action", "problem.view"), ("groupids[]", t.Id.ToString(CultureInfo.InvariantCulture)), ("filter_set", "1")])],

            [(ZabbixPageKind.Template, ZabbixPageView.Edit)] =
            [
                new(s_v70, Zabbix, t => [("action", "template.list"), .. FilterByName(t)]),
                new(s_v74, Zabbix, t => [.. Popup("template.edit"), ("templateid", t.Id.ToString(CultureInfo.InvariantCulture))]),
            ],

            [(ZabbixPageKind.TemplateGroup, ZabbixPageView.Edit)] =
            [
                new(s_v70, Zabbix, t => [("action", "templategroup.edit"), ("groupid", t.Id.ToString(CultureInfo.InvariantCulture))]),
                new(s_v74, Zabbix, t => [.. Popup("templategroup.edit"), ("groupid", t.Id.ToString(CultureInfo.InvariantCulture))]),
            ],

            [(ZabbixPageKind.Item, ZabbixPageView.Edit)] =
            [
                new(s_v70, Zabbix, t => [("action", "item.list"), ("context", ContextOf(t)), ("filter_hostids[]", HostOf(t)), .. FilterByName(t)]),
                new(s_v74, Zabbix, t => [.. Popup("item.edit"), ("context", ContextOf(t)), ("itemid", t.Id.ToString(CultureInfo.InvariantCulture))]),
            ],
            [(ZabbixPageKind.Item, ZabbixPageView.Latest)] =
                [new(s_v70, Zabbix, t => [("action", "latest.view"), ("hostids[]", HostOf(t, latest: true)), ("name", NameOf(t)), ("filter_set", "1")])],
            [(ZabbixPageKind.Item, ZabbixPageView.Graph)] = [new(s_v70, "history.php", t => [("action", "showgraph"), ("itemids[]", t.Id.ToString(CultureInfo.InvariantCulture))])],
            [(ZabbixPageKind.Item, ZabbixPageView.Values)] = [new(s_v70, "history.php", t => [("action", "showvalues"), ("itemids[]", t.Id.ToString(CultureInfo.InvariantCulture))])],

            [(ZabbixPageKind.Trigger, ZabbixPageView.Edit)] =
            [
                new(s_v70, Zabbix, t => [("action", "trigger.list"), ("context", ContextOf(t)), ("filter_hostids[]", HostOf(t)), .. FilterByName(t)]),
                new(s_v74, Zabbix, t => [.. Popup("trigger.edit"), ("context", ContextOf(t)), ("triggerid", t.Id.ToString(CultureInfo.InvariantCulture))]),
            ],
            [(ZabbixPageKind.Trigger, ZabbixPageView.Problems)] = [new(s_v70, Zabbix, t => [("action", "problem.view"), ("triggerids[]", t.Id.ToString(CultureInfo.InvariantCulture)), ("filter_set", "1")])],

            [(ZabbixPageKind.Graph, ZabbixPageView.Edit)] =
            [
                new(s_v70, "graphs.php", t => [("form", "update"), ("context", ContextOf(t)), ("graphid", t.Id.ToString(CultureInfo.InvariantCulture))]),
                new(s_v74, Zabbix, t => [.. Popup("graph.edit"), ("context", ContextOf(t)), ("graphid", t.Id.ToString(CultureInfo.InvariantCulture))]),
            ],

            [(ZabbixPageKind.DiscoveryRule, ZabbixPageView.Edit)] =
            [
                new(s_v70, "host_discovery.php", t => [("form", "update"), ("context", ContextOf(t)), ("itemid", t.Id.ToString(CultureInfo.InvariantCulture))]),
                new(s_v80, Zabbix, t => [.. Popup("lldrule.edit"), ("context", ContextOf(t)), ("itemid", t.Id.ToString(CultureInfo.InvariantCulture))]),
            ],

            [(ZabbixPageKind.WebScenario, ZabbixPageView.Edit)] =
                [new(s_v70, "httpconf.php", t => [("form", "update"), ("context", ContextOf(t)), ("hostid", HostOf(t)), ("httptestid", t.Id.ToString(CultureInfo.InvariantCulture))])],

            [(ZabbixPageKind.Dashboard, ZabbixPageView.Default)] = [new(s_v70, Zabbix, t => [("action", "dashboard.view"), ("dashboardid", t.Id.ToString(CultureInfo.InvariantCulture))])],

            [(ZabbixPageKind.Map, ZabbixPageView.Default)] = [new(s_v70, Zabbix, t => [("action", "map.view"), ("sysmapid", t.Id.ToString(CultureInfo.InvariantCulture))])],
            [(ZabbixPageKind.Map, ZabbixPageView.Edit)] = [new(s_v70, "sysmap.php", t => [("sysmapid", t.Id.ToString(CultureInfo.InvariantCulture))])],

            [(ZabbixPageKind.Problem, ZabbixPageView.Default)] = [new(s_v70, "tr_events.php", t => [("triggerid", TriggerOf(t)), ("eventid", t.Id.ToString(CultureInfo.InvariantCulture))])],
            [(ZabbixPageKind.Event, ZabbixPageView.Default)] = [new(s_v70, "tr_events.php", t => [("triggerid", TriggerOf(t)), ("eventid", t.Id.ToString(CultureInfo.InvariantCulture))])],

            [(ZabbixPageKind.Maintenance, ZabbixPageView.Edit)] =
            [
                new(s_v70, Zabbix, t => [("action", "maintenance.list"), .. FilterByName(t)]),
                new(s_v74, Zabbix, t => [.. Popup("maintenance.edit"), ("maintenanceid", t.Id.ToString(CultureInfo.InvariantCulture))]),
            ],

            [(ZabbixPageKind.User, ZabbixPageView.Edit)] = [new(s_v70, Zabbix, t => [("action", "user.edit"), ("userid", t.Id.ToString(CultureInfo.InvariantCulture))])],
            [(ZabbixPageKind.UserGroup, ZabbixPageView.Edit)] = [new(s_v70, Zabbix, t => [("action", "usergroup.edit"), ("usrgrpid", t.Id.ToString(CultureInfo.InvariantCulture))])],

            [(ZabbixPageKind.Proxy, ZabbixPageView.Edit)] =
            [
                new(s_v70, Zabbix, t => [("action", "proxy.list"), .. FilterByName(t)]),
                new(s_v74, Zabbix, t => [.. Popup("proxy.edit"), ("proxyid", t.Id.ToString(CultureInfo.InvariantCulture))]),
            ],

            [(ZabbixPageKind.NetworkDiscoveryRule, ZabbixPageView.Edit)] =
            [
                new(s_v70, Zabbix, t => [("action", "discovery.list"), .. FilterByName(t)]),
                new(s_v74, Zabbix, t => [.. Popup("discovery.edit"), ("druleid", t.Id.ToString(CultureInfo.InvariantCulture))]),
            ],

            [(ZabbixPageKind.Action, ZabbixPageView.Edit)] =
            [
                new(s_v70, Zabbix, t => [("action", "action.list"), ("eventsource", SourceOf(t)), .. FilterByName(t)]),
                new(s_v74, Zabbix, t => [.. Popup("action.edit"), ("eventsource", SourceOf(t)), ("actionid", t.Id.ToString(CultureInfo.InvariantCulture))]),
            ],

            [(ZabbixPageKind.MediaType, ZabbixPageView.Edit)] =
            [
                new(s_v70, Zabbix, t => [("action", "mediatype.list"), .. FilterByName(t)]),
                new(s_v74, Zabbix, t => [.. Popup("mediatype.edit"), ("mediatypeid", t.Id.ToString(CultureInfo.InvariantCulture))]),
            ],

            [(ZabbixPageKind.Script, ZabbixPageView.Edit)] =
            [
                new(s_v70, Zabbix, t => [("action", "script.list"), .. FilterByName(t)]),
                new(s_v74, Zabbix, t => [.. Popup("script.edit"), ("scriptid", t.Id.ToString(CultureInfo.InvariantCulture))]),
            ],

            [(ZabbixPageKind.Service, ZabbixPageView.Default)] = [new(s_v70, Zabbix, t => [("action", "service.list"), ("serviceid", t.Id.ToString(CultureInfo.InvariantCulture))])],
            [(ZabbixPageKind.Service, ZabbixPageView.Edit)] =
            [
                new(s_v70, Zabbix, t => [("action", "service.list.edit"), ("serviceid", t.Id.ToString(CultureInfo.InvariantCulture))]),
                new(s_v74, Zabbix, t => [.. Popup("service.edit"), ("serviceid", t.Id.ToString(CultureInfo.InvariantCulture))]),
            ],

            [(ZabbixPageKind.Sla, ZabbixPageView.Edit)] =
            [
                new(s_v70, Zabbix, t => [("action", "sla.list"), .. FilterByName(t)]),
                new(s_v74, Zabbix, t => [.. Popup("sla.edit"), ("slaid", t.Id.ToString(CultureInfo.InvariantCulture))]),
            ],
        };
        return table.ToFrozenDictionary();
    }

    private static (string, string)[] Popup(string popup) => [("action", "popup"), ("popup", popup)];

    private static (string, string)[] FilterByName(ZabbixPageTarget target) => [("filter_set", "1"), ("filter_name", NameOf(target))];

    private static string ContextOf(ZabbixPageTarget target) => target.Context switch
    {
        ZabbixPageContext.Host => "host",
        ZabbixPageContext.Template => "template",
        _ => throw Missing(target, "whether it is on a host or a template (read it with selectHosts, or set Context)"),
    };

    private static string HostOf(ZabbixPageTarget target, bool latest = false)
    {
        if (latest && target.Context != ZabbixPageContext.Host)
        {
            throw target.Context is null
                ? Missing(target, "whether it is on a host or a template (read it with selectHosts, or set Context)")
                : new ArgumentException("An item on a template has no latest data; only a host's item has.", nameof(target));
        }
        return target.HostId is ulong host and not 0 ? host.ToString(CultureInfo.InvariantCulture) : throw Missing(target, "its host (read it with selectHosts, or set HostId)");
    }

    private static string NameOf(ZabbixPageTarget target)
        => string.IsNullOrEmpty(target.Name) ? throw Missing(target, "its name, which this version's list is filtered by (read it with its name, or set Name)") : target.Name;

    private static string SourceOf(ZabbixPageTarget target)
        => target.EventSource is { } source
            ? ((int)source).ToString(CultureInfo.InvariantCulture)
            : throw Missing(target, "its event source (read it with eventsource, or set EventSource)");

    private static string TriggerOf(ZabbixPageTarget target)
        => target.TriggerId is ulong trigger and not 0
            ? trigger.ToString(CultureInfo.InvariantCulture)
            : throw Missing(target, "its trigger: only a trigger's problem or event has a page (read it with source, object and objectid, or set TriggerId)");

    private static ArgumentException Missing(ZabbixPageTarget target, string what)
        => new($"The page of {Describe(target.Kind)} {target.Id} needs {what}.", nameof(target));
}
