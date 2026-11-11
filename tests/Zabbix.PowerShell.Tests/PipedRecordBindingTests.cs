// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Reflection;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// A generated <c>New-</c> or <c>Set-</c> takes every field by property name, so <c>Import-Csv hosts.csv | New-ZbxHost</c>
/// creates one host per row, while a piped Zabbix record binds only what is its own (its own object's references and its
/// own id): <c>Get-ZbxHost | New-ZbxItem</c> takes the host's id and never its description, <c>Get-ZbxEvent |
/// New-ZbxHost</c> binds nothing and is refused (<see cref="ZabbixPipedRecord"/>).
/// </summary>
public sealed class PipedRecordBindingTests
{
    /// <summary>Every generated <c>New-</c> and <c>Set-</c>.</summary>
    public static TheoryData<string> ChangeCommands { get; } = [.. ModuleUnderTest.Zabbix.Cmdlets
        .Where(type => type.Namespace == "Community.Zabbix.PowerShell.Commands.Generated" && IsChange(type))
        .Select(type => type.GetCustomAttribute<CmdletAttribute>()!)
        .Select(cmdlet => $"{cmdlet.VerbName}-{cmdlet.NounName}")
        .Order(StringComparer.Ordinal)];

    private static bool IsChange(Type type)
    {
        for (Type? t = type.BaseType; t is not null; t = t.BaseType)
            if (t.IsGenericType && (t.GetGenericTypeDefinition() == typeof(ZabbixNewCmdlet<>) || t.GetGenericTypeDefinition() == typeof(ZabbixSetCmdlet<,>))) return true;
        return false;
    }

    [Theory]
    [MemberData(nameof(ChangeCommands))]
    public void Every_field_parameter_of_a_generated_New_or_Set_binds_by_property_name_and_no_secret_does(string command)
    {
        Type type = ModuleUnderTest.Zabbix.Cmdlets.Single(t => t.GetCustomAttribute<CmdletAttribute>() is { } c && $"{c.VerbName}-{c.NounName}" == command);
        // A switch that says what the command does (New-ZbxToken -Generate asks for the secret too) is not a field: a
        // column of that name must not turn it on.
        string[] notFields = ["AdditionalProperty", "Force", "PassThru", "Generate", "InputObject", "PipedRecord", "Connection"];
        List<string> violations = [];
        foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            ParameterAttribute[] uses = [.. property.GetCustomAttributes<ParameterAttribute>(true)];
            if (uses.Length == 0 || Array.IndexOf(notFields, property.Name) >= 0) continue;
            bool byName = uses.Any(use => use.ValueFromPipelineByPropertyName);
            bool secret = property.PropertyType == typeof(System.Security.SecureString);
            if (byName == secret) violations.Add(property.Name + (secret ? " is a secret bound by property name" : " does not bind by property name"));
        }

        Assert.True(violations.Count == 0, command + ": " + string.Join(", ", violations));
    }

    [Fact]
    public async Task A_piped_object_s_properties_create_the_host_it_names()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["9"]}""").On("host.get", """[{"hostid":"9","host":"h1"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("[pscustomobject]@{ Name = 'h1'; InterfaceIP = '10.0.0.1'; Description = 'from a row' } | New-ZbxHost -Group 2 -Confirm:$false -Verbose");

        Assert.Empty(run.Errors);
        Assert.Contains(run.Verbose, line => line.Contains("\"New host\" on target \"host 'h1'\"", StringComparison.Ordinal));
        JsonElement created = Assert.Single(server.ParamsOf("host.create"));
        Assert.Equal("h1", created.GetProperty("host").GetString());
        Assert.Equal("from a row", created.GetProperty("description").GetString());
        Assert.Equal("10.0.0.1", created.GetProperty("interfaces")[0].GetProperty("ip").GetString());
        Assert.Equal("2", created.GetProperty("groups")[0].GetProperty("groupid").GetString());
    }

    [Fact]
    public async Task WhatIf_names_the_piped_host_and_creates_nothing()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        // The prompt's text (what -WhatIf prints) is the verbose line -Confirm:$false -Verbose writes: target 'h1'.
        Run run = shell.Invoke("[pscustomobject]@{ Name = 'h1'; InterfaceIP = '10.0.0.1' } | New-ZbxHost -Group g -WhatIf");

        Assert.Empty(run.Errors);
        Assert.Empty(server.ParamsOf("host.create"));
        Assert.Empty(server.ParamsOf("hostgroup.get"));
    }

    [Fact]
    public async Task Each_row_of_a_csv_file_creates_its_own_host()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["9"]}""").On("host.get", """[{"hostid":"9","host":"h"}]""");
        server.On("hostgroup.get", """[{"groupid":"2","name":"Linux servers"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        string csv = Path.Combine(Path.GetTempPath(), $"hosts-{Guid.NewGuid():N}.csv");
        await File.WriteAllTextAsync(csv, "Name,InterfaceIP,Group\nweb-01,10.0.0.1,Linux servers\nweb-02,10.0.0.2,Linux servers\n", TestContext.Current.CancellationToken);
        try
        {
            Run run = shell.Invoke("Import-Csv -LiteralPath $csv | New-ZbxHost -Confirm:$false", new Dictionary<string, object?> { ["csv"] = csv });

            Assert.Empty(run.Errors);
            JsonElement[] created = [.. server.ParamsOf("host.create")];
            Assert.Equal(["web-01", "web-02"], created.Select(c => c.GetProperty("host").GetString()));
            Assert.Equal(["10.0.0.1", "10.0.0.2"], created.Select(c => c.GetProperty("interfaces")[0].GetProperty("ip").GetString()));
            Assert.All(created, c => Assert.Equal("2", c.GetProperty("groups")[0].GetProperty("groupid").GetString()));
        }
        finally
        {
            File.Delete(csv);
        }
    }

    [Fact]
    public async Task A_piped_host_gives_a_new_item_its_id_and_nothing_else()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """[{"hostid":"7","host":"web-07","name":"Web seven","description":"the host's own","status":"1"}]""");
        server.On("item.create", """{"itemids":["70"]}""").On("item.get", """[{"itemid":"70","name":"n"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Id 7 | New-ZbxItem -Name n -Key k -Type Trapper -ValueType Float -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement created = Assert.Single(server.ParamsOf("item.create"));
        Assert.Equal("7", created.GetProperty("hostid").GetString());
        Assert.Equal("n", created.GetProperty("name").GetString());
        Assert.False(created.TryGetProperty("description", out _), created.GetRawText());
        Assert.False(created.TryGetProperty("status", out _), created.GetRawText());
    }

    [Fact]
    public async Task A_piped_record_of_another_object_binds_nothing_and_is_refused()
    {
        // An event's Name is not a host's name: Get-ZbxEvent | New-ZbxHost would have created a host named after it.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("event.get", """[{"eventid":"5","name":"CPU high","severity":"4"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxEvent -Id 5 | New-ZbxHost -Group 2 -Confirm:$false; 'after'");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.StartsWith("InputObjectNotBound,", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Empty(server.ParamsOf("host.create"));
        Assert.Equal("after", run.Output[^1].ToString());
    }

    [Fact]
    public async Task A_piped_hashtable_is_refused_naming_the_cast_that_binds_it()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("@{ Name = 'h1' } | New-ZbxHost -Group 2 -Confirm:$false");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.StartsWith("InputObjectNotBound,", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Contains("[pscustomobject]", error.Exception.Message, StringComparison.Ordinal);
        Assert.Empty(server.ParamsOf("host.create"));
    }

    [Fact]
    public async Task A_csv_row_with_an_id_changes_the_fields_it_names()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("7", "web-07"), ("8", "web-08"))).On("host.update", """{"hostids":["7","8"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("[pscustomobject]@{ HostId = '7'; Description = 'seven' }, [pscustomobject]@{ HostId = '8'; Description = 'eight' } | Set-ZbxHost -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement[] updates = [.. Assert.Single(server.ParamsOf("host.update")).EnumerateArray()];
        Assert.Equal(["7:seven", "8:eight"], updates.Select(u => u.GetProperty("hostid").GetString() + ":" + u.GetProperty("description").GetString()));
    }

    [Fact]
    public async Task A_piped_host_never_sends_its_own_fields_back_as_changes()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """[{"hostid":"7","host":"web-07","name":"Web seven","description":"the host's own","status":"0","tls_connect":"1"}]""");
        server.On("host.update", """{"hostids":["7"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Id 7 | Set-ZbxHost -Status Disabled -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement update = Assert.Single(Assert.Single(server.ParamsOf("host.update")).EnumerateArray());
        Assert.Equal(["hostid", "status"], update.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }
}
