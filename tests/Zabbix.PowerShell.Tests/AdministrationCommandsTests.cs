// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>The administration commands: settings, SLI, user directory test, user provisioning, scripts.</summary>
public sealed class AdministrationCommandsTests
{
    [Fact]
    public async Task Get_ZbxSetting_reads_the_typed_record()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("settings.get", """{"work_period":"1-5,09:00-18:00"}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxSetting");

        Assert.Empty(run.Errors);
        Assert.Equal("1-5,09:00-18:00", ((ZabbixSettingsRecord)Assert.Single(run.Output).BaseObject).WorkPeriod);
    }

    [Fact]
    public async Task Set_ZbxSetting_sends_the_typed_request_cast_from_a_hashtable()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("settings.update", """["default_theme"]""");                // CSettings::update: the names it updated
        server.On("settings.get", """{"work_period":"1-5,09:00-18:00"}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""Set-ZbxSetting ([Community.Zabbix.ZabbixSettingsUpdateRequest]@{ WorkPeriod = '1-5,09:00-18:00' })""");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("settings.update"));
        Assert.Equal("1-5,09:00-18:00", request.GetProperty("work_period").GetString());
    }

    [Fact]
    public async Task Get_ZbxSli_resolves_the_sla_then_asks_for_its_report()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("sla.get", """[{"slaid":"3","name":"Gold"}]""");
        server.On("sla.getsli", """{"periods":[{"period_from":0,"period_to":1}],"serviceids":["5"],"sli":[[{"sli":99.9,"uptime":100,"downtime":0,"error_budget":0,"excluded_downtimes":[]}]]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxSli Gold");

        Assert.Empty(run.Errors);
        var report = Assert.IsType<ZabbixSliReport>(Assert.Single(run.Output).BaseObject);
        Assert.Equal((99.9, 5UL, TimeSpan.FromSeconds(100)), (report.Sli![0][0].Sli!.Value, report.ServiceIds![0], report.Sli[0][0].Uptime!.Value));
        JsonElement asked = Assert.Single(server.ParamsOf("sla.getsli"));
        Assert.Equal("3", asked.GetProperty("slaid").GetString());
    }

    [Fact]
    public async Task Test_ZbxUserDirectory_sends_the_typed_request_cast_from_a_hashtable()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("userdirectory.test", """{"username":"probe"}""");             // CUserDirectory::test: the test user
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            Test-ZbxUserDirectory ([Community.Zabbix.ZabbixUserDirectoryTestRequest]@{
                Host = 'ldap.example'; Port = '389'; BaseDN = 'dc=example'; SearchAttribute = 'uid'
                TestUserName = 'probe'; TestPassword = 'secret'
            })
            """);

        Assert.Empty(run.Errors);
        Assert.Equal("probe", Assert.IsType<ZabbixUserDirectoryTestResult>(Assert.Single(run.Output).BaseObject).UserName);
        JsonElement asked = Assert.Single(server.ParamsOf("userdirectory.test"));
        Assert.Equal("ldap.example", asked.GetProperty("host").GetString());
    }

    [Fact]
    public async Task Invoke_ZbxUserProvision_resolves_and_provisions()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("user.get", """[{"userid":"5","username":"alice"}]""");
        server.On("user.provision", """{"userids":["5"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Invoke-ZbxUserProvision alice -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Equal("5", Assert.Single(Assert.Single(server.ParamsOf("user.provision")).EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Reset_ZbxUserTotp_resolves_and_resets()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("user.get", """[{"userid":"5","username":"alice"}]""");
        server.On("user.resettotp", """{"userids":["5"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Reset-ZbxUserTotp alice -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Single(server.ParamsOf("user.resettotp"));
    }

    [Fact]
    public async Task Unblock_ZbxUser_resolves_and_unblocks()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("user.get", """[{"userid":"5","username":"alice"}]""");
        server.On("user.unblock", """{"userids":["5"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Unblock-ZbxUser alice -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Single(server.ParamsOf("user.unblock"));
    }

    [Fact]
    public async Task Invoke_ZbxScript_resolves_the_script_and_the_host_then_executes()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("script.get", """[{"scriptid":"12","name":"restart"}]""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("script.execute", """{"response":"success","value":"restarted","debug":null}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Invoke-ZbxScript restart -Host web-01 -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Equal("restarted", Assert.IsType<ZabbixScriptExecuteResult>(Assert.Single(run.Output).BaseObject).Value);
        JsonElement asked = Assert.Single(server.ParamsOf("script.execute"));
        Assert.Equal("12", asked.GetProperty("scriptid").GetString());
        Assert.Equal("10084", asked.GetProperty("hostid").GetString());
    }
}
