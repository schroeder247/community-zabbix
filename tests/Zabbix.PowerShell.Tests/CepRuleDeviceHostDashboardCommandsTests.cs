// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The generated commands of <c>ceprule</c> (8.0), <c>device</c> (8.0, read only: its other methods are the mobile
/// app's enrolment) and <c>hostdashboard</c> (read only, and only for given hosts: <c>hostdashboard.get</c> requires
/// <c>hostids</c>).
/// </summary>
public sealed class CepRuleDeviceHostDashboardCommandsTests
{
    [Theory]
    [InlineData("Get-ZbxCepRule")]
    [InlineData("New-ZbxCepRule")]
    [InlineData("Set-ZbxCepRule")]
    [InlineData("Remove-ZbxCepRule")]
    [InlineData("Get-ZbxDevice")]
    [InlineData("Get-ZbxHostDashboard")]
    public void The_command_is_exported_by_the_module_Zabbix(string command)
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"(Get-Command {command}).ModuleName");

        Assert.Empty(run.Errors);
        Assert.Equal("Community.Zabbix", Assert.Single(run.Output).ToString());
    }

    [Fact]
    public async Task New_ZbxCepRule_creates_a_rule_on_8_0()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("apiinfo.version", "\"8.0.0\"");
        server.On("ceprule.create", """{"cep_ruleids":["3"]}""");
        server.On("ceprule.get", """[{"cep_ruleid":"3","name":"burst","sortorder":1}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxCepRule burst 1 -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Equal("burst", Assert.IsType<ZabbixCepRule>(Assert.Single(run.Output).BaseObject).Name);
        JsonElement sent = Assert.Single(server.ParamsOf("ceprule.create"));
        Assert.Equal("burst", sent.GetProperty("name").GetString());
        Assert.Equal(1, sent.GetProperty("sortorder").GetInt32());
    }

    [Fact]
    public async Task Get_ZbxDevice_reads_the_devices_on_8_0()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("apiinfo.version", "\"8.0.0\"");
        server.On("device.get", """[{"deviceid":"4","name":"phone"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxDevice -Name phone");

        Assert.Empty(run.Errors);
        Assert.Equal(4UL, Assert.IsType<ZabbixDevice>(Assert.Single(run.Output).BaseObject).DeviceId);
    }

    [Fact]
    public async Task Get_ZbxHostDashboard_sends_the_hosts_it_is_given()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostdashboard.get", """[{"dashboardid":"9","hostid":"10084","name":"Overview"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHostDashboard -Host 10084");

        Assert.Empty(run.Errors);
        Assert.Equal(9UL, Assert.IsType<ZabbixHostDashboard>(Assert.Single(run.Output).BaseObject).DashboardId);
        Assert.Equal("""["10084"]""", Assert.Single(server.ParamsOf("hostdashboard.get")).GetProperty("hostids").GetRawText());
    }

    [Fact]
    public async Task Get_ZbxHostDashboard_takes_a_piped_host_by_its_HostId()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("hostdashboard.get", """[{"dashboardid":"9","hostid":"10084","name":"Overview"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost web-01 | Get-ZbxHostDashboard");

        Assert.Empty(run.Errors);
        Assert.Single(run.Output);
        Assert.Equal("""["10084"]""", Assert.Single(server.ParamsOf("hostdashboard.get")).GetProperty("hostids").GetRawText());
    }

    [Theory]
    [InlineData("All", true)]
    [InlineData("ById", true)]
    [InlineData("ByName", true)]
    [InlineData("ByFilter", true)]
    public void Get_ZbxHostDashboard_requires_the_hosts_in_every_parameter_set(string set, bool mandatory)
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"((Get-Command Get-ZbxHostDashboard).ParameterSets | Where-Object Name -eq '{set}').Parameters | Where-Object Name -eq HostReference | ForEach-Object IsMandatory");

        Assert.Empty(run.Errors);
        Assert.Equal(mandatory, Assert.Single(run.Output).BaseObject);
    }
}
