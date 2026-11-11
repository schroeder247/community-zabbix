// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
extern alias ZabbixGenerator;

using System.Xml.Linq;
using ZabbixGenerator::Community.Zabbix.Generator;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The generated <c>Community.Zabbix.format.ps1xml</c>: a table, list and wide view per
/// read record, derived from the description by <see cref="ZabbixTypesEmitter.GenerateFormat"/>, loaded by the
/// manifest's <c>FormatsToProcess</c>, so a <c>Get-</c> prints a table instead of a 56-line property dump.
/// </summary>
public sealed class ZabbixFormatPs1XmlTests
{
    private static readonly string SchemaPath = Path.Combine(AppContext.BaseDirectory, "schema", "zabbix-api.json");
    private static readonly string GeneratedFormatPath = Path.Combine(AppContext.BaseDirectory, "Community.Zabbix.format.ps1xml");

    [Fact]
    public void The_built_format_ps1xml_matches_a_fresh_regeneration_from_the_current_schema()
    {
        ApiDescription description = ApiDescription.Read(File.ReadAllText(SchemaPath));
        string expected = ZabbixTypesEmitter.GenerateFormat(description);
        string actual = File.ReadAllText(GeneratedFormatPath).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Every_read_entity_with_an_id_has_a_table_view_that_leads_with_its_id_and_its_name()
    {
        ApiDescription description = ApiDescription.Read(File.ReadAllText(SchemaPath));
        XDocument format = XDocument.Load(GeneratedFormatPath);
        Dictionary<string, XElement> tables = format.Descendants("View")
            .Where(v => v.Element("TableControl") is not null)
            .ToDictionary(v => v.Element("ViewSelectedBy")!.Element("TypeName")!.Value, StringComparer.Ordinal);
        Dictionary<string, ServiceDescription> services = description.Services.GroupBy(s => s.Object).ToDictionary(g => g.Key, g => g.First());

        var missing = new List<string>();
        foreach (TypeDescription read in description.Types.Where(t => t.Role == "read" && t.Object is not null && services.ContainsKey(t.Object)))
        {
            FieldDescription? id = ZabbixCmdletEmitter.EntityIdField(services[read.Object!], read);
            if (id is null) continue;
            string typeName = "Community.Zabbix." + (read.Name.StartsWith("Zabbix", StringComparison.Ordinal) ? read.Name : "Zabbix" + read.Name);
            if (!tables.TryGetValue(typeName, out XElement? view))
            {
                missing.Add(typeName);
                continue;
            }
            string[] columns = [.. view.Descendants("PropertyName").Select(p => p.Value)];
            Assert.Equal(id.Name, columns[0]);
            if (ZabbixCmdletEmitter.EntityNameField(read.Object!, read) is { Kind: not ("json" or "output") } name && !name.Kind.StartsWith("list:", StringComparison.Ordinal))
                Assert.Equal(name.Name, columns[1]);
            Assert.InRange(columns.Length, 1, ZabbixTypesEmitter.MaxColumns);
        }

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_clock_a_read_record_carries_shows_as_a_date_in_every_view_of_it()
    {
        // A clock field the description types "clock" (problem Clock, maintenance ActiveSince/ActiveTill, token
        // CreatedAt/ExpiresAt, a nested record's TsDelete) is a ScriptBlock in every view, never its clock.ns text, and a
        // record that carries one has a view even where PowerShell would table it by itself.
        ApiDescription description = ApiDescription.Read(File.ReadAllText(SchemaPath));
        XDocument format = XDocument.Load(GeneratedFormatPath);
        ILookup<string, XElement> views = format.Descendants("View").ToLookup(v => v.Element("ViewSelectedBy")!.Element("TypeName")!.Value, StringComparer.Ordinal);

        var violations = new List<string>();
        foreach (TypeDescription type in description.Types.Where(t => t.Role is "read" or "nested-read"))
        {
            string[] clocks = [.. type.Fields.Where(f => f.Kind == "clock").Select(f => f.Name)];
            if (clocks.Length == 0) continue;
            string typeName = "Community.Zabbix." + (type.Name.StartsWith("Zabbix", StringComparison.Ordinal) ? type.Name : "Zabbix" + type.Name);
            if (!views[typeName].Any()) violations.Add(typeName + ": no view");
            foreach (XElement view in views[typeName])
            {
                foreach (string raw in view.Descendants("PropertyName").Select(p => p.Value).Intersect(clocks))
                    violations.Add($"{typeName} {view.Element("Name")!.Value}: {raw} shows as its clock.ns text");
            }
        }

        Assert.True(violations.Count == 0, string.Join('\n', violations));
    }

    [Fact]
    public async Task A_problem_s_a_maintenance_s_and_a_token_s_times_print_as_local_dates()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("problem.get", """[{"eventid":"7","name":"CPU","severity":"3","clock":"1793290980","ns":"0","r_clock":"0"}]""");
        server.On("maintenance.get", """[{"maintenanceid":"7","name":"m","active_since":"1793290980","active_till":"1793290980"}]""");
        server.On("token.get", """[{"tokenid":"7","name":"t","created_at":"1793290980","expires_at":"1793290980","lastaccess":"0"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            [DateTimeOffset]::FromUnixTimeSeconds(1793290980).LocalDateTime.ToString()
            Get-ZbxProblem | Out-String -Width 200
            Get-ZbxMaintenance | Format-List | Out-String -Width 200
            Get-ZbxToken | Format-List | Out-String -Width 200
            (Get-ZbxProblem).Clock | Out-String -Width 200
            """);

        Assert.Empty(run.Errors);
        string local = run.Output[0].ToString()!;
        foreach (string text in run.Output.Skip(1).Take(3).Select(o => o.ToString()!))
        {
            Assert.Contains(local, text, StringComparison.Ordinal);
            Assert.DoesNotContain("1793290980", text, StringComparison.Ordinal);
        }
        // A clock on its own: a date.
        Assert.Contains(local, run.Output[4].ToString()!, StringComparison.Ordinal);
    }

    [Fact]
    public void Import_Module_loads_the_format_data()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("(Get-FormatData -TypeName 'Community.Zabbix.ZabbixHost').FormatViewDefinition.Name");

        Assert.Empty(run.Errors);
        Assert.Equal(["ZabbixHost.Table", "ZabbixHost.List", "ZabbixHost.Wide"], run.Output.Select(o => o.BaseObject.ToString()));
    }

    [Fact]
    public async Task A_host_prints_as_a_table_of_its_summary_columns()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """[{"hostid":"10084","host":"web-01","name":"Web One","status":"0","ipmi_authtype":"-1"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Id 10084 | Out-String -Width 200");

        Assert.Empty(run.Errors);
        string[] lines = [.. Assert.Single(run.Output).ToString()!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        Assert.Matches(@"^HostId\s+TechnicalName\s+VisibleName\s+Status$", lines[0]);
        Assert.Matches(@"^10084\s+web-01\s+Web One\s+Enabled$", lines[2]);
        Assert.DoesNotContain(lines, line => line.Contains("IpmiAuthType", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_item_table_tells_hosts_apart_and_shows_the_last_value_and_history_shows_no_log_only_column()
    {
        // Items of several hosts are told apart, and a numeric value's history shows no empty log-only
        // Severity and Source columns.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", """[{"itemid":"23","hostid":"10084","name":"CPU","key_":"system.cpu.load","status":"0","lastvalue":"0.42"}]""");
        server.On("history.get", """[{"itemid":"23","clock":"1790000000","ns":"5","value":"0.42"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run item = shell.Invoke("Get-ZbxItem -Id 23 | Out-String -Width 200");
        Run history = shell.Invoke("Get-ZbxHistory -Id 23 | Out-String -Width 200");

        Assert.Empty(item.Errors);
        Assert.Matches(@"^ItemId\s+Name\s+Key\s+HostId\s+LastValue\s+LastClock$", Assert.Single(item.Output).ToString()!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0]);
        string header = Assert.Single(history.Output).ToString()!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
        Assert.DoesNotContain("Severity", header, StringComparison.Ordinal);
        Assert.DoesNotContain("Source", header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_records_Unix_time_is_also_a_date_to_compare()
    {
        // Get-ZbxHistory | Where-Object Time -gt (Get-Date).AddHours(-1): Clock is the wire's number, Time the date.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("history.get", """[{"itemid":"23","clock":"1790000000","ns":"500","value":"1"}]""");
        server.On("item.get", """[{"itemid":"23","hostid":"10084","lastclock":"0"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("$h = Get-ZbxHistory -Id 23; $h.Time.ToUniversalTime().ToString('o'); ($h | Where-Object Time -gt ([datetime]'2026-01-01')).Count; $null -eq (Get-ZbxItem -Id 23).LastTime");

        Assert.Empty(run.Errors);
        Assert.Equal(["2026-09-21T14:13:20.0000005Z", "1", "True"], run.Output.Select(o => o.ToString()));
    }

    [Fact]
    public async Task Format_List_shows_the_summary_first_and_every_modelled_field_but_not_the_AdditionalFields_bag()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """[{"hostid":"10084","host":"web-01","name":"Web One","status":"0","unmodelled":"x"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Id 10084 | Format-List | Out-String -Width 200");

        Assert.Empty(run.Errors);
        string[] names = [.. Assert.Single(run.Output).ToString()!.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.Split(':')[0].Trim())];
        Assert.Equal(["HostId", "TechnicalName", "VisibleName", "Status"], names.Take(4));
        Assert.Contains("IpmiAuthType", names);
        Assert.Contains("HostGroups", names);
        Assert.DoesNotContain("AdditionalFields", names);
    }

    [Fact]
    public async Task Format_Wide_shows_the_name()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"2","name":"Linux servers"},{"groupid":"4","name":"Zabbix servers"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHostGroup | Format-Wide | Out-String -Width 200");

        Assert.Empty(run.Errors);
        string text = Assert.Single(run.Output).ToString()!;
        Assert.Contains("Linux servers", text, StringComparison.Ordinal);
        Assert.Contains("Zabbix servers", text, StringComparison.Ordinal);
        Assert.DoesNotContain("GroupId", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_output_type_with_more_than_four_properties_has_a_view()
    {
        // PowerShell tables a type of four or fewer properties by itself; above that it prints a list of every property.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""
            $types = Get-Command -Module Community.Zabbix, Community.Zabbix.Agent, Community.Zabbix.Proxy | ForEach-Object OutputType | ForEach-Object Type | Where-Object { $_ } | Sort-Object FullName -Unique
            foreach ($t in $types) {
                if ($t.IsPrimitive -or $t -in [string], [object], [psobject], [hashtable], [version], [void] -or $t.Namespace -like 'System*') { continue }
                if (@($t.GetProperties([Reflection.BindingFlags]'Public,Instance')).Count -le 4) { continue }
                if (-not (Get-FormatData -TypeName $t.FullName)) { $t.FullName }
            }
            """);

        Assert.Empty(run.Errors);
        Assert.Empty(run.Output.Select(o => o.ToString()));
    }
}
