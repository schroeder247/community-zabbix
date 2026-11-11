// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Net;
using System.Net.Sockets;
using Community.Zabbix.PowerShell.Commands;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// One <c>-Server</c> shape: a list of nodes, tried in order (one HA cluster), each read by
/// <see cref="ZabbixEndPoint"/>'s parser; <c>Start-ZbxActiveAgent</c>'s is Zabbix's <c>ServerActive</c> grammar,
/// where <c>;</c> fails over and <c>,</c> names independent destinations. The sender needs no API connection when it is
/// given its trapper.
/// </summary>
public sealed class ServerNodeListTests
{
    private const string EmptyChecks = """{"response":"success","config_revision":1}""";

    [Fact]
    public void Every_Server_parameter_is_a_list_of_nodes_and_no_command_takes_a_Port_beside_it()
    {
        var problems = new List<string>();
        foreach (Type type in ModuleUnderTest.AllCmdlets)
        {
            if (type.GetProperty("Server") is not { } server || server.GetCustomAttributes(typeof(ParameterAttribute), true).Length == 0) continue;
            Type expected = type == typeof(StartZbxActiveAgentCommand) ? typeof(string) : typeof(DnsEndPoint[]);
            if (server.PropertyType != expected) problems.Add($"{type.Name}.Server is {server.PropertyType.Name}");
            if (type.GetProperty("Port") is { } port && port.GetCustomAttributes(typeof(ParameterAttribute), true).Length > 0) problems.Add($"{type.Name} takes -Port beside -Server");
        }
        Assert.Equal(typeof(DnsEndPoint[]), typeof(ConnectZbxServerCommand).GetProperty("Trapper")!.PropertyType);

        Assert.Empty(problems);
    }

    [Fact]
    public async Task Get_ZbxServerStatus_fails_over_to_the_next_node_of_Server()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("status.get", """{"response":"success","data":{"host":{"count":3}}}""");
        using Shell shell = Shell.Open();
        shell.Connect(api);

        Run run = shell.Invoke($"Get-ZbxServerStatus -Server '127.0.0.1:{ClosedPort()}', '127.0.0.1:{trapper.Port}'");

        Assert.Empty(run.Errors);
        Assert.Single(run.Output);
        Assert.Single(trapper.FramesOf("status.get"));
    }

    public static TheoryData<string, string, string> WireCommands => new()
    {
        { "Get-ZbxQueue -Server {nodes}", "queue.get", """{"response":"success","data":[]}""" },
        { "Get-ZbxServerStatus -Server {nodes}", "status.get", """{"response":"success","data":{}}""" },
        { "Test-ZbxItem -Data @{ item = @{ key = 'agent.ping' } } -Server {nodes}", "item.test", """{"response":"success","data":{"item":{"result":"1","eol":"LF"},"preprocessing":{"steps":[],"result":"1"}}}""" },
        { "Test-ZbxExpression 'a' -Server {nodes}", "expressions.evaluate", """{"response":"success","data":[{"expression":"a","value":1}]}""" },
        { "Test-ZbxMediaType Email -SendTo ops@example.com -Server {nodes} -Confirm:$false", "alert.send", """{"response":"success"}""" },
        { "Send-ZbxData web-01 app.status ok -Server {nodes} -Confirm:$false", "sender data", FakeTrapper.Success(1, 0) },
        { "Receive-ZbxProxyConfig zbx-proxy -Server {nodes}", "proxy config", "{}" },
        { "Send-ZbxProxyData zbx-proxy -Server {nodes} -History (New-ZbxHistoryDataEntry -ItemId 1 -Value 1) -Confirm:$false", "proxy data", FakeTrapper.Success(1, 0) },
    };

    [Theory]
    [MemberData(nameof(WireCommands))]
    public async Task A_wire_command_fails_over_to_the_next_node_of_its_Server_list(string command, string request, string answer)
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        api.On("mediatype.get", """[{"mediatypeid":"7","name":"Email"}]""");
        await using FakeTrapper live = FakeTrapper.Start();
        live.On(request, answer);
        using Shell shell = Shell.Open();
        shell.Connect(api);

        Run run = shell.Invoke(command.Replace("{nodes}", $"'127.0.0.1:{ClosedPort()}', '127.0.0.1:{live.Port}'", StringComparison.Ordinal));

        Assert.Empty(run.Errors);
        Assert.Single(live.FramesOf(request));
    }

    [Fact]
    public async Task Connect_ZbxServer_Trapper_takes_the_nodes_of_an_HA_cluster()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("queue.get", """{"response":"success","data":[]}""");
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper '127.0.0.1:{ClosedPort()}', '127.0.0.1:{trapper.Port}'");

        Run run = shell.Invoke("Get-ZbxQueue");

        Assert.Empty(run.Errors);
        Assert.Single(trapper.FramesOf("queue.get"));
    }

    [Fact]
    public async Task Send_ZbxData_with_Server_sends_with_no_connection()
    {
        // zabbix_sender's own use: a host with no API token sends to its trapper.
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("sender data", FakeTrapper.Success(1, 0));
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"Send-ZbxData web-01 app.status ok -Server '127.0.0.1:{ClosedPort()}', '127.0.0.1:{trapper.Port}' -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Equal(1, Assert.IsType<ZabbixSenderResponse>(Assert.Single(run.Output).BaseObject).Processed);
        Assert.Equal("web-01", Assert.Single(trapper.FramesOf("sender data")).GetProperty("data")[0].GetProperty("host").GetString());
    }

    [Fact]
    public async Task Send_ZbxData_with_no_connection_logs_to_its_own_streams()
    {
        // A connection's loggers reach the command's streams; with none, the command's own do.
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("sender data", FakeTrapper.Success(1, 0));
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"$DebugPreference = 'Continue'; Send-ZbxData web-01 app.status ok -Server '127.0.0.1:{trapper.Port}' -Confirm:$false | Out-Null");

        Assert.Empty(run.Errors);
        Assert.Contains(run.Debug, line => line.Contains($"127.0.0.1:{trapper.Port}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Get_ZbxAgentItem_logs_to_its_own_streams()
    {
        await using FakeTrapper agent = FakeTrapper.Start();
        agent.Otherwise = _ => "1";
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"$DebugPreference = 'Continue'; Get-ZbxAgentItem '127.0.0.1:{agent.Port}' agent.ping | Out-Null");

        Assert.Empty(run.Errors);
        Assert.NotEmpty(run.Debug);
    }

    [Fact]
    public void Send_ZbxData_without_Server_or_a_connection_is_ZabbixNotConnected()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Send-ZbxData web-01 app.status ok -Confirm:$false");

        Assert.StartsWith("ZabbixNotConnected,", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("'127.0.0.1:{closed};127.0.0.1:{live}'")]
    [InlineData("'127.0.0.1:{closed}', '127.0.0.1:{live}'")]
    public async Task Start_ZbxActiveAgent_fails_over_within_one_HA_cluster(string server)
    {
        // ';' in the ServerActive text, or a list: the nodes of one cluster, the second asked when the first is down.
        await using FakeTrapper live = FakeTrapper.Start();
        live.On("active checks", EmptyChecks).On("agent data", FakeTrapper.Success(0, 0));
        using Shell shell = Shell.Open();
        try
        {
            Run started = shell.Invoke($"Start-ZbxActiveAgent probe web-01 -Server {server.Replace("{closed}", ClosedPort().ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal).Replace("{live}", live.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)} -Confirm:$false");

            Assert.Empty(started.Errors);
            await WaitForAsync(() => live.FramesOf("active checks").Any());
        }
        finally
        {
            shell.Invoke("Stop-ZbxActiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public async Task Start_ZbxActiveAgent_comma_sends_to_two_independent_destinations()
    {
        await using FakeTrapper first = FakeTrapper.Start();
        await using FakeTrapper second = FakeTrapper.Start();
        first.On("active checks", EmptyChecks).On("agent data", FakeTrapper.Success(0, 0));
        second.On("active checks", EmptyChecks).On("agent data", FakeTrapper.Success(0, 0));
        using Shell shell = Shell.Open();
        try
        {
            Run started = shell.Invoke($"Start-ZbxActiveAgent probe web-01 -Server '127.0.0.1:{first.Port},127.0.0.1:{second.Port}' -Confirm:$false");

            Assert.Empty(started.Errors);
            await WaitForAsync(() => first.FramesOf("active checks").Any() && second.FramesOf("active checks").Any());
        }
        finally
        {
            shell.Invoke("Stop-ZbxActiveAgent probe -Confirm:$false");
        }
    }

    [Theory]
    [InlineData("zbx1", "zbx1:10051")]
    [InlineData("zbx1;zbx2:10052", "zbx1:10051;zbx2:10052")]
    [InlineData("zbx-a,zbx-b", "zbx-a:10051,zbx-b:10051")]
    [InlineData("2001:db8::7", "[2001:db8::7]:10051")]
    [InlineData("[2001:db8::7]:10052;zbx2", "[2001:db8::7]:10052;zbx2:10051")]
    public void Start_ZbxActiveAgent_Server_reads_the_ServerActive_grammar(string text, string serverActive)
    {
        // A bare IPv6 address is one node with the trapper's port, never split at its last colon.
        Assert.Equal(serverActive, ZabbixServerActive.Parse(Transform(text), null).ToString());
    }

    [Fact]
    public void Start_ZbxActiveAgent_Server_reads_a_list_as_one_HA_cluster()
    {
        ZabbixServerActive active = ZabbixServerActive.Parse(Transform(new object[] { "zbx1", "zbx2:10052" }), null);

        IReadOnlyList<ZabbixServerNode> cluster = Assert.Single(active);
        Assert.Equal(["zbx1:10051", "zbx2:10052"], cluster.Select(node => node.ToString()));
    }

    [Theory]
    [InlineData("zbx1;zbx1")]
    [InlineData("zbx1;;zbx2")]
    public void Start_ZbxActiveAgent_Server_refuses_what_upstream_refuses(string text)
    {
        Assert.Throws<ArgumentTransformationMetadataException>(() => Transform(text));
    }

    private static string Transform(object value)
        => (string)new ZabbixServerActiveAttribute().Transform(null!, value)!;

    // A loopback port nothing listens on: bound and released, so a connect to it is refused at once.
    private static int ClosedPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        // The conditions read a session's state, which raises no event to await.
#pragma warning disable RS0030
        while (!condition())
            await Task.Delay(50, deadline.Token);
#pragma warning restore RS0030
    }
}
