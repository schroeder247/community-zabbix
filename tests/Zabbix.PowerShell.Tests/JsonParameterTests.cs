// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// A field whose JSON shape Zabbix does not fix (schema kind <c>json</c>) is a <see cref="JsonElement"/> parameter,
/// which PowerShell cannot convert to by itself: <see cref="ZabbixJsonAttribute"/> takes every shape a shell writes.
/// </summary>
public sealed class JsonParameterTests
{
    public static TheoryData<string> ObjectForms =>
    [
        "@{ refresh = 30; tags = @('a', 'b') }",
        "[ordered]@{ refresh = 30; tags = @('a', 'b') }",
        "[pscustomobject]@{ refresh = 30; tags = @('a', 'b') }",
        "'{\"refresh\":30,\"tags\":[\"a\",\"b\"]}'",
        "[System.Text.Json.JsonDocument]::Parse('{\"refresh\":30,\"tags\":[\"a\",\"b\"]}').RootElement",
    ];

    [Theory]
    [MemberData(nameof(ObjectForms))]
    public async Task New_ZbxModule_Config_takes_every_shell_shape_of_an_object(string config)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("module.create", """{"moduleids":["5"]}""");
        server.On("module.get", """[{"moduleid":"5","id":"widget","relative_path":"widgets/widget"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke($"New-ZbxModule -ModuleIdentifier widget -RelativePath widgets/widget -Config ({config}) -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement sent = Assert.Single(server.ParamsOf("module.create")).GetProperty("config");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""{"refresh":30,"tags":["a","b"]}"""), JsonNode.Parse(sent.GetRawText())), sent.GetRawText());
    }

    [Fact]
    public async Task Set_ZbxModule_Config_takes_a_hashtable()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("module.update", """{"moduleids":["5"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Set-ZbxModule 5 -Config @{ refresh = 60 } -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Equal("""{"refresh":60}""", Assert.Single(server.ParamsOf("module.update"))[0].GetProperty("config").GetRawText());
    }

    [Theory]
    [InlineData("New-ZbxHostInterface")]
    [InlineData("Set-ZbxHostInterface")]
    public void A_host_interface_command_takes_no_configuration_import_key(string command)
    {
        // hostinterface.create/update let the configuration import's interface_ref and items through their allow-list
        // (CHostInterface.php 8.0:248) and store neither, so neither is a parameter.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"$p = (Get-Command {command}).Parameters.Keys; $p -contains 'Items'; $p -contains 'InterfaceReference'; $p -contains 'Details'");

        Assert.Empty(run.Errors);
        Assert.Equal([false, false, true], run.Output.Select(o => (bool)o.BaseObject));
    }

    [Fact]
    public void New_ZbxHttpTestStep_Posts_takes_a_list_of_name_value_hashtables()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("$posts = (New-ZbxHttpTestStep login https://x/login -StepNumber 1 -Posts @([ordered]@{ name = 'user'; value = 'admin' })).posts; @($posts).Count; $posts[0].name; $posts[0].value");

        Assert.Empty(run.Errors);
        Assert.Equal(["1", "user", "admin"], run.Output.Select(o => o.ToString()));
    }

    [Theory]
    [InlineData("'{not json'")]
    [InlineData("(ConvertTo-SecureString 'x' -AsPlainText -Force)")]
    public void A_value_that_has_no_JSON_form_is_a_binding_error(string config)
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"New-ZbxModule -ModuleIdentifier widget -RelativePath widgets/widget -Config ({config}) -Confirm:$false");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.Contains("ParameterArgumentTransformationError", error.FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public void Every_json_field_parameter_takes_the_transform_and_says_what_it_takes()
    {
        var missing = new List<string>();
        foreach (Type type in ModuleUnderTest.AllCmdlets)
        {
            foreach (System.Reflection.PropertyInfo property in type.GetProperties())
            {
                if (Nullable.GetUnderlyingType(property.PropertyType) != typeof(JsonElement) && property.PropertyType != typeof(JsonElement)) continue;
                if (property.GetCustomAttributes(typeof(ParameterAttribute), true).Length == 0) continue;
                if (property.GetCustomAttributes(typeof(ZabbixJsonAttribute), true).Length == 0) missing.Add($"{type.Name}.{property.Name}");
            }
        }

        Assert.Empty(missing);
    }
}
