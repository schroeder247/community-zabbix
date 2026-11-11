// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>The host lifecycle: <c>Disable-ZbxHost</c>, <c>Enable-ZbxHost</c>, <c>Resolve-ZbxTrapperEndpoint</c>, <c>Wait-ZbxHost</c>.</summary>
public sealed class HostLifecycleCommandsTests
{
    [Fact]
    public async Task Disable_ZbxHost_resolves_the_name_then_updates_its_status()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("host.update", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        // No -Force, no -Confirm:$false: ConfirmImpact Medium does not prompt at the default $ConfirmPreference, and a
        // host that cannot prompt runs it (it must not throw ZabbixConfirmationUnavailable).
        Run run = shell.Invoke("Disable-ZbxHost web-01");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.update"));
        JsonElement update = Assert.Single(request.EnumerateArray());
        Assert.Equal("1", update.GetProperty("status").ToString());
    }

    [Fact]
    public async Task Enable_ZbxHost_sends_the_monitored_status()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("host.update", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Enable-ZbxHost web-01");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.update"));
        JsonElement update = Assert.Single(request.EnumerateArray());
        Assert.Equal("0", update.GetProperty("status").ToString());
    }

    [Fact]
    public async Task Disable_ZbxHost_confirms_each_resolved_host_by_its_name()
    {
        // One ShouldProcess per resolved host, named, not "2 host(s)" before resolution; -Force's verbose line
        // is the same target ShouldProcess would ask about.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("host.update", """{"hostids":["10084","10085"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Disable-ZbxHost web-01, 10085 -Force -Verbose");

        Assert.Empty(run.Errors);
        Assert.Contains(run.Verbose, line => line.Contains("\"Disable host\" on target \"host 'web-01' (10084)\"", StringComparison.Ordinal));
        Assert.Contains(run.Verbose, line => line.Contains("\"Disable host\" on target \"host 10085\"", StringComparison.Ordinal));
        Assert.Equal(2, Assert.Single(server.ParamsOf("host.update")).GetArrayLength());
    }

    [Fact]
    public async Task Resolve_ZbxTrapperEndpoint_of_a_server_monitored_host_is_the_connections_trapper()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """[{"hostid":"10084","host":"web-01","name":"web-01","status":"0","monitored_by":"0"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server, "-Trapper zbx.example:10052");

        Run run = shell.Invoke("Get-ZbxHost web-01 | Resolve-ZbxTrapperEndpoint");

        Assert.Empty(run.Errors);
        dynamic endpoint = Assert.Single(run.Output);
        Assert.Equal("zbx.example", (string)endpoint.Server);
        Assert.Equal(10052, (int)endpoint.Port);
    }

    [Fact]
    public async Task Resolve_ZbxTrapperEndpoint_of_a_proxy_monitored_host_is_the_proxys_own_address()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", """[{"hostid":"10084","host":"web-01","name":"web-01","status":"0","monitored_by":"1","proxyid":"5"}]""");
        server.On("proxy.get", """[{"proxyid":"5","name":"proxy-1","address":"proxy.example","port":"10051"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost web-01 | Resolve-ZbxTrapperEndpoint");

        Assert.Empty(run.Errors);
        dynamic endpoint = Assert.Single(run.Output);
        Assert.Equal("proxy.example", (string)endpoint.Server);
        Assert.Equal(10051, (int)endpoint.Port);
    }

    // These two set the fake clock BEFORE Connect-ZbxServer, so it is the clock of the connection Wait-ZbxHost polls on
    // (set after, the connection would run on the system clock and every Advance would be a no-op). Each poll the server
    // sees is what moves the clock one interval: no unbounded `while (calls < 3) time.Advance(…)` loop, no wall time.
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Wait_ZbxHost_polls_until_the_host_appears()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        int calls = 0;
        server.On("host.get", _ => Interlocked.Increment(ref calls) < 3 ? "[]" : FakeZabbix.Hosts(("10084", "web-042")));
        using Shell shell = Shell.Open();
        var time = new FakeTimeProvider();
        shell.Context.Time = time;
        shell.Connect(server);
        Assert.Same(time, shell.Context.Current!.Client.Time);
        TaskCompletionSource<string> polled = server.ExpectArrival();

        await using Call call = shell.Start("Wait-ZbxHost web-042 -Interval 1 -Timeout 30");
        for (int poll = 1; poll < 3; poll++) polled = await NextIntervalAsync(server, time, polled);

        PSDataCollection<PSObject> output = await call.Running.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        Assert.Empty(call.Streams.Error);
        Assert.Equal(3, Volatile.Read(ref calls));
        Assert.Equal("web-042", ((dynamic)Assert.Single(output).BaseObject).HostName);
    }

    [Fact]
    public async Task Wait_ZbxHost_times_out_when_the_host_never_appears()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", "[]");
        using Shell shell = Shell.Open();
        var time = new FakeTimeProvider();
        shell.Context.Time = time;
        shell.Connect(server);
        TaskCompletionSource<string> polled = server.ExpectArrival();

        await using Call call = shell.Start("Wait-ZbxHost nope -Interval 1 -Timeout 2");
        for (int poll = 1; poll < 3; poll++) polled = await NextIntervalAsync(server, time, polled);

        await call.Running.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        Assert.Contains("did not appear", Assert.Single(call.Streams.Error).Exception.Message, StringComparison.Ordinal);
    }

    // Waits for the poll to reach the server, arms the signal for the next one, then moves the clock one interval (a
    // PeriodicTimer keeps a tick that fires before it is awaited, so the order against the command's own await is free).
    private static async Task<TaskCompletionSource<string>> NextIntervalAsync(FakeZabbix server, FakeTimeProvider time, TaskCompletionSource<string> polled)
    {
        Assert.Equal("host.get", await polled.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken));
        TaskCompletionSource<string> next = server.ExpectArrival();
        time.Advance(TimeSpan.FromSeconds(1));
        return next;
    }
}
