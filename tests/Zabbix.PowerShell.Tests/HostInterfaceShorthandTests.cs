// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// <c>New-ZbxHost</c> builds exactly one
/// <c>HostInterfaceCreateRequest</c> inline from <c>-InterfaceType</c>/<c>-InterfaceIP</c>/<c>-InterfaceDns</c>/
/// <c>-InterfacePort</c>/<c>-UseIP</c>, the shorthand for a host with a single interface. The generator
/// (<c>ZabbixCmdletEmitter</c>) emits these
/// five shorthand parameters on <c>New-ZbxHost</c> and merges the interface they build into <c>-Interface</c>.
/// </summary>
public sealed class HostInterfaceShorthandTests
{
    [Fact]
    public async Task New_ZbxHost_InterfaceType_shorthand_builds_and_sends_one_interface()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        // The single-interface shorthand as a script writes it.
        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -InterfaceType Agent -InterfaceIP '127.0.0.1' -InterfacePort '10050' -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        JsonElement iface = Assert.Single(request.GetProperty("interfaces").EnumerateArray());
        Assert.Equal(1, iface.GetProperty("type").GetInt32());
        Assert.Equal("127.0.0.1", iface.GetProperty("ip").GetString());
        Assert.Equal("10050", iface.GetProperty("port").GetString());
        Assert.Equal(1, iface.GetProperty("useip").GetInt32());
        Assert.Equal(1, iface.GetProperty("main").GetInt32());
    }

    [Fact]
    public async Task New_ZbxHost_UseIp_defaults_true_matching_the_shipped_module()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -InterfaceType Agent -InterfaceIP '10.0.0.1' -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        JsonElement iface = Assert.Single(request.GetProperty("interfaces").EnumerateArray());
        Assert.Equal(1, iface.GetProperty("useip").GetInt32());
        // Port not bound and type not Snmp: defaults to the agent port.
        Assert.Equal("10050", iface.GetProperty("port").GetString());
    }

    [Fact]
    public async Task New_ZbxHost_UseIp_false_is_honored_when_explicitly_passed()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -InterfaceType Agent -InterfaceDns 'web-01.example' -UseIP:$false -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        JsonElement iface = Assert.Single(request.GetProperty("interfaces").EnumerateArray());
        Assert.Equal(0, iface.GetProperty("useip").GetInt32());
        Assert.Equal("web-01.example", iface.GetProperty("dns").GetString());
    }

    [Fact]
    public async Task New_ZbxHost_InterfaceDns_alone_connects_by_DNS()
    {
        // -InterfaceDns alone connects by DNS, with no -UseIP:$false: otherwise the server creates the interface with
        // useip=1 on 127.0.0.1 (on 7.0.31 and 8.0.0).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -InterfaceDns 'web-01.example' -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement iface = Assert.Single(Assert.Single(server.ParamsOf("host.create")).GetProperty("interfaces").EnumerateArray());
        Assert.Equal(0, iface.GetProperty("useip").GetInt32());
        Assert.Equal("web-01.example", iface.GetProperty("dns").GetString());
        Assert.Equal(string.Empty, iface.GetProperty("ip").GetString());
    }

    [Fact]
    public async Task New_ZbxHost_Snmp_interface_defaults_port_161_when_not_bound()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -InterfaceType Snmp -InterfaceIP '10.0.0.1' -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        JsonElement iface = Assert.Single(request.GetProperty("interfaces").EnumerateArray());
        Assert.Equal(2, iface.GetProperty("type").GetInt32());
        Assert.Equal("161", iface.GetProperty("port").GetString());
    }

    [Fact]
    public async Task New_ZbxHost_shorthand_is_additive_to_an_explicit_Interfaces_array()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            $existing = [Community.Zabbix.ZabbixHostInterfaceRequest]@{ Type = 4; Ip = '10.0.0.2'; Port = '12345'; Useip = 1; Main = 1 }
            New-ZbxHost -Host web-01 -Group @('7') -Interface @($existing) -InterfaceType Agent -InterfaceIP '127.0.0.1' -InterfacePort '10050' -Confirm:$false
            """);

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        Assert.Equal(2, request.GetProperty("interfaces").GetArrayLength());
    }

    [Fact]
    public async Task New_ZbxHost_with_no_shorthand_parameter_bound_sends_Interfaces_unchanged()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -Interface @() -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        Assert.Equal(0, request.GetProperty("interfaces").GetArrayLength());
    }

    [Theory]
    [InlineData("New-ZbxHostInterface 10084 -Type Agent -UseIP IP -IP 10.0.0.1 -Port 10050 -Main 1 -Confirm:$false", "ip", "10.0.0.1", "dns")]
    [InlineData("New-ZbxHostInterface 10084 -Type Agent -UseIP Dns -Dns web.example -Port 10050 -Main 1 -Confirm:$false", "dns", "web.example", "ip")]
    public async Task New_ZbxHostInterface_needs_only_the_address_it_connects_by(string script, string given, string value, string empty)
    {
        // An IP interface needs no -Dns '' and a DNS one no -IP '' (both required by the server, on 7.0.31 and
        // 8.0.0, so the cmdlet fills the empty one).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostinterface.create", """{"interfaceids":["5"]}""").On("hostinterface.get", """[{"interfaceid":"5","hostid":"10084"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke(script);

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("hostinterface.create"));
        Assert.Equal(value, request.GetProperty(given).GetString());
        Assert.Equal(string.Empty, request.GetProperty(empty).GetString());
    }
}
