// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>What the commands share: JSON in the shell's shapes, running sessions, the trapper, safe names.</summary>
public sealed class FoundationTests
{
    [Fact]
    public void Json_from_the_api_reaches_the_shell_as_ConvertFrom_Json_shapes_it()
    {
        using JsonDocument document = JsonDocument.Parse("""{"hostid":"10084","n":3,"ok":true,"tags":[{"tag":"a"}],"none":null}""");

        var shaped = Assert.IsType<PSObject>(ZabbixJsonShape.ToShell(document.RootElement));

        Assert.Equal("10084", shaped.Properties["hostid"].Value);
        Assert.Equal(3L, shaped.Properties["n"].Value);
        Assert.Equal(true, shaped.Properties["ok"].Value);
        Assert.Null(shaped.Properties["none"].Value);
        PSObject tag = Assert.IsType<PSObject>(Assert.Single(Assert.IsType<object[]>(shaped.Properties["tags"].Value)));
        Assert.Equal("a", tag.Properties["tag"].Value);
        Assert.Null(ZabbixJsonShape.ToShell(default));
    }

    [Fact]
    public void What_a_user_writes_becomes_the_json_ConvertTo_Json_writes()
    {
        var table = new Hashtable { ["hostids"] = new[] { "10084" }, ["limit"] = 2 };

        JsonElement fromTable = ZabbixJsonShape.FromShell(table);
        JsonElement fromText = ZabbixJsonShape.FromShell("""{"output":"extend"}""");
        JsonElement fromPsObject = ZabbixJsonShape.FromShell(PSObject.AsPSObject(table));

        Assert.Equal("""["10084"]""", fromTable.GetProperty("hostids").GetRawText());
        Assert.Equal(2, fromTable.GetProperty("limit").GetInt32());
        Assert.Equal("extend", fromText.GetProperty("output").GetString());
        Assert.Equal(2, fromPsObject.GetProperty("limit").GetInt32());
        Assert.Equal(JsonValueKind.Null, ZabbixJsonShape.FromShell(null).ValueKind);
    }

    [Fact]
    public async Task A_pipeline_that_does_not_stop_fails_its_test_naming_the_script_instead_of_blocking_it()
    {
        // A test that fails before it stops its pipeline disposes it on the way out, and
        // PowerShell.Dispose() on a running pipeline stops it synchronously, without a bound: a .NET call in progress is
        // not interruptible (with pwsh 7.6, Stop() and Dispose() each block 14 s of a 15 s Sleep). A Call's
        // disposal is bounded.
        using Shell shell = Shell.Open();
        var sleeping = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const string script = "$sleeping.SetResult(); [System.Threading.Thread]::Sleep(5000)";
        Call call = shell.Start(script, new Dictionary<string, object?> { ["sleeping"] = sleeping }, TimeSpan.FromSeconds(1));
        await sleeping.Task.WaitAsync(Shell.CloseDeadline, TestContext.Current.CancellationToken);

        TimeoutException stuck = await Assert.ThrowsAsync<TimeoutException>(() => call.DisposeAsync().AsTask())
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Contains(script, stuck.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<PipelineStoppedException>(() => call.Running.WaitAsync(Shell.CloseDeadline, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_session_is_found_by_kind_and_name_and_stopped_when_its_runspace_closes()
    {
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var session = new ProbeSession("Probe-1", stopped);
        await using var duplicate = new ProbeSession("probe-1", stopped);
        Shell shell = Shell.Open();
        ZabbixSessions sessions = shell.Context.Sessions;

        Assert.True(sessions.TryAdd(session));
        Assert.False(sessions.TryAdd(duplicate));                                  // names are case-insensitive
        Assert.True(sessions.TryGet("PROBE-1", out ProbeSession? found));
        Assert.Same(session, found);
        Assert.Same(session, Assert.Single(sessions.OfType<ProbeSession>()));
        shell.Dispose();

        Assert.True(stopped.Task.IsCompletedSuccessfully);
        Assert.Empty(sessions.All);
        Assert.Throws<ObjectDisposedException>(() => sessions.TryAdd(new ProbeSession("late", stopped)));
    }

    [Fact]
    public async Task A_name_that_matches_several_objects_is_refused_not_guessed()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web"), ("10085", "web")));
        server.On("host.delete", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Remove-ZbxTestHost web -Force -Confirm:$false");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.StartsWith("ZabbixReferenceAmbiguous", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Contains("10084, 10085", error.Exception.Message, StringComparison.Ordinal);
        Assert.Empty(server.ParamsOf("host.delete"));
    }

    [Fact]
    public async Task The_trapper_is_the_one_given_else_the_connections_else_the_active_ha_node()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hanode.get", """[{"address":"zabbix-a","port":"10051","status":"3"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server, "-Name ha");
        shell.Connect(server, "-Name pinned -Trapper trapper.example:10052");

        Run run = shell.Invoke("""
            Resolve-ZbxTestTrapper -Connection ha
            Resolve-ZbxTestTrapper -Connection pinned
            Resolve-ZbxTestTrapper -Connection ha -Server 192.0.2.1:10053
            """);

        Assert.Empty(run.Errors);
        Assert.Equal(["zabbix-a:10051", "trapper.example:10052", "192.0.2.1:10053"], run.Output.Select(o => ZabbixEndPoint.Format((System.Net.DnsEndPoint)o.BaseObject)));
        JsonElement asked = Assert.Single(server.ParamsOf("hanode.get"));
        Assert.Equal("3", asked.GetProperty("filter").GetProperty("status").ToString());
    }

    [Fact]
    public async Task No_trapper_is_invented_when_the_api_names_no_active_node()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hanode.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Resolve-ZbxTestTrapper");

        Assert.Empty(run.Output);
        Assert.Contains("-Trapper host:port", Assert.Single(run.Errors).Exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_connection_runs_on_its_runspaces_clock()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        shell.Context.Time = clock;

        shell.Connect(server);

        Assert.Same(clock, shell.Context.Current!.Client.Time);
    }

    private sealed class ProbeSession(string name, TaskCompletionSource stopped) : IZabbixSession
    {
        public string Name { get; } = name;

        public DateTimeOffset Started { get; } = DateTimeOffset.UnixEpoch;

        public ValueTask DisposeAsync()
        {
            stopped.TrySetResult();
            return ValueTask.CompletedTask;
        }
    }
}
