// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// Enum member names that keep the readable spellings of the unreleased 26.x module (docs/adr/0001-repository-layout.md):
/// <c>schema/overlay.json</c>'s <c>members</c> overrides for <c>ZabbixScriptType</c>, <c>ZabbixHostStatus</c>,
/// <c>ZabbixServiceStatusCalculation</c>, <c>ZabbixMfaCodeLength</c> and <c>ZabbixScriptScope</c>. The wire values (extracted by
/// <c>tools/ZabbixApiExtract</c>) are untouched; only the C# member names follow that spelling.
/// </summary>
public sealed class EnumMemberNamingTests
{
    [Fact]
    public void ZabbixScriptType_names_the_zero_member_Script_matching_the_shipped_module()
    {
        Assert.Equal("Script", nameof(ZabbixScriptType.Script));
        Assert.Equal(0, (int)ZabbixScriptType.Script);
    }

    [Fact]
    public void ZabbixHostStatus_names_Enabled_and_Disabled_matching_the_shipped_module()
    {
        Assert.Equal(0, (int)ZabbixHostStatus.Enabled);
        Assert.Equal(1, (int)ZabbixHostStatus.Disabled);
        Assert.Equal(3, (int)ZabbixHostStatus.Template);
    }

    [Fact]
    public void ZabbixServiceStatusCalc_names_MostCritical_and_MostCriticalIfAllChildren_matching_the_shipped_module()
    {
        Assert.Equal(0, (int)ZabbixServiceStatusCalculation.SetOk);
        Assert.Equal(1, (int)ZabbixServiceStatusCalculation.MostCriticalIfAllChildren);
        Assert.Equal(2, (int)ZabbixServiceStatusCalculation.MostCritical);
    }

    [Fact]
    public void ZabbixMfaCodeLength_names_Six_and_Eight_matching_the_shipped_module()
    {
        Assert.Equal(6, (int)ZabbixMfaCodeLength.Six);
        Assert.Equal(8, (int)ZabbixMfaCodeLength.Eight);
    }

    [Fact]
    public void ZabbixScriptScope_names_ManualHost_and_ManualEvent_matching_the_shipped_module()
    {
        Assert.Equal(1, (int)ZabbixScriptScope.Action);
        Assert.Equal(2, (int)ZabbixScriptScope.ManualHost);
        Assert.Equal(4, (int)ZabbixScriptScope.ManualEvent);
    }

    [Fact]
    public async Task New_ZbxHost_Status_Disabled_sends_the_unchanged_wire_value_1()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -Status Disabled -Confirm:$false");

        Assert.Empty(run.Errors);
        System.Text.Json.JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        Assert.Equal("1", request.GetProperty("status").GetRawText());
    }
}
