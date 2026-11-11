// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The objects the module writes, through PowerShell's own serializers and formatters —
/// <c>Export-Clixml</c>/<c>Import-Clixml</c>, <c>ConvertTo-Json</c>, <c>Format-Table</c>/<c>Format-List</c> — for every
/// entity a <c>Get-</c> cmdlet emits (found by reflection over the <c>ZabbixGetCmdlet&lt;TEntity, TParameters&gt;</c>
/// cmdlets), each read from the same JSON the wire carries, with one field the model does not know (<c>zz_extra</c>).
/// </summary>
public sealed class AdversarialOutputTests
{
    // Type names and adapter internals that mean a value was rendered as its CLR type or its JSON plumbing.
    private static readonly string[] Leaks = ["System.Object[]", "System.Collections.Generic", "System.Text.Json", "ValueKind", "JsonElement"];

    /// <summary>Every distinct entity a generated or hand-written <c>Get-</c> cmdlet emits, read from wire JSON.</summary>
    internal static IReadOnlyList<object> Entities(bool nested = true)
    {
        string extra = nested ? ""","zz_nested":{"a":[1,2],"b":{"c":null}}""" : string.Empty;
        var entities = new List<object>();
        foreach (Type entityType in typeof(ZabbixCmdlet).Assembly.GetTypes()
            .Select(EntityOf).OfType<Type>().Distinct().OrderBy(type => type.Name, StringComparer.Ordinal))
        {
            object shape = typeof(ZabbixEntities).GetMethod(nameof(ZabbixEntities.Of))!.MakeGenericMethod(entityType).Invoke(null, null)!;
            string idField = (string)shape.GetType().GetProperty("IdField")!.GetValue(shape)!;
            string nameField = (string)shape.GetType().GetProperty("NameField")!.GetValue(shape)!;
            JsonTypeInfo info = Community.Zabbix.ZabbixApiJson.Default.GetTypeInfo(entityType)!;
            string name = idField == nameField ? string.Empty : $$""","{{nameField}}":"name-1" """.TrimEnd();
            string json = $$"""{"{{idField}}":"1"{{name}},"zz_extra":"x"{{extra}} """.TrimEnd() + "}";
            entities.Add(JsonSerializer.Deserialize(json, info)!);
        }
        return entities;
    }

    private static Type? EntityOf(Type type)
    {
        if (type.IsAbstract) return null;
        for (Type? t = type.BaseType; t is not null; t = t.BaseType)
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ZabbixGetCmdlet<,>)) return t.GetGenericArguments()[0];
        return null;
    }

    [Fact]
    public void Every_entity_a_Get_cmdlet_emits_is_found_and_read_with_its_unmodelled_field()
    {
        IReadOnlyList<object> entities = Entities();

        Assert.True(entities.Count >= 40, $"only {entities.Count} entity types found");
        Assert.All(entities, entity => Assert.True(
            (entity as IZabbixAdditionalFields)?.AdditionalFields?.ContainsKey("zz_extra") == true,
            entity.GetType().Name));
    }

    [Fact]
    public void Export_Clixml_then_Import_Clixml_keeps_every_entitys_id_name_and_unmodelled_field()
    {
        string directory = Directory.CreateTempSubdirectory("advps-clixml-").FullName;
        try
        {
            using Shell shell = Shell.Open();

            Run run = shell.Invoke("""
                $i = 0
                foreach ($e in $entities) {
                    $path = Join-Path $directory "$i.xml"; $i++
                    $e | Export-Clixml -Path $path
                    $back = Import-Clixml -Path $path
                    [pscustomobject]@{
                        Type  = $e.GetType().Name
                        Extra = $back.ZzExtra
                        Id    = @($back.PSObject.Properties | Where-Object { $_.Value -eq '1' }).Count
                        Name  = @($back.PSObject.Properties | Where-Object { $_.Value -eq 'name-1' }).Count
                        Named = @($e.PSObject.Properties | Where-Object { $_.Value -eq 'name-1' }).Count
                    }
                }
                """, new Dictionary<string, object?> { ["entities"] = Entities().ToArray(), ["directory"] = directory });

            Assert.Empty(run.Errors);
            Assert.All(run.Output, row =>
            {
                string type = (string)row.Properties["Type"].Value;
                Assert.True("x".Equals(row.Properties["Extra"].Value), $"{type}: zz_extra did not survive Export/Import-Clixml");
                Assert.True((int)row.Properties["Id"].Value >= 1, $"{type}: the id did not survive");
                Assert.Equal((int)row.Properties["Named"].Value, (int)row.Properties["Name"].Value);
            });
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ConvertTo_Json_of_every_entity_carries_the_unmodelled_field_as_a_value_and_no_adapter_internals()
    {
        // For every entity: ZabbixAdditionalFieldsAdapter.GetProperties lists the bag's keys, not AdditionalFields itself as a
        // public CLR property, so ConvertTo-Json writes "ZzExtra":"x" once, by the name the records give their members. Listing the bag as a property would write
        // "AdditionalFields":{"zz_extra":{"ValueKind":3},"zz_nested":{"ValueKind":1}} next to it: the JsonElement's own
        // shape leaks (the value is lost) and every unmodelled field is written twice.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("foreach ($e in $entities) { [pscustomobject]@{ Type = $e.GetType().Name; Json = ($e | ConvertTo-Json -Depth 10 -WarningAction SilentlyContinue) } }",
            new Dictionary<string, object?> { ["entities"] = Entities().ToArray() });

        Assert.Empty(run.Errors);
        Assert.All(run.Output, row =>
        {
            string type = (string)row.Properties["Type"].Value;
            string json = row.Properties["Json"].Value.ToString()!;
            using JsonDocument document = JsonDocument.Parse(json);
            Assert.Equal("x", document.RootElement.GetProperty("ZzExtra").GetString());
            Assert.False(document.RootElement.TryGetProperty("AdditionalFields", out _), $"{type}: AdditionalFields leaked into ConvertTo-Json");
            Assert.DoesNotContain("ValueKind", json, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ConvertTo_Json_of_every_entity_completes_without_a_cycle_or_an_exception()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("foreach ($e in $entities) { $e | ConvertTo-Json -Depth 10 -WarningAction SilentlyContinue }",
            new Dictionary<string, object?> { ["entities"] = Entities().ToArray() });

        Assert.Empty(run.Errors);
        Assert.Equal(Entities().Count, run.Output.Count);
        Assert.All(run.Output, json => JsonDocument.Parse((string)json.BaseObject).Dispose());
    }

    [Fact]
    public void Format_List_and_Format_Table_of_every_entity_render_values_not_type_names()
    {
        // For every entity: the module ships FormatsToProcess with default views. Without them Format-List prints every
        // property (ZabbixHost: 60 rows, mostly empty), an "AdditionalFields : {[zz_extra, x]}" row (the bag's raw
        // KeyValuePair/JsonElement rendering) repeats the adapted "zz_extra : x" row below it, and Format-Table's columns are
        // whatever the first properties happen to be.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("foreach ($e in $entities) { [pscustomobject]@{ Type = $e.GetType().Name; List = ($e | Format-List | Out-String -Width 400); Table = ($e | Format-Table | Out-String -Width 400) } }",
            new Dictionary<string, object?> { ["entities"] = Entities(nested: false).ToArray() });

        Assert.Empty(run.Errors);
        Assert.All(run.Output, row =>
        {
            string type = (string)row.Properties["Type"].Value;
            foreach (string view in new[] { "List", "Table" })
            {
                string text = row.Properties[view].Value.ToString()!;
                Assert.False(Leaks.Any(leak => text.Contains(leak, StringComparison.Ordinal)), $"{type} {view}: {text}");
                Assert.DoesNotContain("AdditionalFields", text, StringComparison.Ordinal);
            }
        });
    }

    [Fact]
    public async Task A_host_with_nested_lists_formats_its_groups_and_tags_as_values_not_type_names()
    {
        // Format-List of a host read with groups/tags/interfaces renders each nested record as values. Through the C# record's
        // compiler ToString it would read "Groups : {ZabbixHostGroup { GroupId = 1, Name = Linux, Flags = , Uuid = ,
        // Hosts = , Groupdiscoveries = , DiscoveryRules = , HostPrototypes = , DiscoveryData = , AdditionalFields =  }}",
        // and the bag again as "AdditionalFields : {[zz_extra, {"k":"v"}]}" (raw JSON text) above "zz_extra : @{k=v}".
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """
            [{"hostid":"1","host":"web","name":"web","groups":[{"groupid":"1","name":"Linux"}],
              "tags":[{"tag":"env","value":"prod"}],"interfaces":[{"interfaceid":"2","ip":"10.0.0.1","port":"10050","type":"1"}],
              "inventory":{"os":"Linux"},"zz_extra":{"k":"v"}}]
            """);
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost | Format-List | Out-String -Width 400");

        Assert.Empty(run.Errors);
        string text = (string)Assert.Single(run.Output).BaseObject;
        Assert.False(Leaks.Any(leak => text.Contains(leak, StringComparison.Ordinal)), text);
        Assert.DoesNotContain("AdditionalFields", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Get_Member_of_an_entity_lists_its_modelled_and_unmodelled_properties()
    {
        using Shell shell = Shell.Open();
        object host = Entities().Single(e => e is ZabbixHost);

        Run run = shell.Invoke("($e | Get-Member -MemberType Properties).Name", new Dictionary<string, object?> { ["e"] = host });

        Assert.Empty(run.Errors);
        string[] names = [.. run.Output.Select(o => (string)o.BaseObject)];
        Assert.Contains("HostId", names);
        Assert.Contains("TechnicalName", names);
        Assert.Contains("ZzExtra", names);
    }

    [Fact]
    public void An_unmodelled_field_is_named_as_the_records_name_members_and_typed_as_its_value()
    {
        // vendor_name beside TechnicalName takes the records' member naming, not wire casing, and Get-Member types a string as its value, not JsonElement.
        using Shell shell = Shell.Open();
        object host = Entities().Single(e => e is ZabbixHost);

        Run run = shell.Invoke("$m = $e | Get-Member -Name ZzExtra; $m.Definition; $e.zz_extra; $e.ZzExtra", new Dictionary<string, object?> { ["e"] = host });

        Assert.Empty(run.Errors);
        Assert.Equal(["System.String ZzExtra {get;}", "x", "x"], run.Output.Select(o => o.ToString()));
    }

    [Fact]
    public void Select_Object_star_of_an_entity_is_a_flat_object_whose_unmodelled_field_is_a_value()
    {
        using Shell shell = Shell.Open();
        object host = Entities().Single(e => e is ZabbixHost);

        Run run = shell.Invoke("$s = $e | Select-Object *; $s.ZzExtra; $s.ZzNested.a.Count; $s.ZzNested.b.c -eq $null",
            new Dictionary<string, object?> { ["e"] = host });

        Assert.Empty(run.Errors);
        Assert.Equal(["x", 2, true], run.Output.Select(o => o.BaseObject));
    }

    [Fact]
    public void A_numeric_wire_value_beyond_Int64_reads_as_a_BigInteger_and_a_real_as_a_double()
    {
        using Shell shell = Shell.Open();
        var host = JsonSerializer.Deserialize(
            """{"hostid":"1","host":"h","zz_big":18446744073709551616,"zz_real":1.5e3,"zz_neg":-0,"zz_str":"007"}""",
            (JsonTypeInfo<ZabbixHost>)Community.Zabbix.ZabbixApiJson.Default.GetTypeInfo(typeof(ZabbixHost))!);

        Run run = shell.Invoke("$e.zz_big.GetType().Name; $e.zz_real; $e.zz_neg; $e.zz_str", new Dictionary<string, object?> { ["e"] = host });

        Assert.Empty(run.Errors);
        Assert.Equal(["BigInteger", 1500d, 0L, "007"], run.Output.Select(o => o.BaseObject));
    }

    [Fact]
    public void The_adapter_reports_every_entity_type_and_its_ancestors_as_its_type_names()
    {
        using Shell shell = Shell.Open();
        object host = Entities().Single(e => e is ZabbixHost);

        Run run = shell.Invoke("$e.PSObject.TypeNames", new Dictionary<string, object?> { ["e"] = host });

        Assert.Equal(["Community.Zabbix.ZabbixHost", "System.Object"], run.Output.Select(o => (string)o.BaseObject));
    }
}
