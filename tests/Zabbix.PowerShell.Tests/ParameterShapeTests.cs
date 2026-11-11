// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Management.Automation;
using System.Reflection;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>The small parameter shapes that existing scripts rely on: positions, pipeline binding and name forms.</summary>
public sealed class ParameterShapeTests
{
    [Fact]
    public void New_ZbxMaintenanceTimePeriod_takes_its_type_by_position()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("New-ZbxMaintenanceTimePeriod OneTime -Period 3600");

        Assert.Empty(run.Errors);
        var period = Assert.IsType<Hashtable>(Assert.Single(run.Output).BaseObject);
        // Builders key by wire name and store the wire's number: a raw-API body built from the CLR member name would
        // send "Onetime", which the server refuses.
        Assert.Equal((long)ZabbixTimePeriodType.OneTime, period["timeperiod_type"]);
    }

    [Fact]
    public async Task Get_ZbxTrend_without_ItemId_asks_for_every_items_trends()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("trend.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxTrend -Limit 5");

        Assert.Empty(run.Errors);
        Assert.Equal("""{"limit":5}""", Assert.Single(server.ParamsOf("trend.get")).GetRawText());
    }

    [Fact]
    public async Task Publish_ZbxDashboard_takes_a_piped_hashtable_through_the_requests_own_contract()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("dashboard.get", "[]");
        server.On("dashboard.create", """{"dashboardids":["4"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("@{ name = 'next-board'; display_period = 30; pages = @(@{ widgets = @() }) } | Publish-ZbxDashboard -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("dashboard.create"));
        Assert.Equal("next-board", request.GetProperty("name").GetString());
        Assert.Equal(30, request.GetProperty("display_period").GetInt32());
    }

    [Theory]
    [InlineData("-ManualClose", "1")]
    [InlineData("-ManualClose:$false", "0")]
    [InlineData("", null)]
    public async Task New_ZbxTrigger_ManualClose_is_a_switch(string argument, string? wire)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("trigger.create", """{"triggerids":["8"]}""");
        server.On("trigger.get", """[{"triggerid":"8","description":"t"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke($"New-ZbxTrigger -Name t -Expression 'last(/h/k)=0' {argument} -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("trigger.create"));
        if (wire is null) Assert.False(request.TryGetProperty("manual_close", out _));
        else Assert.Equal(wire, request.GetProperty("manual_close").GetRawText());
    }

    [Fact]
    public async Task New_ZbxTrigger_Name_is_the_trigger_s_name_and_Description_its_comments()
    {
        // The API calls a trigger's name description and its description comments (CTriggerGeneral.php 8.0:1110);
        // the commands use the frontend's words.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("trigger.create", """{"triggerids":["8"]}""");
        server.On("trigger.get", """[{"triggerid":"8","description":"CPU high","comments":"Runbook 7"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("$t = New-ZbxTrigger -Name 'CPU high' -Expression 'last(/h/k)>90' -Description 'Runbook 7' -Confirm:$false; $t.Name; $t.Description");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("trigger.create"));
        Assert.Equal("CPU high", request.GetProperty("description").GetString());
        Assert.Equal("Runbook 7", request.GetProperty("comments").GetString());
        Assert.Equal(["CPU high", "Runbook 7"], run.Output.Select(o => o.BaseObject));
    }

    [Fact]
    public void New_ZbxServiceStatusRule_takes_severity_names_for_its_statuses()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("New-ZbxServiceStatusRule -Type 0 -LimitValue 1 -LimitStatus Warning -NewStatus High");

        Assert.Empty(run.Errors);
        var rule = Assert.IsType<Hashtable>(Assert.Single(run.Output).BaseObject);
        // Builders key by wire name and store the wire's number, not the CLR member name.
        Assert.Equal((long)ZabbixServiceStatus.Warning, rule["limit_status"]);
        Assert.Equal((long)ZabbixSeverity.High, rule["new_status"]);
    }

    [Theory]
    [InlineData("NP8MAC", "{$NP8MAC}")]
    [InlineData("$np8mac", "{$NP8MAC}")]
    [InlineData("{$NP8MAC}", "{$NP8MAC}")]
    [InlineData("NAME:\"ctx\"", "{$NAME:\"ctx\"}")]
    [InlineData("{$NAME:\"ctx\"}", "{$NAME:\"ctx\"}")]
    public async Task New_ZbxUserMacro_Macro_turns_a_bare_name_into_a_macro(string written, string wire)
    {
        // usermacro.create answers 'incorrect syntax near "NP8MAC"' for a bare name.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("usermacro.create", """{"hostmacroids":["6"]}""");
        server.On("usermacro.get", """[{"hostmacroid":"6","macro":"{$NP8MAC}"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxUserMacro -HostId 10084 -Macro $m -Value v -Confirm:$false", new Dictionary<string, object?> { ["m"] = written });

        Assert.Empty(run.Errors);
        Assert.Equal(wire, Assert.Single(server.ParamsOf("usermacro.create")).GetProperty("macro").GetString());
    }

    [Theory]
    [InlineData("backup_dir", "{$BACKUP_DIR}")]
    [InlineData("{$BACKUP_DIR}", "{$BACKUP_DIR}")]
    public async Task Get_ZbxUserMacro_Name_selects_by_the_macro_not_the_description(string written, string wire)
    {
        // A user macro's name is its macro, not its free-text description: -Name filters on macro, normalized from a
        // bare name.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("usermacro.get", """[{"hostmacroid":"6","macro":"{$BACKUP_DIR}"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxUserMacro -Name $m", new Dictionary<string, object?> { ["m"] = written });

        Assert.Empty(run.Errors);
        System.Text.Json.JsonElement sent = Assert.Single(server.ParamsOf("usermacro.get"));
        string selected = sent.TryGetProperty("filter", out System.Text.Json.JsonElement filter)
            ? filter.GetProperty("macro").EnumerateArray().Single().GetString()!
            : sent.GetProperty("search").GetProperty("macro").EnumerateArray().Single().GetString()!;
        Assert.Equal(wire, selected);
        Assert.False(sent.TryGetProperty("filter", out System.Text.Json.JsonElement f) && f.TryGetProperty("description", out _));
    }

    [Theory]
    [InlineData("@('10084','10085') | Get-ZbxHost")]
    [InlineData("[pscustomobject]@{ HostId = '10084' }, [pscustomobject]@{ HostId = '10085' } | Get-ZbxHost")]
    public async Task Piped_ids_and_objects_carrying_HostId_select_hosts_by_id(string script)
    {
        // -Id takes pipeline input by value and HostId by property name.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke(script);

        Assert.Empty(run.Errors);
        string[] asked = [.. server.ParamsOf("host.get").SelectMany(p => p.GetProperty("hostids").EnumerateArray()).Select(id => id.GetString()!)];
        Assert.Equal(["10084", "10085"], asked);
    }

    [Fact]
    public async Task Get_ZbxNetworkDiscoveryRule_Id_answers_to_NetworkDiscoveryRuleId()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("(Get-Command Get-ZbxNetworkDiscoveryRule).Parameters['Id'].Aliases -contains 'NetworkDiscoveryRuleId'");

        Assert.Empty(run.Errors);
        Assert.Equal(true, Assert.Single(run.Output).BaseObject);
    }

    [Theory]
    [InlineData("Get-ZbxHost -Id 10084 | New-ZbxItem -Name i -Key k -Type Trapper -ValueType Unsigned -Force", "10084")]
    [InlineData("[pscustomobject]@{ TemplateId = '10001' } | New-ZbxItem -Name i -Key k -Type Trapper -ValueType Unsigned -Force", "10001")]
    // A real template.get record has no hostid (CTemplate.php:270-272 renames it to templateid), so the record's HostId
    // member must not bind $null by property name before the TemplateId alias gets its turn.
    [InlineData("Get-ZbxTemplate -Id 10001 | New-ZbxItem -Name i -Key k -Type Trapper -ValueType Unsigned -Force", "10001")]
    public async Task A_piped_host_or_template_is_what_New_ZbxItem_creates_the_item_on(string script, string hostId)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("template.get", """[{"templateid":"10001","host":"next-t","name":"next-t"}]""");
        server.On("item.create", """{"itemids":["9"]}""");
        server.On("item.get", """[{"itemid":"9","name":"i"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke(script);

        Assert.Empty(run.Errors);
        Assert.Equal(hostId, Assert.Single(server.ParamsOf("item.create")).GetProperty("hostid").GetString());
    }

    [Fact]
    public async Task A_piped_hosts_zero_TemplateId_and_MaintenanceId_filter_nothing()
    {
        // On 8.0.0 host.get returns templateid/maintenanceid "0" (none); bound by property name onto
        // Get-ZbxTrigger -Template/-Maintenance it would send templateids: [0] and empty the result.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """[{"hostid":"10084","host":"web-01","templateid":"0","maintenanceid":"0"}]""");
        server.On("trigger.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Id 10084 | Get-ZbxTrigger");

        Assert.Empty(run.Errors);
        JsonElement asked = Assert.Single(server.ParamsOf("trigger.get"));
        Assert.Equal("""["10084"]""", asked.GetProperty("hostids").GetRawText());
        Assert.False(asked.TryGetProperty("templateids", out _));
        Assert.False(asked.TryGetProperty("maintenanceids", out _));
    }

    [Theory]
    [InlineData("Get-ZbxItem -Id 23 | Get-ZbxTrigger", "trigger.get", "itemids", "templateids")]
    [InlineData("Get-ZbxItem -Id 23 | Get-ZbxTrigger", "trigger.get", "itemids", "hostids")]
    [InlineData("Get-ZbxHost -Id 10084 | Get-ZbxItem", "item.get", "hostids", "proxyids")]
    public async Task A_piped_record_binds_a_reference_filter_only_with_its_own_id(string script, string method, string kept, string dropped)
    {
        // The binder matches a property by name: an item's TemplateId (the template item it was inherited from) bound
        // Get-ZbxTrigger -Template, a host's ProxyId Get-ZbxItem -Proxy. A record's property binds a reference filter only
        // when the record is the filter's own object.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", """[{"itemid":"23","hostid":"10084","templateid":"12345","key_":"app.status"}]""");
        server.On("host.get", """[{"hostid":"10084","host":"web-01","proxyid":"5","monitored_by":"1"}]""");
        server.On("trigger.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke(script);

        Assert.Empty(run.Errors);
        JsonElement asked = server.ParamsOf(method).Last();
        Assert.True(asked.TryGetProperty(kept, out _), asked.GetRawText());
        Assert.False(asked.TryGetProperty(dropped, out _), asked.GetRawText());
    }

    [Fact]
    public async Task A_users_own_record_binds_every_reference_it_names()
    {
        // A CSV row or a [pscustomobject] names its references as the user wrote them.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("[pscustomobject]@{ TemplateId = '10001' } | Get-ZbxItem");

        Assert.Empty(run.Errors);
        Assert.Equal("""["10001"]""", server.ParamsOf("item.get").Last().GetProperty("templateids").GetRawText());
    }

    [Theory]
    [InlineData("New-ZbxHostGroup", "Name", 0)]
    [InlineData("New-ZbxHost", "Name", 0)]
    [InlineData("New-ZbxTemplate", "Name", 0)]
    [InlineData("New-ZbxTrigger", "Name", 0)]
    [InlineData("New-ZbxTrigger", "Expression", 1)]
    [InlineData("New-ZbxAction", "Name", 0)]
    [InlineData("New-ZbxAction", "EventSource", 1)]
    [InlineData("New-ZbxMediaType", "Name", 0)]
    [InlineData("New-ZbxMediaType", "Type", 1)]
    [InlineData("New-ZbxRole", "Type", 1)]
    [InlineData("New-ZbxUser", "Username", 0)]
    [InlineData("New-ZbxUserMacro", "Macro", 0)]
    [InlineData("New-ZbxUserMacro", "Value", 1)]
    [InlineData("New-ZbxHostInterface", "HostId", 0)]
    [InlineData("New-ZbxTag", "Tag", 0)]
    [InlineData("New-ZbxTag", "Value", 1)]
    [InlineData("New-ZbxHttpTestStep", "Name", 0)]
    [InlineData("New-ZbxHttpTestStep", "Url", 1)]
    [InlineData("New-ZbxActionCondition", "Conditiontype", 0)]
    [InlineData("New-ZbxLldFilterCondition", "Value", 1)]
    [InlineData("New-ZbxMapLinkTrigger", "TriggerId", 0)]
    [InlineData("New-ZbxDashboardWidget", "Type", 0)]
    [InlineData("Set-ZbxHostGroup", "Name", 1)]
    [InlineData("Add-ZbxTag", "Name", 0)]
    [InlineData("Add-ZbxTag", "Value", 1)]
    [InlineData("Remove-ZbxTag", "Name", 0)]
    [InlineData("Invoke-ZbxScript", "HostReference", 1)]
    public void A_parameter_keeps_the_shipped_modules_position(string command, string parameter, int position)
    {
        // Positional parameters are part of the module's contract: without them New-ZbxHostGroup Databases binds
        // nothing, and Get-ZbxHost x | Add-ZbxTag env prod binds env as the host.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"(Get-Command {command}).Parameters['{parameter}'].ParameterSets.Values | ForEach-Object Position | Select-Object -Unique");

        Assert.Empty(run.Errors);
        Assert.Equal(position, (int)Assert.Single(run.Output).BaseObject);
    }

    [Fact]
    public async Task A_piped_host_takes_a_tag_by_the_shipped_positional_form()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", parameters => parameters.TryGetProperty("selectTags", out _)
            ? """[{"hostid":"10084","host":"web-01","tags":[]}]"""
            : FakeZabbix.Hosts(("10084", "web-01")));
        server.On("host.update", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost web-01 | Add-ZbxTag env prod -Force");

        Assert.Empty(run.Errors);
        System.Text.Json.JsonElement tag = Assert.Single(Assert.Single(server.ParamsOf("host.update")).EnumerateArray().Single().GetProperty("tags").EnumerateArray());
        Assert.Equal(("env", "prod"), (tag.GetProperty("tag").GetString(), tag.GetProperty("value").GetString()));
    }

    [Fact]
    public async Task An_id_of_zero_piped_by_property_name_selects_nothing_by_id()
    {
        // A host piped to Get-ZbxTemplate binds its HostId to -Host; its TemplateId "0" must not bind to -Id, since
        // templateids: ["0"] empties the result. Zero is Zabbix's "none".
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("template.get", """[{"templateid":"10001","host":"t"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("[pscustomobject]@{ HostId = '10084'; TemplateId = '0' } | Get-ZbxTemplate");

        Assert.Empty(run.Errors);
        System.Text.Json.JsonElement sent = Assert.Single(server.ParamsOf("template.get"));
        Assert.Equal("""["10084"]""", sent.GetProperty("hostids").GetRawText());
        Assert.False(sent.TryGetProperty("templateids", out _));
    }

    [Fact]
    public async Task A_piped_object_selects_itself_not_every_object_of_its_host()
    {
        // PowerShell binds a piped record in the default set first, where -Host could take its HostId and leave its own
        // ItemId unbound: Get-ZbxItem -Id 23 | Get-ZbxItem would read every item of the host. (The pipeline
        // matrix's records carry "0" for every other id, which no filter binds, so it could not see this.)
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", """[{"itemid":"23","hostid":"10084","key_":"app.status"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxItem -Id 23 | Get-ZbxItem");

        Assert.Empty(run.Errors);
        System.Text.Json.JsonElement second = server.ParamsOf("item.get").Last();
        Assert.Equal("""["23"]""", second.GetProperty("itemids").GetRawText());
    }

    // The duration and point-in-time parameters have one shape across the three modules: a TimeSpan for a duration and a
    // DateTimeOffset for a point in time, never seconds as an int or a long clock.
    private static readonly string[] SharedShapes = ["Timeout", "Interval", "RefreshInterval", "HeartbeatInterval", "CheckTimeout", "Clock", "From", "Till", "PeriodFrom", "PeriodTo", "Format"];

    private static IEnumerable<(Type Cmdlet, PropertyInfo Parameter)> Parameters() => ModuleUnderTest.AllCmdlets
        .SelectMany(type => type.GetProperties().Where(p => p.GetCustomAttributes<ParameterAttribute>(true).Any()).Select(p => (type, p)));

    [Fact]
    public void A_parameter_name_means_one_type_in_all_three_modules()
    {
        // A generated New-/Set- field of the same name is the API's own text ("3s", "{$TIMEOUT}"), never this shape.
        string[] split = [.. Parameters()
            .Where(parameter => SharedShapes.Contains(parameter.Parameter.Name) && parameter.Parameter.PropertyType != typeof(string))
            .GroupBy(parameter => parameter.Parameter.Name)
            .Where(group => group.Select(parameter => Nullable.GetUnderlyingType(parameter.Parameter.PropertyType) ?? parameter.Parameter.PropertyType).Distinct().Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(parameter => $"{parameter.Cmdlet.Name} {parameter.Parameter.PropertyType.Name}"))}")];

        Assert.Empty(split);
    }

    [Fact]
    public void A_parameter_name_has_one_type_across_the_modules()
    {
        // SD03, "Use Consistent Parameter Types": a name keeps its type on every cmdlet. Compared by shape: a nullable is
        // its value type, an array its element, a reference a reference whatever it names, each object's own enum one
        // enum (-Status, -Type) and each object's own request record or get parameters one record (-Tag, -Filter). The
        // reviewed differences are listed: the API's own word for two concepts (a report's -ActiveSince is a date, a
        // maintenance's a clock; -ManualInput is a script's flag and the input Invoke-ZbxScript passes), a wire or agent
        // command naming a host group by the string it creates it under, and the API's text beside a typed shell value
        // (-Port is the API's text, which takes a {$MACRO}; an agent's or a proxy's listening port is a number), and a list
        // parameter's singular beside another object's scalar of the same word (-Macro: a host's macros, a user macro's
        // name; -Highlight: a map link's highlights, a map's highlight flag).
        string[] reviewed =
        [
            "ActiveSince: DateOnly, DateTimeOffset",
            "ActiveTill: DateOnly, DateTimeOffset",
            "Agent: DnsEndPoint, String",
            "AuthType: Enum, Int32",
            "Config: JsonElement, Record",
            "Details: Record, SwitchParameter",
            "EventName: Record, String",
            "Expression: Record, String",
            "Group: Reference, String",
            "Highlight: Boolean, Record",
            "History: String, ZabbixProxyHistoryItem",
            "HostGroup: Reference, String",
            "Id: Int64, String, UInt64",
            "InputObject: PSObject, Reference, String, ZabbixHost",
            "Interface: Record, Reference",
            "InterfacePort: Int32, String",
            "Key: String, ZabbixItemKey",
            "Macro: Record, String",
            "ManualInput: Boolean, String",
            "Parameter: Object, Record",
            "Period: Enum, String",
            "Port: Int32, String",
            "Posts: JsonElement, String",
            "Proxy: Reference, Uri",
            "Script: Reference, String",
            "Server: DnsEndPoint, String",
            "Tag: Record, String",
            "Timeout: String, TimeSpan",
            "Type: Enum, String",
            "Url: Record, String",
            "UseIp: Enum, SwitchParameter",
            "User: Record, Reference",
            "UserGroup: Record, Reference",
            "Value: Object, String",
            "Version: String, ZabbixComponentVersion",
        ];

        static string Shape(Type type)
        {
            Type value = Nullable.GetUnderlyingType(type) ?? (type.IsArray ? type.GetElementType()! : type);
            value = Nullable.GetUnderlyingType(value) ?? value;
            if (value.IsGenericType && value.GetGenericTypeDefinition() == typeof(ZabbixReference<>)) return "Reference";
            if (value.IsEnum && value.Namespace == "Community.Zabbix") return "Enum";
            return value.Namespace == "Community.Zabbix" && (value.Name.EndsWith("Request", StringComparison.Ordinal) || value.Name.EndsWith("GetParameters", StringComparison.Ordinal)) ? "Record" : value.Name;
        }

        string[] split = [.. Parameters()
            .GroupBy(parameter => parameter.Parameter.Name)
            .Select(group => (group.Key, Shapes: group.Select(parameter => Shape(parameter.Parameter.PropertyType)).Distinct().Order(StringComparer.Ordinal).ToArray()))
            .Where(group => group.Shapes.Length > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Shapes)}")
            .Order(StringComparer.Ordinal)];

        Assert.Equal(reviewed, split);
    }

    [Fact]
    public void A_duration_is_a_TimeSpan_read_as_seconds_and_a_time_is_a_DateTimeOffset_read_as_a_date()
    {
        // No -FooSeconds int beside a TimeSpan sibling, and every TimeSpan or DateTimeOffset
        // carries the transformation that reads a number as seconds and a date as local time.
        string[] offenders = [.. Parameters()
            .Where(parameter => parameter.Parameter.Name.EndsWith("Seconds", StringComparison.Ordinal)
                || ((Nullable.GetUnderlyingType(parameter.Parameter.PropertyType) ?? parameter.Parameter.PropertyType) == typeof(TimeSpan) && parameter.Parameter.GetCustomAttribute<ZabbixDurationAttribute>() is null)
                || ((Nullable.GetUnderlyingType(parameter.Parameter.PropertyType) ?? parameter.Parameter.PropertyType) == typeof(DateTimeOffset) && parameter.Parameter.GetCustomAttribute<ZabbixClockAttribute>() is null)
                || ((Nullable.GetUnderlyingType(parameter.Parameter.PropertyType) ?? parameter.Parameter.PropertyType) == typeof(TimeOnly) && parameter.Parameter.GetCustomAttribute<ZabbixTimeOfDayAttribute>() is null))
            .Select(parameter => $"{parameter.Cmdlet.Name} -{parameter.Parameter.Name}")];

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("2", "00:00:02")]
    [InlineData("1.5", "00:00:01.5000000")]
    [InlineData("'00:00:03'", "00:00:03")]
    [InlineData("'45'", "00:00:45")]
    [InlineData("([timespan]::FromSeconds(4))", "00:00:04")]
    [InlineData("'2s'", "00:00:02")]
    [InlineData("'5m'", "00:05:00")]
    [InlineData("'1h'", "01:00:00")]
    [InlineData("'1d'", "1.00:00:00")]
    [InlineData("'2w'", "14.00:00:00")]
    [InlineData("' 30s '", "00:00:30")]
    public void A_duration_reads_a_number_as_seconds(string argument, string expected)
    {
        // PowerShell's own [timespan]2 is two ticks; the parameter's transformation reads it as two seconds.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"(Get-Command Wait-ZbxHost).Parameters['Timeout'].Attributes.Where({{ $_ -is [System.Management.Automation.ArgumentTransformationAttribute] }}).Transform($ExecutionContext, {argument}).ToString('c')");

        Assert.Empty(run.Errors);
        Assert.Equal(expected, Assert.Single(run.Output).ToString());
    }

    [Theory]
    [InlineData("7200", "02:00:00")]
    [InlineData("'7200'", "02:00:00")]
    [InlineData("'02:00'", "02:00:00")]
    [InlineData("'22:30'", "22:30:00")]
    [InlineData("([timespan]::FromHours(3))", "03:00:00")]
    [InlineData("([TimeOnly]::new(4, 15))", "04:15:00")]
    public void A_time_of_day_reads_a_number_as_seconds_after_midnight(string argument, string expected)
    {
        // PowerShell's own [TimeOnly]7200 is 7200 ticks; the parameter's transformation reads it as two hours.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"(Get-Command New-ZbxMaintenanceTimePeriod).Parameters['StartTime'].Attributes.Where({{ $_ -is [System.Management.Automation.ArgumentTransformationAttribute] }}).Transform($ExecutionContext, {argument}).ToString('HH:mm:ss', [cultureinfo]::InvariantCulture)");

        Assert.Empty(run.Errors);
        Assert.Equal(expected, Assert.Single(run.Output).ToString());
    }

    [Theory]
    [InlineData("86400")]
    [InlineData("-1")]
    [InlineData("'25:00'")]
    [InlineData("'soon'")]
    public void A_time_outside_one_day_is_a_binding_error(string argument)
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"New-ZbxMaintenanceTimePeriod -TimePeriodType Daily -Every 1 -StartTime {argument} -Period 900");

        Assert.StartsWith("ParameterArgumentTransformationError,", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Test-ZbxAgentItem agent.ping -Timeout 0")]
    [InlineData("Test-ZbxAgentItem agent.ping -Timeout -1")]
    [InlineData("Test-ZbxAgentItem agent.ping -Timeout 'soon'")]
    [InlineData("Wait-ZbxHost web-01 -Timeout 0")]
    [InlineData("Wait-ZbxHost web-01 -Interval '0s'")]
    [InlineData("Wait-ZbxHost web-01 -Interval '5x'")]
    [InlineData("Wait-ZbxHost web-01 -Interval 'm'")]
    [InlineData("Wait-ZbxHost web-01 -Interval '-5m'")]
    [InlineData("Wait-ZbxHost web-01 -Interval '1.5m'")]
    public void A_duration_of_zero_or_less_or_not_a_duration_is_a_binding_error(string command)
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke(command);

        Assert.StartsWith("ParameterArgumentTransformationError,", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_duration_parameter_takes_Zabbix_time_suffixes()
    {
        // The suffix form an item's update interval is written in ('30s', '5m') reads the same on every TimeSpan
        // parameter of the three modules, a narrower range included.
        string[] refused = [.. Parameters()
            .Where(parameter => (Nullable.GetUnderlyingType(parameter.Parameter.PropertyType) ?? parameter.Parameter.PropertyType) == typeof(TimeSpan))
            .Where(parameter => !Equals(parameter.Parameter.GetCustomAttribute<ZabbixDurationAttribute>()?.Transform(null!, "2s"), TimeSpan.FromSeconds(2))
                || !Equals(parameter.Parameter.GetCustomAttribute<ZabbixDurationAttribute>()?.Transform(null!, "1m"), TimeSpan.FromMinutes(1)))
            .Select(parameter => $"{parameter.Cmdlet.Name} -{parameter.Parameter.Name}")];

        Assert.Empty(refused);
        Assert.Contains(Parameters(), parameter => parameter.Cmdlet.Name == "WatchZbxItemCommand" && parameter.Parameter.Name == "Interval");
    }

    [Theory]
    [InlineData("(Get-Date '2026-10-01T08:00:00Z').ToUniversalTime()", 1790841600)]
    [InlineData("[DateTimeOffset]'2026-10-01T08:00:00Z'", 1790841600)]
    [InlineData("'2026-10-01T08:00:00Z'", 1790841600)]
    [InlineData("1790841600", 1790841600)]
    [InlineData("'1790841600.5'", 1790841600)]
    public async Task A_point_in_time_binds_a_date_seconds_or_a_clock(string from, long seconds)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("trend.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke($"Get-ZbxTrend -From ({from}) -Limit 1");

        Assert.Empty(run.Errors);
        Assert.Equal(seconds, Assert.Single(server.ParamsOf("trend.get")).GetProperty("time_from").GetInt64());
    }
}
