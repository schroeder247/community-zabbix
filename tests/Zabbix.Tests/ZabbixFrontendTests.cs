// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>
/// The frontend's page links: every (kind, view) per version, against the routes read from the pinned frontend sources
/// (the table's own comment names where each comes from), and the base the links hang off.
/// </summary>
public sealed class ZabbixFrontendTests
{
    private static readonly Uri Base = new("https://zbx.example/zabbix/");

    private static readonly ZabbixHost OnHost = new() { HostId = 10084, Status = ZabbixHostStatus.Enabled };

    private static readonly ZabbixHost OnTemplate = new() { HostId = 10001, Status = ZabbixHostStatus.Template };

    /// <summary>Each case: the object as code holds it, the view, and the page per version (7.0, 7.4, 8.0); null repeats the one before.</summary>
    private static readonly Dictionary<string, (IZabbixFrontendPage Record, ZabbixPageView View, string V70, string? V74, string? V80)> Cases = new()
    {
        ["host"] = (new ZabbixHost { HostId = 10084, VisibleName = "Zabbix server" }, ZabbixPageView.Default,
            "zabbix.php?action=host.edit&hostid=10084", "zabbix.php?action=popup&popup=host.edit&hostid=10084", null),
        ["host-edit"] = (new ZabbixHost { HostId = 10084 }, ZabbixPageView.Edit,
            "zabbix.php?action=host.edit&hostid=10084", "zabbix.php?action=popup&popup=host.edit&hostid=10084", null),
        ["host-latest"] = (new ZabbixHost { HostId = 10084 }, ZabbixPageView.Latest,
            "zabbix.php?action=latest.view&hostids%5B%5D=10084&filter_set=1", null, null),
        ["host-problems"] = (new ZabbixHost { HostId = 10084 }, ZabbixPageView.Problems,
            "zabbix.php?action=problem.view&hostids%5B%5D=10084&filter_set=1", null, null),
        ["hostgroup"] = (new ZabbixHostGroup { GroupId = 2, Name = "Linux servers" }, ZabbixPageView.Default,
            "zabbix.php?action=hostgroup.edit&groupid=2", "zabbix.php?action=popup&popup=hostgroup.edit&groupid=2", null),
        ["hostgroup-latest"] = (new ZabbixHostGroup { GroupId = 2 }, ZabbixPageView.Latest,
            "zabbix.php?action=latest.view&groupids%5B%5D=2&filter_set=1", null, null),
        ["hostgroup-problems"] = (new ZabbixHostGroup { GroupId = 2 }, ZabbixPageView.Problems,
            "zabbix.php?action=problem.view&groupids%5B%5D=2&filter_set=1", null, null),
        ["template"] = (new ZabbixTemplate { TemplateId = 10001, VisibleName = "Linux by Zabbix agent" }, ZabbixPageView.Default,
            "zabbix.php?action=template.list&filter_set=1&filter_name=Linux%20by%20Zabbix%20agent",
            "zabbix.php?action=popup&popup=template.edit&templateid=10001", null),
        ["templategroup"] = (new ZabbixTemplateGroup { GroupId = 1, Name = "Templates" }, ZabbixPageView.Default,
            "zabbix.php?action=templategroup.edit&groupid=1", "zabbix.php?action=popup&popup=templategroup.edit&groupid=1", null),
        ["item"] = (new ZabbixItem { ItemId = 42237, HostId = 10084, Name = "CPU load", Hosts = [OnHost] }, ZabbixPageView.Default,
            "zabbix.php?action=item.list&context=host&filter_hostids%5B%5D=10084&filter_set=1&filter_name=CPU%20load",
            "zabbix.php?action=popup&popup=item.edit&context=host&itemid=42237", null),
        ["item-template"] = (new ZabbixItem { ItemId = 42240, HostId = 10001, Name = "CPU load", Hosts = [OnTemplate] }, ZabbixPageView.Edit,
            "zabbix.php?action=item.list&context=template&filter_hostids%5B%5D=10001&filter_set=1&filter_name=CPU%20load",
            "zabbix.php?action=popup&popup=item.edit&context=template&itemid=42240", null),
        ["item-latest"] = (new ZabbixItem { ItemId = 42237, HostId = 10084, Name = "CPU load", Hosts = [OnHost] }, ZabbixPageView.Latest,
            "zabbix.php?action=latest.view&hostids%5B%5D=10084&name=CPU%20load&filter_set=1", null, null),
        ["item-graph"] = (new ZabbixItem { ItemId = 42237 }, ZabbixPageView.Graph, "history.php?action=showgraph&itemids%5B%5D=42237", null, null),
        ["item-values"] = (new ZabbixItem { ItemId = 42237 }, ZabbixPageView.Values, "history.php?action=showvalues&itemids%5B%5D=42237", null, null),
        ["trigger"] = (new ZabbixTrigger { TriggerId = 13491, Name = "Load is high", Hosts = [OnHost] }, ZabbixPageView.Default,
            "zabbix.php?action=trigger.list&context=host&filter_hostids%5B%5D=10084&filter_set=1&filter_name=Load%20is%20high",
            "zabbix.php?action=popup&popup=trigger.edit&context=host&triggerid=13491", null),
        ["trigger-problems"] = (new ZabbixTrigger { TriggerId = 13491 }, ZabbixPageView.Problems,
            "zabbix.php?action=problem.view&triggerids%5B%5D=13491&filter_set=1", null, null),
        ["graph"] = (new ZabbixGraph { GraphId = 910, Name = "CPU", Hosts = [OnHost] }, ZabbixPageView.Default,
            "graphs.php?form=update&context=host&graphid=910", "zabbix.php?action=popup&popup=graph.edit&context=host&graphid=910", null),
        ["graph-template"] = (new ZabbixGraph { GraphId = 911, Templates = [new ZabbixTemplate { TemplateId = 10001 }] }, ZabbixPageView.Edit,
            "graphs.php?form=update&context=template&graphid=911", "zabbix.php?action=popup&popup=graph.edit&context=template&graphid=911", null),
        ["discoveryrule"] = (new ZabbixDiscoveryRule { ItemId = 42500, HostId = 10001, Hosts = [OnTemplate] }, ZabbixPageView.Default,
            "host_discovery.php?form=update&context=template&itemid=42500", null, "zabbix.php?action=popup&popup=lldrule.edit&context=template&itemid=42500"),
        ["webscenario"] = (new ZabbixHttpTest { HttpTestId = 5, HostId = 10084, Hosts = [OnHost] }, ZabbixPageView.Default,
            "httpconf.php?form=update&context=host&hostid=10084&httptestid=5", null, null),
        ["dashboard"] = (new ZabbixDashboard { DashboardId = 1 }, ZabbixPageView.Default, "zabbix.php?action=dashboard.view&dashboardid=1", null, null),
        ["map"] = (new ZabbixMap { MapId = 1 }, ZabbixPageView.Default, "zabbix.php?action=map.view&sysmapid=1", null, null),
        ["map-edit"] = (new ZabbixMap { MapId = 1 }, ZabbixPageView.Edit, "sysmap.php?sysmapid=1", null, null),
        ["problem"] = (new ZabbixProblem { EventId = 77, Source = ZabbixEventSource.Triggers, ObjectType = ZabbixEventObject.Trigger, ObjectId = 13491 },
            ZabbixPageView.Default, "tr_events.php?triggerid=13491&eventid=77", null, null),
        ["event"] = (new ZabbixEvent { EventId = 78, Source = ZabbixEventSource.Triggers, ObjectType = ZabbixEventObject.Trigger, ObjectId = 13491 },
            ZabbixPageView.Default, "tr_events.php?triggerid=13491&eventid=78", null, null),
        ["maintenance"] = (new ZabbixMaintenanceRecord { MaintenanceId = 3, Name = "Weekly & nightly" }, ZabbixPageView.Default,
            "zabbix.php?action=maintenance.list&filter_set=1&filter_name=Weekly%20%26%20nightly",
            "zabbix.php?action=popup&popup=maintenance.edit&maintenanceid=3", null),
        ["user"] = (new ZabbixUser { UserId = 1, UserName = "Admin" }, ZabbixPageView.Default, "zabbix.php?action=user.edit&userid=1", null, null),
        ["usergroup"] = (new ZabbixUserGroup { UserGroupId = 7, Name = "Zabbix administrators" }, ZabbixPageView.Default,
            "zabbix.php?action=usergroup.edit&usrgrpid=7", null, null),
        ["proxy"] = (new ZabbixProxy { ProxyId = 11, Name = "proxy-a" }, ZabbixPageView.Default,
            "zabbix.php?action=proxy.list&filter_set=1&filter_name=proxy-a", "zabbix.php?action=popup&popup=proxy.edit&proxyid=11", null),
        ["networkdiscoveryrule"] = (new ZabbixNetworkDiscoveryRule { NetworkDiscoveryRuleId = 2, Name = "Local network" }, ZabbixPageView.Default,
            "zabbix.php?action=discovery.list&filter_set=1&filter_name=Local%20network", "zabbix.php?action=popup&popup=discovery.edit&druleid=2", null),
        ["action"] = (new ZabbixAction { ActionId = 3, Name = "Report problems", EventSource = ZabbixEventSource.Triggers }, ZabbixPageView.Default,
            "zabbix.php?action=action.list&eventsource=0&filter_set=1&filter_name=Report%20problems",
            "zabbix.php?action=popup&popup=action.edit&eventsource=0&actionid=3", null),
        ["action-autoregistration"] = (new ZabbixAction { ActionId = 4, Name = "Register", EventSource = ZabbixEventSource.Autoregistration }, ZabbixPageView.Edit,
            "zabbix.php?action=action.list&eventsource=2&filter_set=1&filter_name=Register",
            "zabbix.php?action=popup&popup=action.edit&eventsource=2&actionid=4", null),
        ["mediatype"] = (new ZabbixMediaType { MediaTypeId = 1, Name = "Email" }, ZabbixPageView.Default,
            "zabbix.php?action=mediatype.list&filter_set=1&filter_name=Email", "zabbix.php?action=popup&popup=mediatype.edit&mediatypeid=1", null),
        ["script"] = (new ZabbixScript { ScriptId = 1, Name = "Ping" }, ZabbixPageView.Default,
            "zabbix.php?action=script.list&filter_set=1&filter_name=Ping", "zabbix.php?action=popup&popup=script.edit&scriptid=1", null),
        ["service"] = (new ZabbixServiceRecord { ServiceId = 4, Name = "Shop" }, ZabbixPageView.Default, "zabbix.php?action=service.list&serviceid=4", null, null),
        ["service-edit"] = (new ZabbixServiceRecord { ServiceId = 4, Name = "Shop" }, ZabbixPageView.Edit,
            "zabbix.php?action=service.list.edit&serviceid=4", "zabbix.php?action=popup&popup=service.edit&serviceid=4", null),
        ["sla"] = (new ZabbixSlaRecord { SlaId = 2, Name = "Gold" }, ZabbixPageView.Default,
            "zabbix.php?action=sla.list&filter_set=1&filter_name=Gold", "zabbix.php?action=popup&popup=sla.edit&slaid=2", null),
    };

    public static TheoryData<string, string> Pages()
    {
        var data = new TheoryData<string, string>();
        foreach (string version in new[] { "7.0", "7.4", "8.0" })
        {
            foreach (string name in Cases.Keys) data.Add(version, name);
        }
        return data;
    }

    [Theory, MemberData(nameof(Pages))]
    public void Each_kind_and_view_links_the_page_its_version_routes_it_to(string version, string name)
    {
        (IZabbixFrontendPage record, ZabbixPageView view, string v70, string? v74, string? v80) = Cases[name];
        string expected = version switch
        {
            "7.0" => v70,
            "7.4" => v74 ?? v70,
            _ => v80 ?? v74 ?? v70,
        };
        var frontend = new ZabbixFrontend(Base, Version.Parse(version + ".3"));

        Uri page = frontend.UriFor(record, view);

        Assert.Equal("https://zbx.example/zabbix/" + expected, page.AbsoluteUri);
        Assert.Equal(page, frontend.UriFor(record.ToPageTarget(), view));
    }

    [Fact]
    public void Every_kind_and_every_view_it_has_is_a_case()
    {
        List<(ZabbixPageKind Kind, ZabbixPageView View)> covered =
            [.. Cases.Values.Select(c => (c.Record.ToPageTarget().Kind, c.View))];
        foreach (ZabbixPageKind kind in Enum.GetValues<ZabbixPageKind>())
        {
            Assert.Contains(covered, c => c.Kind == kind && c.View == ZabbixPageView.Default);
            foreach (ZabbixPageView view in ZabbixFrontend.ViewsOf(kind).Skip(1))
                Assert.True(covered.Contains((kind, view)) || (view == ZabbixPageView.Edit && covered.Contains((kind, ZabbixPageView.Default))), $"{kind} {view}");
        }
    }

    [Fact]
    public void A_record_with_a_page_gives_the_target_its_fields_fill()
    {
        var frontend = new ZabbixFrontend(Base, new Version(7, 4));
        IZabbixFrontendPage host = new ZabbixHost { HostId = 10084, VisibleName = "Zabbix server" };
        IZabbixFrontendPage item = new ZabbixItem { ItemId = 42237, HostId = 10084, Hosts = [OnHost] };
        ZabbixPageTarget target = host.ToPageTarget();

        Assert.Equal(new ZabbixPageTarget(ZabbixPageKind.Host, 10084UL) { Name = "Zabbix server" }, target);
        Assert.Equal(frontend.UriFor(target), frontend.UriFor(host));
        Assert.Equal(new ZabbixPageTarget(ZabbixPageKind.Host, 10084UL) { Name = "web-01" }, ((IZabbixFrontendPage)new ZabbixHost { HostId = 10084, HostName = "web-01" }).ToPageTarget());
        Assert.Equal(ZabbixPageContext.Host, item.ToPageTarget().Context);
        Assert.Equal(10084UL, item.ToPageTarget().HostId);
        Assert.Same(target, ((IZabbixFrontendPage)target).ToPageTarget());
    }

    [Fact]
    public void A_record_s_target_reads_its_host_context_source_and_trigger_from_what_it_carries()
    {
        IZabbixFrontendPage onTemplate = new ZabbixTrigger { TriggerId = 1, Hosts = [OnTemplate] };
        IZabbixFrontendPage graph = new ZabbixGraph { GraphId = 2, Templates = [new ZabbixTemplate { TemplateId = 10001 }] };
        IZabbixFrontendPage problem = new ZabbixProblem { EventId = 3, Source = ZabbixEventSource.Triggers, ObjectType = ZabbixEventObject.Trigger, ObjectId = 13491 };
        IZabbixFrontendPage internalEvent = new ZabbixEvent { EventId = 4, Source = ZabbixEventSource.Internal, ObjectType = ZabbixEventObject.Item, ObjectId = 9 };
        IZabbixFrontendPage action = new ZabbixAction { ActionId = 5, EventSource = ZabbixEventSource.Discovery };

        Assert.Equal((ZabbixPageContext?)ZabbixPageContext.Template, onTemplate.ToPageTarget().Context);
        Assert.Equal((10001UL, (ZabbixPageContext?)ZabbixPageContext.Template), (graph.ToPageTarget().HostId!.Value, graph.ToPageTarget().Context));
        Assert.Equal(13491UL, problem.ToPageTarget().TriggerId);
        Assert.Null(internalEvent.ToPageTarget().TriggerId);
        Assert.Equal(ZabbixEventSource.Discovery, action.ToPageTarget().EventSource);
        Assert.Null(((IZabbixFrontendPage)new ZabbixItem { ItemId = 6 }).ToPageTarget().Context);
    }

    [Fact]
    public void A_null_page_is_refused()
        => Assert.Throws<ArgumentNullException>("page", () => new ZabbixFrontend(Base, new Version(7, 4)).UriFor(null!));

    [Fact]
    public void The_views_of_a_kind_list_the_default_first()
    {
        Assert.Equal([ZabbixPageView.Default, ZabbixPageView.Edit, ZabbixPageView.Latest, ZabbixPageView.Problems], ZabbixFrontend.ViewsOf(ZabbixPageKind.Host));
        Assert.Equal([ZabbixPageView.Default, ZabbixPageView.Edit, ZabbixPageView.Latest, ZabbixPageView.Graph, ZabbixPageView.Values], ZabbixFrontend.ViewsOf(ZabbixPageKind.Item));
        Assert.Equal([ZabbixPageView.Default], ZabbixFrontend.ViewsOf(ZabbixPageKind.Dashboard));
    }

    [Fact]
    public void A_bare_id_is_a_target_and_id_0_is_refused()
    {
        var frontend = new ZabbixFrontend(Base, new Version(8, 0, 0));

        Assert.Equal("https://zbx.example/zabbix/zabbix.php?action=map.view&sysmapid=3", frontend.UriFor(new ZabbixPageTarget(ZabbixPageKind.Map, 3UL)).AbsoluteUri);
        Assert.Throws<ArgumentException>(() => new ZabbixPageTarget(ZabbixPageKind.Host, 0UL));
    }

    [Fact]
    public void A_record_read_without_its_id_is_refused_at_the_link_not_when_it_gives_its_target()
    {
        var frontend = new ZabbixFrontend(Base, new Version(7, 4));
        var host = new ZabbixHost { VisibleName = "no id" };

        Assert.Equal(0UL, ((IZabbixFrontendPage)host).ToPageTarget().Id);
        ArgumentException refused = Assert.Throws<ArgumentException>(() => frontend.UriFor(host));
        Assert.Contains("carries no id", refused.Message, StringComparison.Ordinal);
        Assert.Equal("page", refused.ParamName);
    }

    [Theory]
    [InlineData("7.0")]
    [InlineData("7.4")]
    public void What_a_route_needs_and_the_target_lacks_is_named(string version)
    {
        var frontend = new ZabbixFrontend(Base, Version.Parse(version));

        ArgumentException context = Assert.Throws<ArgumentException>(() => frontend.UriFor(new ZabbixItem { ItemId = 1, HostId = 2, Name = "x" }));
        Assert.Contains("selectHosts", context.Message, StringComparison.Ordinal);
        Assert.Equal("page", context.ParamName);
        ArgumentException source = Assert.Throws<ArgumentException>(() => frontend.UriFor(new ZabbixAction { ActionId = 3, Name = "a" }));
        Assert.Contains("event source", source.Message, StringComparison.Ordinal);
        ArgumentException trigger = Assert.Throws<ArgumentException>(() => frontend.UriFor(new ZabbixEvent { EventId = 4, Source = ZabbixEventSource.Internal, ObjectType = ZabbixEventObject.Item, ObjectId = 9 }));
        Assert.Contains("trigger", trigger.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => frontend.UriFor(new ZabbixItem { ItemId = 1, HostId = 2, Name = "x", Hosts = [OnTemplate] }, ZabbixPageView.Latest));
    }

    [Fact]
    public void Seven_oh_filters_its_lists_by_name_so_a_bare_id_has_no_form_there_but_has_one_later()
    {
        var target = new ZabbixPageTarget(ZabbixPageKind.Template, 10001UL);

        ArgumentException refused = Assert.Throws<ArgumentException>(() => new ZabbixFrontend(Base, new Version(7, 0)).UriFor(target));
        Assert.Contains("name", refused.Message, StringComparison.Ordinal);
        Assert.Equal("https://zbx.example/zabbix/zabbix.php?action=popup&popup=template.edit&templateid=10001", new ZabbixFrontend(Base, new Version(7, 4)).UriFor(target).AbsoluteUri);
    }

    [Fact]
    public void A_view_the_kind_has_not_is_refused_with_the_views_it_has()
    {
        var frontend = new ZabbixFrontend(Base, new Version(7, 0));

        ArgumentException refused = Assert.Throws<ArgumentException>(() => frontend.UriFor(new ZabbixDashboard { DashboardId = 1 }, ZabbixPageView.Edit));
        Assert.Equal("view", refused.ParamName);
        Assert.Contains("Default", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_version_before_seven_has_no_known_routes_and_a_newer_one_takes_the_newest()
    {
        ZabbixVersionException refused = Assert.Throws<ZabbixVersionException>(() => new ZabbixFrontend(Base, new Version(6, 4, 20)));
        Assert.Equal(new Version(6, 4, 20), refused.ServerVersion);
        Assert.IsAssignableFrom<NotSupportedException>(refused);
        Assert.Equal(new Version(9, 2), new ZabbixFrontend(Base, new Version(9, 2, 1)).Version);
        Assert.Equal("https://zbx.example/zabbix/zabbix.php?action=popup&popup=lldrule.edit&context=host&itemid=5",
            new ZabbixFrontend(Base, new Version(9, 2)).UriFor(new ZabbixDiscoveryRule { ItemId = 5, Hosts = [OnHost] }).AbsoluteUri);
    }

    [Theory]
    [InlineData("https://zbx.example/api_jsonrpc.php", null, "https://zbx.example/")]
    [InlineData("https://zbx.example/zabbix/api_jsonrpc.php", null, "https://zbx.example/zabbix/")]
    [InlineData("https://user:secret@zbx.example/zabbix/api_jsonrpc.php?x=1", null, "https://zbx.example/zabbix/")]
    [InlineData("https://api.zbx.example/api_jsonrpc.php", "https://ui.zbx.example/monitoring", "https://ui.zbx.example/monitoring/")]
    public void The_base_is_the_endpoints_folder_unless_the_options_name_one(string endpoint, string? frontend, string expected)
    {
        ZabbixClientBuilder builder = ZabbixClient.CreateBuilder().WithEndpoint(new Uri(endpoint));
        if (frontend is not null) builder.WithFrontend(new Uri(frontend));
        using ZabbixClient client = builder.Build();

        Assert.Equal(expected, client.FrontendUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("ftp://zbx.example/")]
    [InlineData("/zabbix/")]
    public void A_frontend_uri_that_is_not_absolute_http_fails_validation(string frontend)
    {
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(
            () => ZabbixClient.CreateBuilder().WithEndpoint("https://zbx.example/api_jsonrpc.php").WithFrontend(new Uri(frontend, UriKind.RelativeOrAbsolute)).Build().Dispose());
        Assert.Contains("FrontendUri", refused.Message, StringComparison.Ordinal);
        Assert.IsType<OptionsValidationException>(refused.InnerException);
        Assert.Throws<ArgumentException>(() => new ZabbixFrontend(new Uri(frontend, UriKind.RelativeOrAbsolute), new Version(7, 0)));
    }

    [Fact]
    public async Task The_servers_frontend_carries_its_base_and_the_version_it_reports()
    {
        using var api = new ScriptedApi().On("apiinfo.version", "\"7.4.15\"");
        using Harness harness = TransportHarness.Build(api);
        ZabbixClient client = harness.Provider.GetRequiredService<ZabbixClient>();

        ZabbixFrontend frontend = await client.GetFrontendAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new Version(7, 4), frontend.Version);
        Assert.Equal(client.FrontendUri, frontend.BaseUri);
        Assert.Equal(new Uri(client.FrontendUri, "zabbix.php?action=popup&popup=host.edit&hostid=10084"), frontend.UriFor(new ZabbixHost { HostId = 10084 }));
    }
}
