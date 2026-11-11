// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// A nested-request collection field whose record carries only an id (<c>-Group</c>,
/// <c>-Template</c>, ...) accepts a raw id, a name (resolved in one <c>.get</c>), or a piped/typed entity object —
/// the module's one <see cref="ZabbixReference{TEntity}"/> currency — on both <c>New-</c> and <c>Set-</c>.
/// <c>-TlsAccept</c> (a <c>[Flags]</c> enum) accepts an
/// array of names, a single name, or a bitmask integer.
/// </summary>
public sealed class ReferenceListParametersTests
{
    [Fact]
    public async Task New_ZbxHost_Groups_accepts_a_raw_id_without_any_hostgroup_get()
    {
        // A common fixture pattern: New-ZbxHost -Group @($groupId) where $groupId is a bare id from an earlier
        // New-ZbxHostGroup call. ResolveAsync short-circuits to zero .get calls when
        // every reference already carries an id, so this costs no round trip.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -Interface @() -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.DoesNotContain(server.Calls, c => c.Method == "hostgroup.get");
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        Assert.Equal("7", Assert.Single(request.GetProperty("groups").EnumerateArray()).GetProperty("groupid").GetString());
    }

    [Fact]
    public async Task New_ZbxHost_Groups_accepts_a_name_resolved_in_one_hostgroup_get()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"7","name":"Web servers"}]""");
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group 'Web servers' -Interface @() -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Equal(1, server.Calls.Count(c => c.Method == "hostgroup.get"));
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        Assert.Equal("7", Assert.Single(request.GetProperty("groups").EnumerateArray()).GetProperty("groupid").GetString());
    }

    [Fact]
    public async Task New_ZbxHost_DisplayName_sets_the_wire_name_field()
    {
        // -DisplayName is the visible name; -Name (alias -Host) is the technical one (TechnicalNameAliasTests).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -Interface @() -DisplayName 'Web One' -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        Assert.Equal("Web One", request.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Set_ZbxHost_Groups_resolves_once_for_the_whole_confirmed_batch_then_updates()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("hostgroup.get", """[{"groupid":"7","name":"Web servers"}]""");
        server.On("host.update", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Set-ZbxHost -InputObject web-01 -Group 'Web servers' -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Equal(1, server.Calls.Count(c => c.Method == "hostgroup.get"));
        JsonElement request = Assert.Single(server.ParamsOf("host.update")).EnumerateArray().Single();
        Assert.Equal("7", Assert.Single(request.GetProperty("groups").EnumerateArray()).GetProperty("groupid").GetString());
    }

    [Fact]
    public async Task New_ZbxHost_TlsAccept_binds_an_array_of_member_names()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -Interface @() -TlsAccept Unencrypted,Psk -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        Assert.Equal(3, request.GetProperty("tls_accept").GetInt32());
    }

    [Fact]
    public async Task New_ZbxHost_TlsAccept_binds_a_bitmask_integer()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -Interface @() -TlsAccept 3 -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        Assert.Equal(3, request.GetProperty("tls_accept").GetInt32());
    }

    // item.get's own hostids/groupids/templateids/... options each have a filter parameter on the generated
    // Get-Zbx<Entity> cmdlets, not only -Filter (the whole typed .get record). -Host/-Group/-Template/... are
    // ZabbixReference<TEntity>[], the same ids/names/objects currency
    // New-/Set-'s nested reference fields already accept — resolved in one .get per referenced entity type, then
    // folded into item.get's own hostids/groupids/... options; -HostId etc. are the id property of a piped record,
    // an alias of the same parameter.

    [Fact]
    public async Task Get_ZbxItem_Host_resolves_the_name_to_hostids_in_one_host_get()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("item.get", """[{"itemid":"55","hostid":"10084","name":"CPU load","key_":"system.cpu.load"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxItem -Host 'web-01'");

        Assert.Empty(run.Errors);
        dynamic item = Assert.Single(run.Output);
        Assert.Equal("55", item.ItemId.ToString());
        Assert.Equal(1, server.Calls.Count(c => c.Method == "host.get"));
        JsonElement request = Assert.Single(server.ParamsOf("item.get"));
        Assert.Equal("""["10084"]""", request.GetProperty("hostids").GetRawText());
    }

    [Fact]
    public async Task Get_ZbxItem_HostId_passes_the_id_straight_through_without_any_host_get()
    {
        // -HostId, the id property of a piped host, is an alias of the same -Host parameter: ResolveAsync
        // short-circuits to zero network calls when every reference already carries an id.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", """[{"itemid":"55","hostid":"10084","name":"CPU load","key_":"system.cpu.load"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxItem -HostId 10084");

        Assert.Empty(run.Errors);
        Assert.DoesNotContain(server.Calls, c => c.Method == "host.get");
        JsonElement request = Assert.Single(server.ParamsOf("item.get"));
        Assert.Equal("""["10084"]""", request.GetProperty("hostids").GetRawText());
    }

    [Fact]
    public async Task Get_ZbxItem_Host_composes_with_Filter_the_reference_parameter_sets_hostids_Filter_sets_the_rest()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", """[{"itemid":"55","hostid":"10084","name":"CPU load","key_":"system.cpu.load"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxItem -HostId 10084 -Filter ([Community.Zabbix.ZabbixItemGetParameters]@{ Monitored = $true })");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("item.get"));
        Assert.Equal("""["10084"]""", request.GetProperty("hostids").GetRawText());
        Assert.True(request.GetProperty("monitored").GetBoolean());
    }

    [Fact]
    public async Task Get_ZbxItem_with_no_reference_parameter_bound_makes_no_extra_get_call()
    {
        // HasReferenceFilters gates the extra resolution round trip: an ordinary Get-ZbxItem call (no -Host/-Group/
        // -Template/... bound) must not pay for one.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", """[{"itemid":"55","hostid":"10084","name":"CPU load","key_":"system.cpu.load"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxItem");

        Assert.Empty(run.Errors);
        Assert.DoesNotContain(server.Calls, c => c.Method == "host.get");
        Assert.Equal(1, server.Calls.Count(c => c.Method == "item.get"));
    }

    [Fact]
    public async Task Get_ZbxHost_piped_into_Get_ZbxItem_binds_HostId_by_property_name()
    {
        // Get-ZbxItem's -Host parameter opts into ValueFromPipelineByPropertyName, not only the alias
        // (-HostId) for a NAMED argument, so the idiom "Get-ZbxHost | Get-ZbxItem" binds the piped
        // ZabbixHost's own HostId property instead of failing with "The input object cannot be bound to any
        // parameters". ZabbixCmdletEmitter emits the flag for every generated ZabbixReference<T>[] filter parameter.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("item.get", """[{"itemid":"55","hostid":"10084","name":"CPU load","key_":"system.cpu.load"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Name web-01 | Get-ZbxItem");

        Assert.Empty(run.Errors);
        JsonElement request = server.ParamsOf("item.get").Last();
        Assert.Equal("""["10084"]""", request.GetProperty("hostids").GetRawText());
    }
}
