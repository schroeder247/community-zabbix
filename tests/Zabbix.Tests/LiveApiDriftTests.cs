// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;

namespace Community.Zabbix.Tests;

/// <summary>
/// The live drift run: every generated <c>.get</c>, with <c>output: extend</c> and every current <c>select*</c>,
/// against each server in <c>ZABBIX_TEST_APIS</c>. The oracle is that every reply reads into its record (no field typed
/// wrong), and the report lists the fields the description does not model, which land in <c>AdditionalFields</c>
/// instead of being dropped.
/// </summary>
public sealed class LiveApiDriftTests(ITestOutputHelper output)
{
    // Methods that cannot be called bare: they need an id or a role the probe does not have (the first two are read with
    // their required input by The_gets_with_a_required_option_read_into_their_records), or a time window and the APM
    // database the lab has none of (The_APM_gets_take_their_window_and_ask_for_the_APM_database).
    private static readonly HashSet<string> NeedsInput = new(StringComparer.Ordinal)
    {
        "HostDashboards", "ServerInfo", "Devices", "ApmLogs", "ApmMetrics", "ApmSpans", "ApmTraces",
    };

    /// <summary>The live matrix's servers (<c>ZABBIX_TEST_APIS</c>, <see cref="LiveMatrix"/>).</summary>
    private static IReadOnlyList<(Uri Endpoint, string Token)> Targets { get; } = [.. LiveMatrix.Servers.Select(s => (s.Endpoint, s.Token))];

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task The_gets_with_a_required_option_read_into_their_records(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "drift-required");
        JsonElement hosts = await scope.Server.RpcAsync("host.get", new { output = new[] { "hostid" }, filter = new { host = new[] { "Zabbix server" } } });
        ulong host = ulong.Parse(hosts[0].GetProperty("hostid").GetString()!, CultureInfo.InvariantCulture);

        IReadOnlyList<ZabbixHostDashboard> dashboards = await scope.Server.HostDashboards.GetAsync(new ZabbixHostDashboardGetParameters { HostIds = [host], Output = ZabbixOutput.Extend }, TestContext.Current.CancellationToken);

        Assert.All(dashboards, dashboard => Assert.Equal(host, dashboard.HostId));
        if (version != "8.0") return;
        string serverId = (await scope.Server.RpcAsync("settings.get", new { output = new[] { "serverid" } })).GetProperty("serverid").GetString()!;
        ZabbixServerInfoRecord info = await scope.Server.ServerInfo.GetAsync(new ZabbixServerInfoGetParameters { ServerId = serverId, Output = ZabbixOutput.Extend }, TestContext.Current.CancellationToken);
        Assert.Equal(serverId, info.ServerId);
        Assert.True(info.LastAccess?.ToUnixTimeSeconds() > 0);
    }

    /// <summary>
    /// The APM gets (8.0) read a ClickHouse database the frontend is configured with (<c>settings.apm_global_db</c>); the
    /// lab has none, so each answers that the APM database is not configured. That answer comes after the input
    /// validation (<c>CApmLog::get</c>, 8.0:66-110), so the typed parameters, the required window included, are what the
    /// server accepts.
    /// </summary>
    /// <param name="version">The lab server.</param>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task The_APM_gets_take_their_window_and_ask_for_the_APM_database(string version)
    {
        if (version != "8.0") Assert.Skip("the APM objects are 8.0's");
        await using LiveScope scope = LiveScope.Open(version, "drift-apm");
        ZabbixClock till = DateTimeOffset.UtcNow, from = new ZabbixClock(till.Seconds - 3600, 0);
        CancellationToken ct = TestContext.Current.CancellationToken;
        ZabbixApi api = scope.Server.Api;
        Func<Task>[] gets =
        [
            () => api.ApmLogs.GetAsync(new ZabbixApmLogGetParameters { TimeFrom = from.ToDateTimeOffset(), TimeTo = till.ToDateTimeOffset(), SeverityNumbers = [9], Limit = 5 }, ct),
            () => api.ApmMetrics.GetAsync(new ZabbixApmMetricGetParameters { TimeFrom = from.ToDateTimeOffset(), TimeTo = till.ToDateTimeOffset(), Types = [ZabbixApmMetricType.Gauge], Limit = 5 }, ct),
            () => api.ApmSpans.GetAsync(new ZabbixApmSpanGetParameters { TimeFrom = from.ToDateTimeOffset(), TimeTo = till.ToDateTimeOffset(), MinDuration = 1, Limit = 5 }, ct),
            () => api.ApmTraces.GetAsync(new ZabbixApmTraceGetParameters { TimeFrom = from.ToDateTimeOffset(), TimeTo = till.ToDateTimeOffset(), Limit = 5 }, ct),
        ];

        foreach (Func<Task> get in gets)
        {
            ZabbixApiException refused = await Assert.ThrowsAsync<ZabbixApiException>(get);
            Assert.Contains("APM DB is not configured", refused.Message + " " + refused.Detail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Every_get_on_every_live_server_reads_into_its_record()
    {
        if (Targets.Count == 0) Assert.Skip("ZABBIX_TEST_APIS is not set; set it to url|token pairs (e.g. http://localhost:18070/api_jsonrpc.php|<token>;http://localhost:18080/api_jsonrpc.php|<token>) to run the drift run");
        var failures = new List<string>();
        foreach ((Uri endpoint, string token) in Targets)
        {
            using var client = new ZabbixClient(endpoint, new ZabbixApiTokenCredential(token));
            Version version = await client.GetApiVersionAsync(TestContext.Current.CancellationToken);
            foreach (PropertyInfo property in typeof(ZabbixApi).GetProperties().Where(p => p.Name != nameof(ZabbixApi.Transport)))
            {
                object apiClient = property.GetValue(client.Api)!;
                // The overload that takes the parameters and a token: the one every other overload calls.
                MethodInfo? get = apiClient.GetType().GetMethods().FirstOrDefault(m => m.Name == "GetAsync"
                    && m.GetParameters() is [{ ParameterType: var parameters }, { ParameterType: var token }]
                    && token == typeof(CancellationToken) && parameters != typeof(CancellationToken) && !typeof(Delegate).IsAssignableFrom(parameters));
                if (get is null || NeedsInput.Contains(property.Name)) continue;
                string method = apiClient.GetType().Name["Zabbix".Length..^"Client".Length].ToUpperInvariant();
                if (get.GetCustomAttribute<ZabbixSupportedAttribute>() is { } since && version < Version.Parse(since.Version)) continue;
                Type parametersType = get.GetParameters()[0].ParameterType;
                object parameters = Activator.CreateInstance(parametersType)!;
                foreach (PropertyInfo option in parametersType.GetProperties())
                {
                    bool current = option.GetCustomAttribute<ObsoleteAttribute>() is null
                        && (option.GetCustomAttribute<ZabbixSupportedAttribute>() is not { } s || version >= Version.Parse(s.Version))
                        && (option.GetCustomAttribute<ZabbixUnsupportedAttribute>() is not { } r || version < Version.Parse(r.Version));
                    if (Extend(option.PropertyType) is { } extend && (option.Name == "Output" || (current && option.Name.StartsWith("Select", StringComparison.Ordinal))))
                        option.SetValue(parameters, extend);
                    if (option.Name == "Limit") option.SetValue(parameters, 5);
                }
                // Typed, not by name: a renamed member breaks the build here instead of this test on a live server.
                if (parameters is ZabbixHistoryGetParameters history) history.ValueType = ZabbixItemValueType.Float;
                if (parameters is ZabbixServiceGetParameters service) service.SelectStatusTimeline = null;
                try
                {
                    var task = (Task)get.Invoke(apiClient, [parameters, CancellationToken.None])!;
                    await task;
                    object result = task.GetType().GetProperty("Result")!.GetValue(task)!;
                    foreach (object row in result is IEnumerable rows and not string ? rows.Cast<object>() : [result])
                    {
                        if ((row as IZabbixAdditionalFields)?.AdditionalFields is { } extra && extra.Count > 0)
                            output.WriteLine($"{version} {method}: unmodelled {string.Join(", ", extra.Keys)}");
                    }
                }
                catch (JsonException error)
                {
                    failures.Add($"{version} {property.Name}: {error.Message}");
                }
                catch (ZabbixApiException error)
                {
                    failures.Add($"{version} {property.Name}: refused: {error.Message} {error.Detail}");
                }
            }
        }
        foreach (string failure in failures) output.WriteLine(failure);
        Assert.Empty(failures);
    }

    // ZabbixOutput.Extend as the output of the option's own record, what the keyword converts to; null for another type.
    private static object? Extend(Type option)
        => option.IsGenericType && option.GetGenericTypeDefinition() == typeof(ZabbixOutput<>)
            ? option.GetMethod(nameof(ZabbixOutput<object>.FromZabbixOutput))!.Invoke(null, [ZabbixOutput.Extend])
            : null;

    /// <summary>
    /// The model's shapes that the server's source defines, round-tripped on every live
    /// server: a record is created from the typed requests alone (no <c>= null!</c>, no <c>AdditionalFields</c>), read back
    /// typed, updated and deleted. It holds: a trapper and a dependent item need only their own fields; a map
    /// element with icon and position; map.update takes shapes; an e-mail media's sendto reads back as a
    /// list, an SMS one's as one text; a daily period's every is an interval; a media type left without a
    /// status is created disabled; typed SNMPv3 interface details and graph items. Objects are prefixed
    /// <c>zbx-test-zabbix-source-</c> and deleted by id in <c>finally</c>.
    /// </summary>
    [Fact]
    public async Task Typed_requests_create_read_update_and_delete_on_every_live_server()
    {
        if (Targets.Count == 0) Assert.Skip("ZABBIX_TEST_APIS is not set; set it to url|token pairs to run the round trips against live servers");
        CancellationToken ct = TestContext.Current.CancellationToken;
        foreach ((Uri endpoint, string token) in Targets)
        {
            using var client = new ZabbixClient(endpoint, new ZabbixApiTokenCredential(token));
            Version version = await client.GetApiVersionAsync(ct);
            string prefix = "zbx-test-zabbix-source-" + Guid.NewGuid().ToString("N")[..8];
            var cleanup = new List<Func<Task>>();
            try
            {
                // host group, template group, template
                IReadOnlyList<ulong> groups = await client.HostGroups.CreateAsync([new ZabbixHostGroupCreateRequest { Name = prefix + "-hg" }], ct);
                cleanup.Add(() => client.HostGroups.DeleteAsync(groups, ct));
                IReadOnlyList<ulong> templateGroups = await client.TemplateGroups.CreateAsync([new ZabbixTemplateGroupCreateRequest { Name = prefix + "-tg" }], ct);
                cleanup.Add(() => client.TemplateGroups.DeleteAsync(templateGroups, ct));
                IReadOnlyList<ulong> templates = [await client.Templates.CreateAsync(new ZabbixTemplateCreateRequest { HostName = prefix + "-tpl", Groups = [new ZabbixTemplateGroupRequest { GroupId = templateGroups[0] }] }, ct)];
                cleanup.Add(() => client.Templates.DeleteAsync(templates, ct));

                // a host with an agent and an SNMPv3 interface, a tag, a macro and a PSK
                IReadOnlyList<ulong> hosts = [await client.Hosts.CreateAsync(new ZabbixHostCreateRequest
                {
                    HostName = prefix + "-host",
                    Groups = [new ZabbixHostGroupRequest { GroupId = groups[0] }],
                    Tags = [new ZabbixTagRequest { Tag = "zbx", Value = "crud" }],
                    Macros = [new ZabbixHostMacroRequest { Macro = "{$ZBX}", Value = "1" }],
                    TlsConnect = ZabbixTlsConnectMode.Psk,
                    TlsAccept = ZabbixTlsAcceptModes.Psk,
                    TlsPskIdentity = prefix,
                    TlsPsk = "0123456789abcdef0123456789abcdef",
                    Interfaces =
                    [
                        new ZabbixHostInterfaceRequest { Type = ZabbixInterfaceType.Agent, Main = ZabbixInterfacePriority.Primary, UseIP = ZabbixHostInterfaceUseIP.IP, IP = "127.0.0.1", Dns = "", Port = 10050 },
                        new ZabbixHostInterfaceRequest
                        {
                            Type = ZabbixInterfaceType.Snmp, Main = ZabbixInterfacePriority.Primary, UseIP = ZabbixHostInterfaceUseIP.IP, IP = "127.0.0.1", Dns = "", Port = 161,
                            Details = new ZabbixHostInterfaceDetailRequest
                            {
                                Version = ZabbixSnmpVersion.V3, Bulk = true, SecurityName = "zbx", SecurityLevel = ZabbixSnmpSecurityLevel.AuthenticationAndPrivacy,
                                AuthenticationProtocol = ZabbixSnmpAuthenticationProtocol.Sha256, AuthenticationPassphrase = "zbx-auth-pass", PrivacyProtocol = ZabbixSnmpPrivacyProtocol.Aes128, PrivacyPassphrase = "zbx-priv-pass",
                            },
                        },
                    ],
                }, ct)];
                cleanup.Add(() => client.Hosts.DeleteAsync(hosts, ct));
                ZabbixHost host = Assert.Single(await client.Hosts.GetAsync(new ZabbixHostGetParameters { HostIds = hosts, SelectInterfaces = ZabbixOutput.Extend, SelectTags = ZabbixOutput.Extend, SelectMacros = ZabbixOutput.Extend }, ct));
                ZabbixHostInterfaceDetail snmp = host.Interfaces!.Single(i => i.Type == ZabbixInterfaceType.Snmp).Details!;
                Assert.Equal(ZabbixSnmpVersion.V3, snmp.Version);
                Assert.Equal(ZabbixSnmpSecurityLevel.AuthenticationAndPrivacy, snmp.SecurityLevel);
                Assert.Equal(ZabbixSnmpAuthenticationProtocol.Sha256, snmp.AuthenticationProtocol);
                Assert.Equal("crud", Assert.Single(host.Tags!).Value);
                Assert.DoesNotContain("0123456789abcdef", host.ToString(), StringComparison.Ordinal);

                // A trapper item and a dependent item from their own fields only
                IReadOnlyList<ulong> trapper = [await client.Items.CreateAsync(new ZabbixItemCreateRequest { HostId = hosts[0], Name = "zbx trapper", Type = ZabbixItemType.Trapper, Key = ZabbixItemKey.Parse("zbxtest.trap"), ValueType = ZabbixItemValueType.Float }, ct)];
                IReadOnlyList<ulong> dependent = [await client.Items.CreateAsync(new ZabbixItemCreateRequest { HostId = hosts[0], Name = "zbx dependent", Type = ZabbixItemType.Dependent, Key = ZabbixItemKey.Parse("zbxtest.dep"), ValueType = ZabbixItemValueType.Float, MasterItemId = trapper[0] }, ct)];
                Assert.Equal(ZabbixItemType.Dependent, Assert.Single(await client.Items.GetAsync(new ZabbixItemGetParameters { ItemIds = dependent }, ct)).Type);

                // history.push answers one typed row per value (the item may not be in the server's configuration
                // cache yet, so the first row is either this item or the refusal every unknown item gets)
                ZabbixHistoryPushResult pushed = await client.History.PushAsync(
                    [new ZabbixHistoryPushRequest { ItemId = trapper[0], Value = JsonSerializer.SerializeToElement(1.5) }, new ZabbixHistoryPushRequest { HostName = prefix + "-nohost", Key = ZabbixItemKey.Parse("zbxtest.none"), Value = JsonSerializer.SerializeToElement(1) }], ct);
                Assert.Equal("success", pushed.Response);
                Assert.Equal(2, pushed.Values!.Count);
                Assert.True(pushed.Values[0].ItemId is null || pushed.Values[0].ItemId == trapper[0], pushed.Values[0].ToString());
                Assert.Null(pushed.Values[1].ItemId);
                Assert.NotNull(pushed.Values[1].Error);

                // a trigger, and a graph with its items
                IReadOnlyList<ulong> triggers = [await client.Triggers.CreateAsync(new ZabbixTriggerCreateRequest { Name = "zbx trigger", Expression = $"last(/{prefix}-host/zbxtest.trap)>0" }, ct)];
                Assert.Single(triggers);
                IReadOnlyList<ulong> graphs = [await client.Graphs.CreateAsync(new ZabbixGraphCreateRequest { Name = prefix + "-graph", Width = 900, Height = 200, GraphItems = [new ZabbixGraphItemRequest { ItemId = trapper[0], Color = "1A7C11" }] }, ct)];
                ZabbixGraph graph = Assert.Single(await client.Graphs.GetAsync(new ZabbixGraphGetParameters { GraphIds = graphs, SelectGraphItems = ZabbixOutput.Extend }, ct));
                Assert.Equal(trapper[0], Assert.Single(graph.GraphItems!).ItemId);
                await client.Graphs.UpdateAsync(new ZabbixGraphUpdateRequest { GraphId = graphs[0], Name = prefix + "-graph2" }, ct);
                Assert.Equal(prefix + "-graph2", Assert.Single(await client.Graphs.GetAsync(new ZabbixGraphGetParameters { GraphIds = graphs }, ct)).Name);

                // A daily period every second day
                ZabbixClock now = new(DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 0);
                IReadOnlyList<ulong> maintenances = [await client.Maintenances.CreateAsync(new ZabbixMaintenanceCreateRequest
                {
                    Name = prefix + "-mnt",
                    ActiveSince = now.ToDateTimeOffset(),
                    ActiveTill = DateTimeOffset.FromUnixTimeSeconds(now.Seconds + 86_400),
                    Hosts = [new ZabbixMaintenanceHostRequest { HostId = hosts[0] }],
                    TimePeriods = [new ZabbixMaintenanceTimePeriodRequest { TimePeriodType = ZabbixTimePeriodType.Daily, Every = 2, StartTime = new TimeOnly(1, 0), Period = ZabbixDuration.Parse("3600") }],
                }, ct)];
                cleanup.Add(() => client.Maintenances.DeleteAsync(maintenances, ct));
                ZabbixMaintenanceRecord maintenance = Assert.Single(await client.Maintenances.GetAsync(new ZabbixMaintenanceGetParameters { MaintenanceIds = maintenances, SelectTimePeriods = ZabbixOutput.Extend }, ct));
                Assert.Equal(2, Assert.Single(maintenance.TimePeriods!).Every);

                // Media types left without a status, a user with an e-mail and an SMS media
                IReadOnlyList<ulong> email = [await client.MediaTypes.CreateAsync(new ZabbixMediaTypeCreateRequest { Name = prefix + "-mail", Type = ZabbixMediaTypeKind.Email, SmtpServer = "localhost", SmtpHelo = "localhost", SmtpEmail = "zbx@localhost" }, ct)];
                cleanup.Add(() => client.MediaTypes.DeleteAsync(email, ct));
                IReadOnlyList<ulong> sms = [await client.MediaTypes.CreateAsync(new ZabbixMediaTypeCreateRequest { Name = prefix + "-sms", Type = ZabbixMediaTypeKind.Sms, GsmModem = "/dev/ttyS0" }, ct)];
                cleanup.Add(() => client.MediaTypes.DeleteAsync(sms, ct));
                Assert.Equal(ZabbixMediaTypeStatus.Disabled, Assert.Single(await client.MediaTypes.GetAsync(new ZabbixMediaTypeGetParameters { MediaTypeIds = email }, ct)).Status);
                await client.MediaTypes.UpdateAsync(new ZabbixMediaTypeUpdateRequest { MediaTypeId = email[0], Status = ZabbixMediaTypeStatus.Active }, ct);
                Assert.Equal(ZabbixMediaTypeStatus.Active, Assert.Single(await client.MediaTypes.GetAsync(new ZabbixMediaTypeGetParameters { MediaTypeIds = email }, ct)).Status);
                IReadOnlyList<ulong> userGroups = [await client.UserGroups.CreateAsync(new ZabbixUserGroupCreateRequest { Name = prefix + "-ug" }, ct)];
                cleanup.Add(() => client.UserGroups.DeleteAsync(userGroups, ct));
                IReadOnlyList<ulong> users = [await client.Users.CreateAsync(new ZabbixUserCreateRequest
                {
                    UserName = prefix + "-user",
                    Password = "Zbx-test-" + Guid.NewGuid().ToString("N")[..12] + "!",
                    RoleId = 1,
                    UserGroups = [new ZabbixUserGroupRequest { UserGroupId = userGroups[0] }],
                    Medias = [new ZabbixUserMediaRequest { MediaTypeId = email[0], SendTo = ["a@zbx.test", "b@zbx.test"] }, new ZabbixUserMediaRequest { MediaTypeId = sms[0], SendTo = ["+15550100"] }],
                }, ct)];
                cleanup.Add(() => client.Users.DeleteAsync(users, ct));
                ZabbixUser user = Assert.Single(await client.Users.GetAsync(new ZabbixUserGetParameters { UserIds = users, SelectMedias = ZabbixOutput.Extend }, ct));
                Assert.Equal(["a@zbx.test", "b@zbx.test"], user.Medias!.Single(m => m.MediaTypeId == email[0]).SendTo);
                Assert.Equal(["+15550100"], user.Medias!.Single(m => m.MediaTypeId == sms[0]).SendTo);

                // A map with an image element, a shape and a url; its update adds a shape and keeps the first
                ulong icon = Assert.Single(await client.Images.GetAsync(new ZabbixImageGetParameters { Output = [ZabbixImageFields.ImageId], Filter = new() { ["imagetype"] = new(JsonSerializer.SerializeToElement(1)) }, Limit = 1 }, ct)).ImageId!.Value;
                IReadOnlyList<ulong> maps = [await client.Maps.CreateAsync(new ZabbixMapCreateRequest
                {
                    Name = prefix + "-map",
                    Width = 800,
                    Height = 600,
                    Elements = [new ZabbixMapElementRequest { ElementType = ZabbixMapElementType.Image, IconIdOff = icon, X = 10, Y = 20, Label = "zbx" }],
                    Shapes = [new ZabbixMapShapeRequest { Type = ZabbixMapShapeType.Rectangle, X = 1, Y = 1, Width = 50, Height = 50, Text = "one" }],
                    Urls = [new ZabbixMapUrlRequest { Name = "zbx", Url = "https://zbx.test/", ElementType = 0 }],
                }, ct)];
                cleanup.Add(() => client.Maps.DeleteAsync(maps, ct));
                ZabbixMap map = Assert.Single(await client.Maps.GetAsync(new ZabbixMapGetParameters { MapIds = maps, SelectElements = ZabbixOutput.Extend, SelectShapes = ZabbixOutput.Extend, SelectUrls = ZabbixOutput.Extend }, ct));
                ZabbixMapElement element = Assert.Single(map.Elements!);
                Assert.Equal(((ulong?)icon, 10, 20), (element.IconIdOff, element.X, element.Y));
                Assert.Equal("https://zbx.test/", Assert.Single(map.Urls!).Url);
                ulong first = Assert.Single(map.Shapes!).ShapeId!.Value;
                await client.Maps.UpdateAsync(new ZabbixMapUpdateRequest
                {
                    MapId = maps[0],
                    Shapes = [new ZabbixMapUpdateShapeRequest { ShapeId = first, Text = "one" }, new ZabbixMapUpdateShapeRequest { Type = ZabbixMapShapeType.Ellipse, X = 100, Y = 100, Width = 40, Height = 40, Text = "two" }],
                }, ct);
                map = Assert.Single(await client.Maps.GetAsync(new ZabbixMapGetParameters { MapIds = maps, SelectShapes = ZabbixOutput.Extend }, ct));
                Assert.Equal(["one", "two"], map.Shapes!.Select(shape => shape.Text!).Order(StringComparer.Ordinal));

                // a dashboard whose widget reads back typed
                IReadOnlyList<ulong> dashboards = [await client.Dashboards.CreateAsync(new ZabbixDashboardCreateRequest
                {
                    Name = prefix + "-dash",
                    Pages = [new ZabbixDashboardPageRequest { Widgets = [new ZabbixDashboardPageWidgetRequest { Type = "clock", X = 0, Y = 0, Width = 12, Height = 3 }] }],
                }, ct)];
                cleanup.Add(() => client.Dashboards.DeleteAsync(dashboards, ct));
                ZabbixDashboard dashboard = Assert.Single(await client.Dashboards.GetAsync(new ZabbixDashboardGetParameters { DashboardIds = dashboards, SelectPages = ZabbixOutput.Extend }, ct));
                Assert.Equal("clock", Assert.Single(Assert.Single(dashboard.Pages!).Widgets!).Type);
                output.WriteLine($"{version}: round trips verified");
            }
            finally
            {
                for (int i = cleanup.Count - 1; i >= 0; i--)
                {
                    try { await cleanup[i](); }
                    catch (ZabbixApiException error) { output.WriteLine($"{version}: cleanup {i}: {error.Message} {error.Detail}"); }
                }
            }
        }
    }

    [Fact]
    public async Task An_unmodelled_field_sent_through_AdditionalFields_reaches_host_create_on_8_0()
    {
        // host.create accepts an "inventory" field (CHost.php: $hosts[]['inventory'], applied when inventory_mode is
        // not disabled) that ZabbixHostCreateRequest does not model. Sent through AdditionalFields (the same hatch
        // the PowerShell -Properties hashtable and ZabbixPropertyOverrides route an unmodelled key into), it must
        // reach the server and be applied — proof the bag is not merely accepted locally but actually sent.
        List<(Uri Endpoint, string Token)> eightZero = [.. LiveMatrix.Servers.Where(s => s.Label == "8.0").Select(s => (s.Endpoint, s.Token))];
        if (eightZero.Count == 0) Assert.Skip("ZABBIX_TEST_APIS does not include the 8.0 server");
        using var client = new ZabbixClient(eightZero[0].Endpoint, new ZabbixApiTokenCredential(eightZero[0].Token));
        string name = "zbx-test-zabbix-source-reqext-" + Guid.NewGuid().ToString("N")[..8];
        IReadOnlyList<ulong> groups = await client.HostGroups.CreateAsync([new ZabbixHostGroupCreateRequest { Name = name + "-grp" }], TestContext.Current.CancellationToken);
        IReadOnlyList<ulong> hosts = [];
        try
        {
            var request = new ZabbixHostCreateRequest
            {
                HostName = name,
                Groups = [new ZabbixHostGroupRequest { GroupId = groups[0] }],
                InventoryMode = ZabbixHostInventoryMode.Manual,
                AdditionalFields = new Dictionary<string, JsonElement> { ["inventory"] = JsonSerializer.SerializeToElement(new { alias = "reqext-live" }) },
            };
            hosts = [await client.Hosts.CreateAsync(request, TestContext.Current.CancellationToken)];

            IReadOnlyList<ZabbixHost> read = await client.Hosts.GetAsync(new ZabbixHostGetParameters { HostIds = hosts, SelectInventory = ZabbixOutput.Extend }, TestContext.Current.CancellationToken);

            Assert.Equal("reqext-live", Assert.Single(read).Inventory!.Alias);
        }
        finally
        {
            if (hosts.Count > 0) await client.Hosts.DeleteAsync(hosts, TestContext.Current.CancellationToken);
            await client.HostGroups.DeleteAsync(groups, TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task The_mass_updates_are_sent_or_composed_by_the_server_s_version_and_change_the_same_state()
    {
        if (Targets.Count == 0) Assert.Skip("ZABBIX_TEST_APIS is not set; set it to url|token pairs to run the mass-update check against live servers");
        foreach ((Uri endpoint, string token) in Targets)
        {
            using var client = new ZabbixClient(endpoint, new ZabbixApiTokenCredential(token));
            Version version = await client.GetApiVersionAsync(TestContext.Current.CancellationToken);
            string prefix = "zbx-test-zabbix-source-mass-" + Guid.NewGuid().ToString("N")[..8];
            IReadOnlyList<ulong> groups = await client.HostGroups.CreateAsync([new ZabbixHostGroupCreateRequest { Name = prefix + "-a" }, new ZabbixHostGroupCreateRequest { Name = prefix + "-b" }], TestContext.Current.CancellationToken);
            IReadOnlyList<ulong> hosts = [];
            try
            {
                hosts = await client.Hosts.CreateAsync([
                    new ZabbixHostCreateRequest { HostName = prefix + "-1", Groups = [new ZabbixHostGroupRequest { GroupId = groups[0] }] },
                    new ZabbixHostCreateRequest { HostName = prefix + "-2", Groups = [new ZabbixHostGroupRequest { GroupId = groups[0] }] }], TestContext.Current.CancellationToken);

                await client.Hosts.MassUpdateAsync(new ZabbixHostMassUpdateRequest { Hosts = [.. hosts.Select(h => new ZabbixHostMassHostRequest { HostId = h })], Status = ZabbixHostStatus.Disabled }, TestContext.Current.CancellationToken);
                await client.HostGroups.MassUpdateAsync(new ZabbixHostGroupMassUpdateRequest { Groups = [new ZabbixHostGroupMassGroupRequest { GroupId = groups[1] }], Hosts = [new ZabbixHostGroupMassHostRequest { HostId = hosts[0] }] }, TestContext.Current.CancellationToken);

                IReadOnlyList<ZabbixHost> read = await client.Hosts.GetAsync(new ZabbixHostGetParameters { HostIds = hosts, Output = [ZabbixHostFields.HostId, ZabbixHostFields.Status], SelectHostGroups = [ZabbixHostGroupFields.GroupId] }, TestContext.Current.CancellationToken);
                Assert.All(read, h => Assert.Equal(ZabbixHostStatus.Disabled, h.Status));
                Assert.Equal([groups[0], groups[1]], read.Single(h => h.HostId == hosts[0]).HostGroups!.Select(g => g.GroupId!.Value).Order());
                Assert.Equal([groups[0]], read.Single(h => h.HostId == hosts[1]).HostGroups!.Select(g => g.GroupId!.Value));
                output.WriteLine($"{version}: mass updates {(ZabbixApiMethods.Supports(ZabbixApiMethod.HostMassUpdate, version) ? "sent" : "composed")}, state verified");
            }
            finally
            {
                if (hosts.Count > 0) await client.Hosts.DeleteAsync(hosts, TestContext.Current.CancellationToken);
                await client.HostGroups.DeleteAsync(groups, TestContext.Current.CancellationToken);
            }
        }
    }
}
