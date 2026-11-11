// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Reflection;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// A field the API marks required is not <c>Mandatory=true</c> on a generated
/// <c>New-</c>/<c>Set-</c> parameter (every one of them also carries <c>-AdditionalProperty</c>, an
/// escape hatch that can always supply it instead: <c>New-ZbxProxy</c> takes <c>operating_mode</c> as
/// <c>-AdditionalProperty @{ operating_mode = '0' }</c>, never <c>-OperatingMode</c>). <c>CreateAsync</c>/<c>BuildRequest</c>
/// validates "required, once -AdditionalProperty is merged" instead, naming the missing field.
/// </summary>
public sealed class RequiredFieldEscapeHatchTests
{
    [Fact]
    public void No_generated_New_or_Set_parameter_is_Mandatory_for_an_API_required_field()
    {
        // Sweep: every generated New-/Set- parameter (Properties is always available) is optional at the PowerShell
        // parameter-binding level, whatever the wire's own "required" flag says: ZabbixCmdletEmitter.EmitParameter
        // does not mark these Mandatory=true. InputObject (Set-'s own reference parameter) is exempt:
        // it is not one of Param()'s emitted request-field parameters.
        Type[] generatedNewOrSet = [.. typeof(ZabbixCmdlet).Assembly.GetExportedTypes()
            .Where(type => !type.IsAbstract && type.Namespace == "Community.Zabbix.PowerShell.Commands.Generated"
                && type.BaseType is { IsGenericType: true } baseType
                && (baseType.GetGenericTypeDefinition() == typeof(ZabbixNewCmdlet<>) || baseType.GetGenericTypeDefinition() == typeof(ZabbixSetCmdlet<,>)))];
        Assert.NotEmpty(generatedNewOrSet);

        foreach (Type command in generatedNewOrSet)
        {
            foreach (PropertyInfo property in command.GetProperties())
            {
                if (property.Name is "InputObject" or "Force" or "PassThru" or "AdditionalProperty") continue;
                ParameterAttribute? parameter = property.GetCustomAttribute<ParameterAttribute>();
                if (parameter is null) continue;
                Assert.False(parameter.Mandatory, $"{command.Name}.{property.Name} is Mandatory=true, but a generated New- or Set- takes every field through -AdditionalProperty too, so no field parameter is mandatory");
            }
        }
    }

    [Fact]
    public async Task New_ZbxProxy_OperatingMode_supplied_only_via_Properties_reaches_the_wire()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("proxy.create", """{"proxyids":["10"]}""");
        server.On("proxy.get", """[{"proxyid":"10","name":"proxy1","operating_mode":"0"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxProxy -Name proxy1 -AdditionalProperty @{ operating_mode = '0' } -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("proxy.create"));
        Assert.Equal("0", request.GetProperty("operating_mode").GetRawText());
    }

    [Fact]
    public async Task New_ZbxProxy_without_OperatingMode_or_Properties_fails_with_a_clear_message()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxProxy -Name proxy1 -Confirm:$false");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.Contains("operating_mode", error.Exception.Message, StringComparison.Ordinal);
        Assert.Contains("OperatingMode", error.Exception.Message, StringComparison.Ordinal);
        Assert.Empty(server.ParamsOf("proxy.create"));
    }

    [Fact]
    public async Task New_ZbxProxy_OperatingMode_bound_by_name_still_works_without_Properties()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("proxy.create", """{"proxyids":["10"]}""");
        server.On("proxy.get", """[{"proxyid":"10","name":"proxy1","operating_mode":"1"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxProxy -Name proxy1 -OperatingMode Passive -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("proxy.create"));
        Assert.Equal("1", request.GetProperty("operating_mode").GetRawText());
    }
}
