// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Text.RegularExpressions;

namespace Community.Zabbix.Tests;

/// <summary>
/// The frontend's page links, opened in each version's own frontend: signed in as the lab's administrator (the frontend
/// takes no API token), every kind and view <see cref="ZabbixFrontend"/> links answers a page of the frontend, never its
/// <c>Warning</c> page (a route, argument or context the controller refuses) nor a dialog's JSON (a dialog addressed as a
/// page). A bogus id answering <c>Warning</c> is the control that the check tells the two apart.
/// </summary>
public sealed partial class LiveFrontendTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task Every_kind_and_view_opens_its_page_in_the_signed_in_frontend(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "frontend");
        ZabbixFrontend frontend = await scope.Server.GetFrontendAsync(Cancel);
        using var handler = new SocketsHttpHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false };
        using var browser = new HttpClient(handler);
        await SignInAsync(browser, frontend);
        Fixtures fixtures = await CreateFixturesAsync(scope);
        ZabbixApi api = scope.Server.Api;

        // The lab's stock objects, by name, and the test's own: never "the first" of a kind, which under the live suite can be
        // another test's scoped object, deleted before its page is fetched. The lowest id on a stock template or host is a
        // stock item, trigger, graph or rule.
        ZabbixPageTarget template = First(await api.Templates.GetAsync(new ZabbixTemplateGetParameters { Filter = new() { ["host"] = "Linux by Zabbix agent" } }, Cancel));
        ZabbixPageTarget server = First(await api.Hosts.GetAsync(new ZabbixHostGetParameters { Filter = new() { ["host"] = "Zabbix server" } }, Cancel));
        string[] hostContext = ["hostid", "status"];
        var pages = new List<(string What, ZabbixPageTarget Target, ZabbixPageView View)>
        {
            ("host", First(await api.Hosts.GetAsync(new ZabbixHostGetParameters { HostIds = [fixtures.Host] }, Cancel)), ZabbixPageView.Default),
            ("host latest", new ZabbixPageTarget(ZabbixPageKind.Host, fixtures.Host), ZabbixPageView.Latest),
            ("host problems", new ZabbixPageTarget(ZabbixPageKind.Host, fixtures.Host), ZabbixPageView.Problems),
            ("host group", First(await api.HostGroups.GetAsync(new ZabbixHostGroupGetParameters { GroupIds = [fixtures.Group] }, Cancel)), ZabbixPageView.Default),
            ("host group latest", new ZabbixPageTarget(ZabbixPageKind.HostGroup, fixtures.Group), ZabbixPageView.Latest),
            ("host group problems", new ZabbixPageTarget(ZabbixPageKind.HostGroup, fixtures.Group), ZabbixPageView.Problems),
            ("template", template, ZabbixPageView.Default),
            ("template group", First(await api.TemplateGroups.GetAsync(new ZabbixTemplateGroupGetParameters { Filter = new() { ["name"] = "Templates/Operating systems" } }, Cancel)), ZabbixPageView.Default),
            ("item on a host", First(await api.Items.GetAsync(new ZabbixItemGetParameters { HostIds = [server.Id], SortField = ["itemid"], Limit = 1, SelectHosts = hostContext }, Cancel)), ZabbixPageView.Default),
            ("item on a template", First(await api.Items.GetAsync(new ZabbixItemGetParameters { TemplateIds = [template.Id], SortField = ["itemid"], Limit = 1, SelectHosts = hostContext }, Cancel)), ZabbixPageView.Default),
            ("item latest", First(await api.Items.GetAsync(new ZabbixItemGetParameters { HostIds = [server.Id], SortField = ["itemid"], Limit = 1, SelectHosts = hostContext }, Cancel)), ZabbixPageView.Latest),
            ("item graph", First(await api.Items.GetAsync(new ZabbixItemGetParameters { HostIds = [server.Id], SortField = ["itemid"], Limit = 1 }, Cancel)), ZabbixPageView.Graph),
            ("item values", First(await api.Items.GetAsync(new ZabbixItemGetParameters { HostIds = [server.Id], SortField = ["itemid"], Limit = 1 }, Cancel)), ZabbixPageView.Values),
            ("trigger on a host", First(await api.Triggers.GetAsync(new ZabbixTriggerGetParameters { HostIds = [server.Id], SortField = ["triggerid"], Limit = 1, SelectHosts = hostContext }, Cancel)), ZabbixPageView.Default),
            ("trigger on a template", First(await api.Triggers.GetAsync(new ZabbixTriggerGetParameters { TemplateIds = [template.Id], SortField = ["triggerid"], Limit = 1, SelectHosts = hostContext }, Cancel)), ZabbixPageView.Default),
            ("trigger problems", First(await api.Triggers.GetAsync(new ZabbixTriggerGetParameters { HostIds = [server.Id], SortField = ["triggerid"], Limit = 1 }, Cancel)), ZabbixPageView.Problems),
            ("graph on a template", First(await api.Graphs.GetAsync(new ZabbixGraphGetParameters { TemplateIds = [template.Id], SortField = ["graphid"], Limit = 1, SelectHosts = hostContext }, Cancel)), ZabbixPageView.Default),
            ("discovery rule on a template", First(await api.DiscoveryRules.GetAsync(new ZabbixDiscoveryRuleGetParameters { TemplateIds = [template.Id], SortField = ["itemid"], Limit = 1, SelectHosts = hostContext }, Cancel)), ZabbixPageView.Default),
            ("web scenario", First(await api.HttpTests.GetAsync(new ZabbixHttpTestGetParameters { HttpTestIds = [fixtures.WebScenario], SelectHosts = hostContext }, Cancel)), ZabbixPageView.Default),
            ("dashboard", First(await api.Dashboards.GetAsync(new ZabbixDashboardGetParameters { Filter = new() { ["name"] = "Global view" } }, Cancel)), ZabbixPageView.Default),
            ("map", First(await api.Maps.GetAsync(new ZabbixMapGetParameters { Filter = new() { ["name"] = "Local network" } }, Cancel)), ZabbixPageView.Default),
            ("map editor", First(await api.Maps.GetAsync(new ZabbixMapGetParameters { Filter = new() { ["name"] = "Local network" } }, Cancel)), ZabbixPageView.Edit),
            ("maintenance", First(await api.Maintenances.GetAsync(new ZabbixMaintenanceGetParameters { MaintenanceIds = [fixtures.Maintenance] }, Cancel)), ZabbixPageView.Default),
            ("user", First(await api.Users.GetAsync(new ZabbixUserGetParameters { Filter = new() { ["username"] = "Admin" } }, Cancel)), ZabbixPageView.Default),
            ("user group", First(await api.UserGroups.GetAsync(new ZabbixUserGroupGetParameters { Filter = new() { ["name"] = "Zabbix administrators" } }, Cancel)), ZabbixPageView.Default),
            ("network discovery rule", First(await api.NetworkDiscoveryRules.GetAsync(new ZabbixNetworkDiscoveryRuleGetParameters { Filter = new() { ["name"] = "Local network" } }, Cancel)), ZabbixPageView.Default),
            ("action", First(await api.Actions.GetAsync(new ZabbixActionGetParameters { Filter = new() { ["name"] = "Report problems to Zabbix administrators" } }, Cancel)), ZabbixPageView.Default),
            ("media type", First(await api.MediaTypes.GetAsync(new ZabbixMediaTypeGetParameters { Filter = new() { ["name"] = "Email" } }, Cancel)), ZabbixPageView.Default),
            ("script", First(await api.Scripts.GetAsync(new ZabbixScriptGetParameters { Filter = new() { ["name"] = "Ping" } }, Cancel)), ZabbixPageView.Default),
            ("service", First(await api.Services.GetAsync(new ZabbixServiceGetParameters { ServiceIds = [fixtures.Service] }, Cancel)), ZabbixPageView.Default),
            ("service edit", First(await api.Services.GetAsync(new ZabbixServiceGetParameters { ServiceIds = [fixtures.Service] }, Cancel)), ZabbixPageView.Edit),
            ("sla", First(await api.Slas.GetAsync(new ZabbixSlaGetParameters { SlaIds = [fixtures.Sla] }, Cancel)), ZabbixPageView.Default),
            ("proxy", First(await api.Proxies.GetAsync(new ZabbixProxyGetParameters { ProxyIds = [fixtures.Proxy] }, Cancel)), ZabbixPageView.Default),
        };
        // The stock host's own trigger events, when the lab has raised one (the one trigger no test deletes).
        if ((await api.Events.GetAsync(new ZabbixEventGetParameters { HostIds = [server.Id], Source = ZabbixEventSource.Triggers, ObjectType = ZabbixEventObject.Trigger, Limit = 1 }, Cancel)) is [ZabbixEvent @event, ..])
            pages.Add(("event", @event, ZabbixPageView.Default));
        if ((await api.Problems.GetAsync(new ZabbixProblemGetParameters { HostIds = [server.Id], Source = ZabbixEventSource.Triggers, ObjectType = ZabbixEventObject.Trigger, Limit = 1 }, Cancel)) is [ZabbixProblem problem, ..])
            pages.Add(("problem", problem, ZabbixPageView.Default));

        var refused = new List<string>();
        foreach ((string what, ZabbixPageTarget target, ZabbixPageView view) in pages)
        {
            Uri page = frontend.UriFor(target, view);
            string title = await TitleAsync(browser, page);
            TestContext.Current.TestOutputHelper?.WriteLine($"{version} {what}: {page.PathAndQuery} -> {title}");
            if (title.Length == 0 || title.Contains("Warning", StringComparison.Ordinal) || title.Contains("Fatal", StringComparison.Ordinal))
                refused.Add($"{what}: {page.PathAndQuery} -> '{title}'");
        }
        Assert.Empty(refused);

        // The controls: a host that does not exist is refused, so a page that is not refused was found; and from 7.4 on,
        // where the item form is addressed directly, a template's item in the host context is refused, so the context
        // the conversion read from selectHosts is the one the form checks.
        string bogus = await TitleAsync(browser, frontend.UriFor(new ZabbixPageTarget(ZabbixPageKind.Host, 999999999UL)));
        Assert.Contains("Warning", bogus, StringComparison.Ordinal);
        if (frontend.Version >= new Version(7, 4))
        {
            ZabbixPageTarget templateItem = pages.Single(page => page.What == "item on a template").Target;
            Assert.Equal(ZabbixPageContext.Template, templateItem.Context);
            string misplaced = await TitleAsync(browser, frontend.UriFor(templateItem with { Context = ZabbixPageContext.Host }));
            Assert.Contains("Warning", misplaced, StringComparison.Ordinal);
        }
    }

    private static ZabbixPageTarget First<T>(IReadOnlyList<T> found)
        => found is [var first, ..] && ZabbixPageTarget.TryFrom(first, out ZabbixPageTarget? target)
            ? target
            : throw new InvalidOperationException($"the lab has no {typeof(T).Name}");


    private static async Task SignInAsync(HttpClient browser, ZabbixFrontend frontend)
    {
        // The lab's frontend user (tests/lab/lab.sh signs in as it to mint the API token); the frontend has no token login.
        using var form = new FormUrlEncodedContent([new("name", "Admin"), new("password", "zabbix"), new("autologin", "0"), new("enter", "Sign in")]);
        // The lab names this server, so a frontend that does not answer or refuses the sign-in is a failure, not a skip:
        // a test that cannot reach what it was told is there has found something.
        HttpResponseMessage signedIn;
        try
        {
            signedIn = await browser.PostAsync(new Uri(frontend.BaseUri, "index.php"), form, Cancel);
        }
        catch (HttpRequestException unreachable)
        {
            Assert.Fail($"the frontend at {frontend.BaseUri} does not answer over HTTP ({unreachable.Message})");
            throw;
        }
        using (signedIn)
        {
            Assert.True(signedIn.StatusCode == HttpStatusCode.Found,
                $"the frontend at {frontend.BaseUri} refused the lab's Admin sign-in ({(int)signedIn.StatusCode}); the page links cannot be opened");
        }
    }

    private static async Task<string> TitleAsync(HttpClient browser, Uri page)
    {
        using HttpResponseMessage response = await browser.GetAsync(page, Cancel);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync(Cancel);
        return Title().Match(body) is { Success: true } title ? WebUtility.HtmlDecode(title.Groups[1].Value) : string.Empty;
    }

    [GeneratedRegex("<title>([^<]*)</title>")]
    private static partial Regex Title();

    private sealed record Fixtures(ulong Group, ulong Host, ulong WebScenario, ulong Maintenance, ulong Service, ulong Sla, ulong Proxy);

    // What the lab does not hold on every version: a web scenario, a maintenance, a service, an SLA (on a host of the scenario's own), a proxy.
    private static async Task<Fixtures> CreateFixturesAsync(LiveScope scope)
    {
        string group = (await scope.Server.RpcAsync("hostgroup.create", new { name = scope.Name("group") })).Id("groupids");
        scope.Delete("hostgroup", group);
        string host = (await scope.Server.RpcAsync("host.create", new { host = scope.Name("host"), groups = new[] { new { groupid = group } } })).Id("hostids");
        scope.Delete("host", host);
        string scenario = (await scope.Server.RpcAsync("httptest.create", new
        {
            name = scope.Name("web"),
            hostid = host,
            steps = new[] { new { name = "home", url = "http://localhost/", no = 1 } },
        })).Id("httptestids");
        long now = TimeProvider.System.GetUtcNow().ToUnixTimeSeconds();
        string maintenance = (await scope.Server.RpcAsync("maintenance.create", new
        {
            name = scope.Name("maintenance"),
            active_since = now,
            active_till = now + 3600,
            groups = new[] { new { groupid = group } },
            timeperiods = new[] { new { timeperiod_type = 0, period = 3600 } },
        })).Id("maintenanceids");
        scope.Delete("maintenance", maintenance);
        string service = (await scope.Server.RpcAsync("service.create", new { name = scope.Name("service"), algorithm = 0, sortorder = 0 })).Id("serviceids");
        scope.Delete("service", service);
        string sla = (await scope.Server.RpcAsync("sla.create", new
        {
            name = scope.Name("sla"),
            slo = "99.9",
            period = 0,
            timezone = "UTC",
            effective_date = now,
            service_tags = new[] { new { tag = scope.Name("tag"), @operator = 0, value = "" } },
        })).Id("slaids");
        scope.Delete("sla", sla);
        string proxy = (await scope.Server.RpcAsync("proxy.create", new { name = scope.Name("proxy"), operating_mode = 0 })).Id("proxyids");
        scope.Delete("proxy", proxy);
        return new Fixtures(Parsed(group), Parsed(host), Parsed(scenario), Parsed(maintenance), Parsed(service), Parsed(sla), Parsed(proxy));

        static ulong Parsed(string id) => ulong.Parse(id, System.Globalization.CultureInfo.InvariantCulture);
    }
}
