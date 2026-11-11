// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Community.Zabbix.PowerShell.Commands;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The per-session agent-host bookkeeping: <c>Initialize-/Publish-/Get-/Clear-ZbxAgentHost</c>
/// and <c>Export-ZbxAgentTemplate</c>, a running agent session's own host record, never a process-wide static.
/// </summary>
public sealed class AgentHostCommandsTests
{
    private const string CreatedHost = """{"hostids":["100"]}""";
    private const string CreatedGroup = """{"groupids":["50"]}""";
    private static string FullHost(string hostId, params (string ItemId, string Key)[] items)
    {
        string itemsJson = string.Join(',', items.Select(i => $$"""{"itemid":"{{i.ItemId}}","key_":"{{i.Key}}","hostid":"{{hostId}}"}"""));
        return $$"""[{"hostid":"{{hostId}}","host":"web-01","name":"web-01","items":[{{itemsJson}}],"interfaces":[{"interfaceid":"10","hostid":"{{hostId}}","type":1}]}]""";
    }

    private static int ScriptItemCreate(FakeZabbix server, string startId)
    {
        int next = int.Parse(startId, System.Globalization.CultureInfo.InvariantCulture);
        server.On("item.create", _ => $$"""{"itemids":["{{next++}}"]}""");
        return next;
    }

    [Fact]
    public async Task Initialize_ZbxAgentHost_creates_the_host_interface_and_one_item_per_handler_key()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", parameters => parameters.TryGetProperty("hostids", out _)
            ? FullHost("100", ("1", "agent.ping"), ("2", "agent.version"), ("3", "agent.hostname"), ("4", "system.uname"))
            : "[]");
        server.On("hostgroup.get", "[]");
        server.On("hostgroup.create", CreatedGroup);
        server.On("host.create", CreatedHost);
        server.On("hostinterface.get", """[{"interfaceid":"10","hostid":"100","type":1}]""");
        server.On("item.get", "[]");
        ScriptItemCreate(server, "1");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false");
        try
        {
            Run run = shell.Invoke("Initialize-ZbxAgentHost probe -Confirm:$false -PassThru");

            Assert.Empty(run.Errors);
            var status = Assert.IsType<ZabbixAgentHostStatus>(Assert.Single(run.Output).BaseObject);
            Assert.Equal(100UL, status.Host.HostId);
            Assert.Equal(4, status.Items.Count);
            Assert.All(status.Items, item => Assert.True(item.HasLocalHandler));

            Assert.Contains(server.Calls, call => call.Method == "host.create");
            Assert.Equal(4, server.Calls.Count(call => call.Method == "item.create"));
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public async Task Initialize_ZbxAgentHost_polls_a_passive_session_on_its_listener_through_the_interface_it_creates()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", "[]");
        server.On("hostgroup.get", "[]");
        server.On("hostgroup.create", CreatedGroup);
        server.On("host.create", CreatedHost);
        server.On("hostinterface.get", """[{"interfaceid":"10","hostid":"100","type":1}]""");
        server.On("item.get", "[]");
        ScriptItemCreate(server, "1");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        var session = (ZabbixAgentSession)shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false -PassThru").Output[0].BaseObject;
        int port = ((System.Net.IPEndPoint)session.Listener!.LocalEndPoint!).Port;
        try
        {
            Run run = shell.Invoke("Initialize-ZbxAgentHost probe -InterfaceAddress 'agents.example' -Confirm:$false");

            Assert.Empty(run.Errors);
            JsonElement agent = Assert.Single(Assert.Single(server.ParamsOf("host.create")).GetProperty("interfaces").EnumerateArray());
            Assert.Equal("agents.example", agent.GetProperty("dns").GetString());
            Assert.Equal("", agent.GetProperty("ip").GetString());
            Assert.Equal(0, agent.GetProperty("useip").GetInt32());
            Assert.Equal(port.ToString(System.Globalization.CultureInfo.InvariantCulture), agent.GetProperty("port").GetString());
            JsonElement[] items = [.. server.ParamsOf("item.create")];
            Assert.Equal(4, items.Length);
            Assert.All(items, item =>
            {
                Assert.Equal("10", item.GetProperty("interfaceid").GetString());
                Assert.Equal(0, item.GetProperty("type").GetInt32());
            });
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Theory]
    [InlineData("", 4)]
    [InlineData(" -ValueType Unsigned", 3)]
    public async Task Initialize_ZbxAgentHost_gives_the_built_in_keys_their_own_value_types_and_a_handler_key_ValueType(string valueType, int handlerValueType)
    {
        // agent.version answers "7.4.0": as Numeric (unsigned) the server turns the item unsupported. The built-in keys
        // are created as Zabbix's own agent templates create them, whatever -ValueType says; a handler key gets
        // -ValueType, Text (4) when omitted, which stores any answer.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", "[]");
        server.On("hostgroup.get", "[]");
        server.On("hostgroup.create", CreatedGroup);
        server.On("host.create", CreatedHost);
        server.On("hostinterface.get", """[{"interfaceid":"10","hostid":"100","type":1}]""");
        server.On("item.get", "[]");
        ScriptItemCreate(server, "1");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false");
        try
        {
            shell.Invoke("Register-ZbxPassiveAgentHandler probe -Key 'app.health' -ScriptBlock { 'ok' }");
            Run run = shell.Invoke("Initialize-ZbxAgentHost probe -Confirm:$false" + valueType);

            Assert.Empty(run.Errors);
            Dictionary<string, int> valueTypes = server.ParamsOf("item.create").ToDictionary(
                item => item.GetProperty("key_").GetString()!, item => item.GetProperty("value_type").GetInt32(), StringComparer.Ordinal);
            Assert.Equal(3, valueTypes["agent.ping"]);
            Assert.Equal(1, valueTypes["agent.version"]);
            Assert.Equal(1, valueTypes["agent.hostname"]);
            Assert.Equal(1, valueTypes["system.uname"]);
            Assert.Equal(handlerValueType, valueTypes["app.health"]);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public async Task Initialize_ZbxAgentHost_names_no_interface_on_an_active_check()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", "[]");
        server.On("hostgroup.get", "[]");
        server.On("hostgroup.create", CreatedGroup);
        server.On("host.create", CreatedHost);
        server.On("hostinterface.get", """[{"interfaceid":"10","hostid":"100","type":1}]""");
        server.On("item.get", "[]");
        ScriptItemCreate(server, "1");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Initialize-ZbxAgentHost -HostName 'web-02' -Key 'app.health' -InterfaceAddress '10.0.0.7' -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement agent = Assert.Single(Assert.Single(server.ParamsOf("host.create")).GetProperty("interfaces").EnumerateArray());
        Assert.Equal("10.0.0.7", agent.GetProperty("ip").GetString());
        Assert.Equal(1, agent.GetProperty("useip").GetInt32());
        Assert.Equal("10050", agent.GetProperty("port").GetString());
        JsonElement item = Assert.Single(server.ParamsOf("item.create"));
        Assert.False(item.TryGetProperty("interfaceid", out _));
        Assert.Equal(7, item.GetProperty("type").GetInt32());
    }

    [Fact]
    public async Task Initialize_ZbxAgentHost_reuses_an_existing_host_and_does_not_recreate_it()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", parameters => parameters.TryGetProperty("hostids", out _)
            ? FullHost("100", ("1", "agent.ping"), ("2", "agent.version"), ("3", "agent.hostname"), ("4", "system.uname"))
            : FullHost("100"));
        server.On("item.get", "[]");
        ScriptItemCreate(server, "1");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false");
        try
        {
            Run run = shell.Invoke("Initialize-ZbxAgentHost probe -Confirm:$false");

            Assert.Empty(run.Errors);
            Assert.DoesNotContain(server.Calls, call => call.Method == "host.create");
            Assert.DoesNotContain(server.Calls, call => call.Method == "hostinterface.create");
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public async Task Get_ZbxAgentHost_without_Initialize_first_is_one_non_terminating_error()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);
        shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false");
        try
        {
            Run run = shell.Invoke("Get-ZbxAgentHost probe");

            Assert.Contains("ZabbixAgentHostNotInitialized", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public async Task Get_ZbxAgentHost_reads_the_host_and_flags_which_items_have_a_local_handler()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", parameters => parameters.TryGetProperty("hostids", out _)
            ? FullHost("100", ("1", "agent.ping"), ("2", "agent.version"), ("3", "agent.hostname"), ("4", "system.uname"), ("5", "stale.key"))
            : "[]");
        server.On("hostgroup.get", "[]");
        server.On("hostgroup.create", CreatedGroup);
        server.On("host.create", CreatedHost);
        server.On("hostinterface.get", """[{"interfaceid":"10","hostid":"100","type":1}]""");
        server.On("item.get", "[]");
        ScriptItemCreate(server, "1");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false");
        try
        {
            shell.Invoke("Initialize-ZbxAgentHost probe -Confirm:$false");

            Run run = shell.Invoke("Get-ZbxAgentHost probe");

            Assert.Empty(run.Errors);
            var status = Assert.IsType<ZabbixAgentHostStatus>(Assert.Single(run.Output).BaseObject);
            Assert.Equal(5, status.Items.Count);
            Assert.True(status.Items.Single(i => i.Key == "agent.ping").HasLocalHandler);
            Assert.False(status.Items.Single(i => i.Key == "stale.key").HasLocalHandler);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public async Task Publish_ZbxAgentHost_RemoveStale_deletes_an_item_this_bookkeeping_created_for_a_key_no_longer_handled()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", parameters => parameters.TryGetProperty("hostids", out _)
            ? FullHost("100", ("1", "agent.ping"), ("2", "agent.version"), ("3", "agent.hostname"), ("4", "system.uname"))
            : "[]");
        server.On("hostgroup.get", "[]");
        server.On("hostgroup.create", CreatedGroup);
        server.On("host.create", CreatedHost);
        server.On("hostinterface.get", """[{"interfaceid":"10","hostid":"100","type":1}]""");
        server.On("item.get", "[]");
        server.On("item.delete", """{"itemids":["1"]}""");
        ScriptItemCreate(server, "1");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false");
        try
        {
            shell.Invoke("Initialize-ZbxAgentHost probe -Confirm:$false");

            // No cmdlet unregisters a handler, but the same-assembly test project sees the
            // internal ZabbixAgentSession/ZabbixItemDictionary.Remove (both public within the assembly) directly, which
            // is the only way to simulate "the session no longer answers a key it used to" against the real command.
            Assert.True(shell.Context.Sessions.TryGet("probe", out ZabbixAgentSession? session));
            Assert.True(session!.Handlers.Remove("agent.ping"));

            Run published = shell.Invoke("Publish-ZbxAgentHost probe -RemoveStale -Confirm:$false");

            Assert.Empty(published.Errors);
            Assert.Contains(server.Calls, call => call.Method == "item.delete");
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public async Task Publish_ZbxAgentHost_without_RemoveStale_never_deletes_anything()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", parameters => parameters.TryGetProperty("hostids", out _)
            ? FullHost("100", ("1", "agent.ping"), ("2", "agent.version"), ("3", "agent.hostname"), ("4", "system.uname"))
            : "[]");
        server.On("hostgroup.get", "[]");
        server.On("hostgroup.create", CreatedGroup);
        server.On("host.create", CreatedHost);
        server.On("hostinterface.get", """[{"interfaceid":"10","hostid":"100","type":1}]""");
        server.On("item.get", "[]");
        ScriptItemCreate(server, "1");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false");
        try
        {
            shell.Invoke("Initialize-ZbxAgentHost probe -Confirm:$false");
            Assert.True(shell.Context.Sessions.TryGet("probe", out ZabbixAgentSession? session));
            Assert.True(session!.Handlers.Remove("agent.ping"));

            Run published = shell.Invoke("Publish-ZbxAgentHost probe -Confirm:$false");

            Assert.Empty(published.Errors);
            Assert.DoesNotContain(server.Calls, call => call.Method == "item.delete");
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public async Task Clear_ZbxAgentHost_deletes_the_items_and_the_host_it_created()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", parameters => parameters.TryGetProperty("hostids", out _)
            ? FullHost("100", ("1", "agent.ping"), ("2", "agent.version"), ("3", "agent.hostname"), ("4", "system.uname"))
            : "[]");
        server.On("hostgroup.get", "[]");
        server.On("hostgroup.create", CreatedGroup);
        server.On("host.create", CreatedHost);
        server.On("hostinterface.get", """[{"interfaceid":"10","hostid":"100","type":1}]""");
        server.On("item.get", "[]");
        server.On("item.delete", """{"itemids":["1","2","3","4"]}""");
        server.On("host.delete", """{"hostids":["100"]}""");
        ScriptItemCreate(server, "1");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false");
        try
        {
            shell.Invoke("Initialize-ZbxAgentHost probe -Confirm:$false");

            Run run = shell.Invoke("Clear-ZbxAgentHost probe -Confirm:$false");

            Assert.Empty(run.Errors);
            Assert.Contains(server.Calls, call => call.Method == "item.delete");
            Assert.Contains(server.Calls, call => call.Method == "host.delete");

            // Cleared: a second Get finds nothing tracked.
            Run after = shell.Invoke("Get-ZbxAgentHost probe");
            Assert.Contains("ZabbixAgentHostNotInitialized", Assert.Single(after.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public async Task Clear_ZbxAgentHost_never_deletes_a_host_it_reused()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", parameters => parameters.TryGetProperty("hostids", out _)
            ? FullHost("100", ("1", "agent.ping"), ("2", "agent.version"), ("3", "agent.hostname"), ("4", "system.uname"))
            : FullHost("100"));
        server.On("item.get", "[]");
        server.On("item.delete", """{"itemids":["1","2","3","4"]}""");
        ScriptItemCreate(server, "1");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false");
        try
        {
            shell.Invoke("Initialize-ZbxAgentHost probe -Confirm:$false");

            Run run = shell.Invoke("Clear-ZbxAgentHost probe -Confirm:$false");

            Assert.Empty(run.Errors);
            Assert.Contains(server.Calls, call => call.Method == "item.delete");
            Assert.DoesNotContain(server.Calls, call => call.Method == "host.delete");
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public async Task Initialize_ZbxAgentHost_of_an_unknown_session_is_one_non_terminating_error()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Initialize-ZbxAgentHost nope -Confirm:$false");

        Assert.Contains("ZabbixAgentNotFound", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Initialize_ZbxAgentHost_Key_spec_needs_no_running_session()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", "[]");
        server.On("hostgroup.get", "[]");
        server.On("hostgroup.create", CreatedGroup);
        server.On("host.create", CreatedHost);
        server.On("hostinterface.get", """[{"interfaceid":"10","hostid":"100","type":1}]""");
        server.On("item.get", "[]");
        ScriptItemCreate(server, "1");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Initialize-ZbxAgentHost -Key 'a.b','c.d' -HostName web-01 -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Equal(2, server.Calls.Count(call => call.Method == "item.create"));
    }

    [Fact]
    public async Task Export_ZbxAgentTemplate_exports_configuration_scoped_to_the_agent_host()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", parameters => parameters.TryGetProperty("hostids", out _)
            ? FullHost("100", ("1", "agent.ping"), ("2", "agent.version"), ("3", "agent.hostname"), ("4", "system.uname"))
            : "[]");
        server.On("hostgroup.get", "[]");
        server.On("hostgroup.create", CreatedGroup);
        server.On("host.create", CreatedHost);
        server.On("hostinterface.get", """[{"interfaceid":"10","hostid":"100","type":1}]""");
        server.On("item.get", "[]");
        server.On("configuration.export", JsonSerializer.Serialize("""{"zabbix_export":{"version":"7.0","hosts":[{"host":"web-01"}]}}"""));
        ScriptItemCreate(server, "1");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Confirm:$false");
        try
        {
            shell.Invoke("Initialize-ZbxAgentHost probe -Confirm:$false");

            Run run = shell.Invoke("Export-ZbxAgentTemplate probe");

            Assert.Empty(run.Errors);
            string text = Assert.IsType<string>(Assert.Single(run.Output).BaseObject);
            Assert.Contains("web-01", text, StringComparison.Ordinal);
            Assert.Single(server.ParamsOf("configuration.export"), p => p.GetProperty("options").GetProperty("hosts")[0].GetString() == "100");
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }
}
