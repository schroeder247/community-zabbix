// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// Cmdlets outside the generated CRUD pattern: the singleton settings objects, <c>Get-ZbxApiInfo</c>, the
/// hand-written <c>Get-</c> cmdlets whose parameters record lacks <c>IZabbixGetParameters</c>,
/// and <c>New-ZbxNetworkDiscoveryCheck</c>.
/// </summary>
public sealed class SingletonAndUnfilteredCommandsTests
{
    [Fact]
    public async Task Get_ZbxHousekeeping_returns_the_one_settings_object()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("housekeeping.get", """{"hk_events_mode":"1","hk_history_global":"0"}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHousekeeping");

        Assert.Empty(run.Errors);
        dynamic settings = Assert.Single(run.Output).BaseObject;
        Assert.True((bool)settings.HousekeepingEventsMode);
    }

    [Fact]
    public async Task Set_ZbxHousekeeping_sends_only_the_bound_fields()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("housekeeping.update", "{}");
        server.On("housekeeping.get", """{"hk_history":"31d"}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Set-ZbxHousekeeping -HousekeepingHistory '31d' -PassThru");

        Assert.Empty(run.Errors);
        var settings = Assert.IsType<ZabbixHousekeepingSettings>(Assert.Single(run.Output).BaseObject);
        Assert.Equal(ZabbixDuration.Parse("31d"), settings.HousekeepingHistory);
        JsonElement request = Assert.Single(server.ParamsOf("housekeeping.update"));
        Assert.Equal("31d", request.GetProperty("hk_history").GetString());
        Assert.False(request.TryGetProperty("hk_trends", out _));
    }

    [Fact]
    public async Task Set_ZbxHousekeeping_without_PassThru_writes_nothing_and_reads_nothing_back()
    {
        // Set-ZbxHousekeeping is a sink and writes nothing without -PassThru (Learn, "Support the PassThru Parameter").
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("housekeeping.update", "{}");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Set-ZbxHousekeeping -HousekeepingHistory '31d'");

        Assert.Empty(run.Errors);
        Assert.Empty(run.Output);
        Assert.Empty(server.ParamsOf("housekeeping.get"));
    }

    [Fact]
    public async Task Get_ZbxAuthentication_returns_the_one_settings_object()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("authentication.get", """{"authentication_type":"0"}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxAuthentication");

        Assert.Empty(run.Errors);
        dynamic settings = Assert.Single(run.Output).BaseObject;
        Assert.Equal(ZabbixAuthenticationType.Internal, (ZabbixAuthenticationType)settings.AuthenticationType);
    }

    [Fact]
    public async Task Set_ZbxAuthentication_sends_only_the_bound_fields()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("authentication.update", "{}");
        server.On("authentication.get", """{"mfa_status":"1"}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Set-ZbxAuthentication -MfaStatus $true -PassThru");

        Assert.Empty(run.Errors);
        dynamic settings = Assert.Single(run.Output).BaseObject;
        Assert.True((bool)settings.MfaStatus);
        JsonElement request = Assert.Single(server.ParamsOf("authentication.update"));
        Assert.Equal("1", request.GetProperty("mfa_status").GetString());
        Assert.False(request.TryGetProperty("ldap_auth_enabled", out _));
    }

    [Fact]
    public async Task Get_ZbxAutoregistration_returns_the_one_settings_object()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("autoregistration.get", """{"tls_accept":"1"}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxAutoregistration");

        Assert.Empty(run.Errors);
        Assert.Single(run.Output);
    }

    [Fact]
    public async Task Set_ZbxAutoregistration_decodes_the_secure_string_psk()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("autoregistration.update", "{}");
        server.On("autoregistration.get", """{"tls_psk_identity":"auto-id"}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke(
            "Set-ZbxAutoregistration -TlsPskIdentity 'auto-id' -TlsPsk (ConvertTo-SecureString 'deadbeef' -AsPlainText -Force)");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("autoregistration.update"));
        Assert.Equal("auto-id", request.GetProperty("tls_psk_identity").GetString());
        Assert.Equal("deadbeef", request.GetProperty("tls_psk").GetString());
    }

    [Fact]
    public async Task Set_ZbxAutoregistration_TlsAccept_ORs_an_array_of_modes_onto_one_bitmask_field()
    {
        // -TlsAccept is an array (ZabbixTlsAcceptModes[]) so both an array literal and a comma string bind
        // to it; BuildRequest ORs every given mode onto the request's single [Flags] field.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("autoregistration.update", "{}");
        server.On("autoregistration.get", """{"tls_accept":3}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Set-ZbxAutoregistration -TlsAccept Unencrypted,Psk");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("autoregistration.update"));
        Assert.Equal(3, request.GetProperty("tls_accept").GetInt32());
    }

    [Fact]
    public async Task Set_ZbxSetting_still_takes_the_typed_Request_cast_from_a_hashtable()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("settings.update", "{}");
        server.On("settings.get", """{"work_period":"1-5,09:00-18:00"}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Set-ZbxSetting ([Community.Zabbix.ZabbixSettingsUpdateRequest]@{ WorkPeriod = '1-5,09:00-18:00' })");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("settings.update"));
        Assert.Equal("1-5,09:00-18:00", request.GetProperty("work_period").GetString());
    }

    [Fact]
    public async Task Get_ZbxApiInfo_carries_the_version_and_connection_details()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("apiinfo.version", "\"7.0.31\"");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxApiInfo");

        Assert.Empty(run.Errors);
        dynamic info = Assert.Single(run.Output).BaseObject;
        Assert.Equal(new Version(7, 0, 31), info.Version);
        Assert.Equal(server.Endpoint, info.Endpoint);
    }

    [Fact]
    public async Task Get_ZbxGraphItem_by_id_sends_graphids()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("graphitem.get", """[{"gitemid":"1","itemid":"501"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxGraphItem -Id 42");

        Assert.Empty(run.Errors);
        Assert.Single(run.Output);
        JsonElement request = Assert.Single(server.ParamsOf("graphitem.get"));
        Assert.Equal("42", Assert.Single(request.GetProperty("graphids").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Get_ZbxGraphItem_by_ItemId_sends_itemids()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("graphitem.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxGraphItem -ItemId 501");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("graphitem.get"));
        Assert.Equal("501", Assert.Single(request.GetProperty("itemids").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Get_ZbxGraphItem_SelectGraphs_extends_the_related_graphs()
    {
        // The -SelectGraphs switch.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("graphitem.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxGraphItem -Id 42 -SelectGraphs");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("graphitem.get"));
        Assert.Equal("extend", request.GetProperty("selectGraphs").GetString());
    }

    [Fact]
    public async Task Get_ZbxHaNode_filters_by_status()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hanode.get", """[{"ha_nodeid":"1","name":"node-1","status":"3"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHaNode -Status Active");

        Assert.Empty(run.Errors);
        Assert.Single(run.Output);
        JsonElement request = Assert.Single(server.ParamsOf("hanode.get"));
        Assert.Equal(3, request.GetProperty("filter").GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task Get_ZbxHaNode_takes_cuid_ids_as_strings()
    {
        // An HA node's id is a CUID (ZBX_TYPE_CUID), a 25-character string: -Id takes it as text and sends it unchanged.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hanode.get", """[{"ha_nodeid":"ckr8jg5sa0000k6y5q11vcd2h","name":"node-1","status":"3"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHaNode -Id ckr8jg5sa0000k6y5q11vcd2h");

        Assert.Empty(run.Errors);
        Assert.Equal("ckr8jg5sa0000k6y5q11vcd2h", Assert.Single(run.Output).Properties["HANodeId"].Value);
        JsonElement request = Assert.Single(server.ParamsOf("hanode.get"));
        Assert.Equal("ckr8jg5sa0000k6y5q11vcd2h", Assert.Single(request.GetProperty("ha_nodeids").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Get_ZbxAuditLog_selects_by_user_and_has_no_numeric_id_parameter()
    {
        // An audit record's own id is a CUID string, so the command has no -Id set; its user is a reference parameter,
        // not the record's key (the first numeric id in the record is the user's, and must not be mistaken for the key).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("auditlog.get", """[{"auditid":"ckr8jg5sa0000k6y5q11vcd2h","userid":"5","username":"Admin","clock":"1791589500","action":"8"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxAuditLog -User 5");
        Run parameters = shell.Invoke("(Get-Command Get-ZbxAuditLog).Parameters.Keys -contains 'Id'");

        Assert.Empty(run.Errors);
        Assert.Equal("ckr8jg5sa0000k6y5q11vcd2h", Assert.Single(run.Output).Properties["AuditId"].Value);
        JsonElement request = Assert.Single(server.ParamsOf("auditlog.get"));
        Assert.Equal("5", Assert.Single(request.GetProperty("userids").EnumerateArray()).GetString());
        Assert.False((bool)Assert.Single(parameters.Output).BaseObject);
    }

    [Fact]
    public async Task Get_ZbxHttpTest_resolves_the_host_then_gets_by_hostid()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """[{"hostid":"10084","host":"web-01"}]""");
        server.On("httptest.get", """[{"httptestid":"1","name":"scenario-1"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHttpTest -Host web-01");

        Assert.Empty(run.Errors);
        Assert.Single(run.Output);
        JsonElement request = Assert.Single(server.ParamsOf("httptest.get"));
        Assert.Equal("10084", Assert.Single(request.GetProperty("hostids").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Get_ZbxTask_by_id_sends_taskids()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("task.get", """[{"taskid":"7","status":"1"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxTask -Id 7");

        Assert.Empty(run.Errors);
        Assert.Single(run.Output);
        JsonElement request = Assert.Single(server.ParamsOf("task.get"));
        Assert.Equal("7", Assert.Single(request.GetProperty("taskids").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Get_ZbxTrend_resolves_the_item_then_sends_the_time_window()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", """[{"itemid":"501","key_":"agent.ping","name":"agent.ping","hostid":"10084","hosts":[{"hostid":"10084","host":"web-01"}]}]""");
        server.On("trend.get", """[{"itemid":"501","clock":"1700000000","value_avg":"1.5"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxTrend agent.ping -From ([DateTimeOffset]::FromUnixTimeSeconds(1690000000)) -Till ([DateTimeOffset]::FromUnixTimeSeconds(1700000000)) -Limit 10");

        Assert.Empty(run.Errors);
        Assert.Single(run.Output);
        JsonElement request = Assert.Single(server.ParamsOf("trend.get"));
        Assert.Equal("501", Assert.Single(request.GetProperty("itemids").EnumerateArray()).GetString());
        Assert.Equal(1690000000, request.GetProperty("time_from").GetInt64());
        Assert.Equal(1700000000, request.GetProperty("time_till").GetInt64());
        Assert.Equal(10, request.GetProperty("limit").GetInt32());
    }

    [Fact]
    public void New_ZbxNetworkDiscoveryCheck_builds_an_icmp_check_with_no_key()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("New-ZbxNetworkDiscoveryCheck -Type Icmp");

        Assert.Empty(run.Errors);
        var check = Assert.IsType<ZabbixNetworkDiscoveryCheckRequest>(Assert.Single(run.Output).BaseObject);
        Assert.Equal(ZabbixNetworkDiscoveryCheckType.Icmp, check.Type);
        Assert.Equal(0, check.Unique);
        Assert.Null(check.Key);
    }

    [Fact]
    public void New_ZbxNetworkDiscoveryCheck_snmpv2c_sets_oid_community_and_port()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("New-ZbxNetworkDiscoveryCheck -Type SnmpV2c -SnmpOid 'sysDescr.0' -SnmpCommunity public -Port 161 -Unique");

        Assert.Empty(run.Errors);
        var check = Assert.IsType<ZabbixNetworkDiscoveryCheckRequest>(Assert.Single(run.Output).BaseObject);
        Assert.Equal(ZabbixNetworkDiscoveryCheckType.SnmpV2c, check.Type);
        Assert.Equal("sysDescr.0", check.Key);
        Assert.Equal("public", check.SnmpCommunity);
        Assert.Equal("161", check.Ports);
        Assert.Equal(1, check.Unique);
    }

    [Fact]
    public void New_ZbxNetworkDiscoveryCheck_Ports_range_takes_precedence_over_Port()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("New-ZbxNetworkDiscoveryCheck -Type Tcp -Port 22 -Ports '22-23'");

        Assert.Empty(run.Errors);
        Assert.Equal("22-23", Assert.IsType<ZabbixNetworkDiscoveryCheckRequest>(Assert.Single(run.Output).BaseObject).Ports);
    }

    [Fact]
    public void New_ZbxNetworkDiscoveryCheck_Properties_escape_hatch_wins_last()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("New-ZbxNetworkDiscoveryCheck -Type ZabbixAgent -Key agent.ping -AdditionalProperty @{ allow_redirect = 1; key_ = 'agent.version' }");

        Assert.Empty(run.Errors);
        var check = Assert.IsType<ZabbixNetworkDiscoveryCheckRequest>(Assert.Single(run.Output).BaseObject);
        Assert.Equal(1, check.AllowRedirect);
        Assert.Equal("agent.version", check.Key);
    }

    [Fact]
    public void New_ZbxNetworkDiscoveryCheck_Properties_that_do_not_fit_are_one_terminating_parameter_error()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("try { New-ZbxNetworkDiscoveryCheck -Type ZabbixAgent -AdditionalProperty @{ uniq = 'not a number' } } catch { 'caught' }");

        Assert.Equal("caught", Assert.Single(run.Output).ToString());
    }

    [Fact]
    public void New_ZbxNetworkDiscoveryCheck_default_view_never_shows_a_passphrase()
    {
        // The check carries the SNMPv3 passphrases for the wire (the SDK request's fields are strings); what a console
        // prints of it, list or table, masks them.
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("""
            $s = ConvertTo-SecureString 'hunter2-auth' -AsPlainText -Force
            $p = ConvertTo-SecureString 'hunter2-priv' -AsPlainText -Force
            $c = New-ZbxNetworkDiscoveryCheck -Type SnmpV3 -SnmpOid sysName.0 -SnmpAuthenticationPassphrase $s -SnmpPrivacyPassphrase $p
            $c | Out-String
            $c | Format-List | Out-String
            """);

        Assert.Empty(run.Errors);
        Assert.All(run.Output, text => Assert.DoesNotContain("hunter2", text.ToString(), StringComparison.Ordinal));
        Assert.Contains("********", run.Output[0].ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("-Check @(@{ type = 9; key_ = 'system.uname'; ports = 10050 })")]
    [InlineData("-Check @{ type = 9; key_ = 'system.uname'; ports = '10050' }")]
    [InlineData("-Check (New-ZbxNetworkDiscoveryCheck -Type ZabbixAgent -Key system.uname -Port 10050)")]
    public async Task New_ZbxNetworkDiscoveryRule_Checks_binds_a_hashtable_or_the_builders_output(string checks)
    {
        // -Check binds a hashtable and the builder's output alike (a bare JsonElement parameter binds neither).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("drule.create", """{"druleids":["3"]}""");
        server.On("drule.get", """[{"druleid":"3","name":"next-rule"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke($"New-ZbxNetworkDiscoveryRule -Name next-rule -IPRange 10.0.0.1-10 {checks} -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement check = Assert.Single(Assert.Single(server.ParamsOf("drule.create")).GetProperty("dchecks").EnumerateArray());
        Assert.Equal(9, check.GetProperty("type").GetInt32());
        Assert.Equal("system.uname", check.GetProperty("key_").GetString());
        Assert.Equal("10050", check.GetProperty("ports").GetString());
    }
}
