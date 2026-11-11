// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Reflection;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>The one collection every PowerShell stress/concurrency class runs in, one at a time.</summary>
[CollectionDefinition("Adversarial PowerShell stress", DisableParallelization = true)]
public sealed class AdversarialPowerShellStressGroup;

/// <summary>
/// Concurrency — two runspaces hammering one connection, <c>ForEach-Object -Parallel</c>
/// using the context, and <c>Remove-Module Community.Zabbix</c> while a call is in flight in another runspace. It must work or fail
/// clearly; never corrupt (one caller's answer reaching another) or crash.
/// </summary>
[Collection("Adversarial PowerShell stress")]
public sealed class AdversarialConcurrencyTests
{
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(60);

    // Answers host.get with the one host the request's name selection names (a -Name is a search), so a reply that
    // reached the wrong caller shows.
    private static string Echo(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("search", out JsonElement search) || !search.TryGetProperty("host", out JsonElement host)) return "[]";
        string name = (host.ValueKind == JsonValueKind.Array ? host[0].GetString() : host.GetString())!;
        return FakeZabbix.Hosts((name[1..], name));
    }

    [Fact]
    public async Task Two_runspaces_sharing_one_connection_object_each_get_their_own_answers()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", Echo);
        using Shell owner = Shell.Open();
        using Shell borrower = Shell.Open();
        owner.Connect(server);
        ZabbixConnection connection = owner.Context.Current!;
        const string script = "foreach ($i in $range) { $h = Get-ZbxHost -Name \"h$i\" -Connection $connection; if ($h.HostId -ne \"$i\") { throw \"asked h$i, got $($h.HostId)\" }; $h.HostId }";

        await using Call a = owner.Start(script, new Dictionary<string, object?> { ["range"] = Enumerable.Range(1, 150).ToArray(), ["connection"] = connection });
        await using Call b = borrower.Start(script, new Dictionary<string, object?> { ["range"] = Enumerable.Range(1001, 150).ToArray(), ["connection"] = connection });
        PSDataCollection<PSObject>[] results = await Task.WhenAll(a.Running, b.Running).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        Assert.Empty(a.Streams.Error);
        Assert.Empty(b.Streams.Error);
        Assert.Equal(Enumerable.Range(1, 150).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), results[0].Select(o => o.BaseObject.ToString()!));
        Assert.Equal(Enumerable.Range(1001, 150).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)), results[1].Select(o => o.BaseObject.ToString()!));
        Assert.Equal(300, server.ParamsOf("host.get").Count());
        Assert.False(connection.IsClosed);
    }

    [Fact]
    public async Task Eight_runspaces_hammering_one_connection_while_it_is_disconnected_either_finish_or_fail_with_a_closed_connection_error()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", Echo);
        using Shell owner = Shell.Open();
        owner.Connect(server);
        ZabbixConnection connection = owner.Context.Current!;
        Shell[] shells = [.. Enumerable.Range(0, 8).Select(_ => Shell.Open())];
        var calls = new List<Call>();
        try
        {
            foreach (Shell shell in shells)
            {
                calls.Add(shell.Start(
                    "foreach ($i in 1..40) { Get-ZbxHost -Name \"h$i\" -Connection $connection }", new Dictionary<string, object?> { ["connection"] = connection }));
            }
            TaskCompletionSource<string> arrived = server.ExpectArrival();
            await arrived.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            owner.Invoke("Disconnect-ZbxServer");
            PSDataCollection<PSObject>[] outputs = await Task.WhenAll(calls.Select(call => call.Running)).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            for (int i = 0; i < calls.Count; i++)
            {
                Call call = calls[i];
                Assert.All(call.Streams.Error, error => Assert.Equal(ErrorCategory.ResourceUnavailable, error.CategoryInfo.Category));
                Assert.All(outputs[i].Select(o => (ZabbixHost)o.BaseObject), host => Assert.StartsWith("h", host.TechnicalName, StringComparison.Ordinal));
                Assert.Equal(40, outputs[i].Count + call.Streams.Error.Count);
            }
            Assert.True(connection.IsReleased);
        }
        finally
        {
            // Every stop and close bounded and side by side, so a stuck one fails within one deadline, not nine in a row.
            await Task.WhenAll(calls.Select(call => call.DisposeAsync().AsTask()));
            await Task.WhenAll(shells.Select(shell => Task.Run(shell.Dispose)));
        }
    }

    [Fact]
    public async Task ForEach_Object_Parallel_without_a_connection_fails_each_branch_with_ZabbixNotConnected()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", Echo);
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("1..4 | ForEach-Object -Parallel { Import-Module $using:manifest; Get-ZbxHost -Name \"h$_\" } -ThrottleLimit 4",
            new Dictionary<string, object?> { ["manifest"] = Shell.Manifest });

        Assert.Empty(run.Output);
        Assert.Equal(4, run.Errors.Count);
        Assert.All(run.Errors, error => Assert.StartsWith("ZabbixNotConnected,", error.FullyQualifiedErrorId, StringComparison.Ordinal));
        Assert.Empty(server.ParamsOf("host.get"));
    }

    [Fact]
    public async Task ForEach_Object_Parallel_with_the_connection_passed_in_works_in_every_branch()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", Echo);
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            $connection = (Get-ZbxContext).Current
            1..32 | ForEach-Object -Parallel { Import-Module $using:manifest; (Get-ZbxHost -Name "h$_" -Connection $using:connection).HostId } -ThrottleLimit 8
            """, new Dictionary<string, object?> { ["manifest"] = Shell.Manifest });

        Assert.Empty(run.Errors);
        Assert.Equal(Enumerable.Range(1, 32).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal),
            run.Output.Select(o => o.BaseObject.ToString()!).Order(StringComparer.Ordinal));
        Assert.False(shell.Context.Current!.IsClosed);
    }

    [Fact]
    public async Task Remove_Module_in_the_owner_runspace_lets_a_borrowed_in_flight_call_finish_and_the_module_imports_again()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01"))).Stall("host.get");
        using Shell owner = Shell.Open();
        using Shell borrower = Shell.Open();
        owner.Connect(server);
        ZabbixConnection connection = owner.Context.Current!;
        TaskCompletionSource<string> arrived = server.ExpectArrival();
        await using Call call = borrower.Start("Get-ZbxHost -Connection $connection", new Dictionary<string, object?> { ["connection"] = connection });
        await arrived.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        Run removed = owner.Invoke("Remove-Module Community.Zabbix, Community.Zabbix.Agent, Community.Zabbix.Proxy");
        server.Release("host.get");
        PSDataCollection<PSObject> hosts = await call.Running.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
        Run again = owner.Invoke($"Import-Module '{Shell.Manifest}'; Connect-ZbxServer -Uri '{server.Endpoint}' -ApiToken (ConvertTo-SecureString 't' -AsPlainText -Force); Get-ZbxHost");

        Assert.Empty(removed.Errors);
        Assert.Equal("web-01", ((ZabbixHost)Assert.Single(hosts).BaseObject).TechnicalName);
        Assert.Empty(call.Streams.Error);
        Assert.True(connection.IsReleased);
        Assert.Empty(again.Errors);
        Assert.Equal("web-01", ((ZabbixHost)Assert.Single(again.Output).BaseObject).TechnicalName);
    }

    [Fact]
    public async Task A_command_after_Remove_Module_in_the_same_runspace_is_not_found_and_import_restores_it_unconnected()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run removed = shell.Invoke("Remove-Module Community.Zabbix, Community.Zabbix.Agent, Community.Zabbix.Proxy; Get-ZbxHost");
        Run imported = shell.Invoke($"Import-Module '{Shell.Manifest}'; Get-ZbxHost");

        Assert.IsType<CommandNotFoundException>(Assert.Single(removed.Errors).Exception);
        Assert.StartsWith("ZabbixNotConnected,", Assert.Single(imported.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Empty(server.ParamsOf("host.get"));
    }

    [Fact]
    public async Task Concurrent_tab_completion_in_many_runspaces_fetches_names_once_per_runspace_and_answers_every_request()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("1", "alpha"), ("2", "alpine"), ("3", "beta"), ("4", "with space")));
        Shell[] shells = [.. Enumerable.Range(0, 8).Select(_ => Shell.Open())];
        try
        {
            foreach (Shell shell in shells) shell.Connect(server);

            Task<string[]>[] completions = [.. shells.Select(shell => Task.Run(() => Enumerable.Range(0, 25)
                .SelectMany(_ => shell.Invoke("$s = 'Remove-ZbxHost -InputObject al'; (TabExpansion2 -inputScript $s -cursorColumn $s.Length).CompletionMatches.CompletionText").Output)
                .Select(o => o.BaseObject.ToString()!).ToArray()))];
            string[][] results = await Task.WhenAll(completions).WaitAsync(Failsafe, TestContext.Current.CancellationToken);

            Assert.All(results, result => Assert.Equal(Enumerable.Repeat(new[] { "alpha", "alpine" }, 25).SelectMany(x => x), result));
            Assert.Equal(shells.Length, server.ParamsOf("host.get").Count());
        }
        finally
        {
            foreach (Shell shell in shells) shell.Dispose();
        }
    }

    [Fact]
    public void Tab_completion_with_no_connection_offers_nothing_and_writes_no_error()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("foreach ($s in 'Get-ZbxHost -Name we', 'Get-ZbxHost -Connection x', 'Remove-ZbxHost ') { (TabExpansion2 -inputScript $s -cursorColumn $s.Length).CompletionMatches }");

        Assert.Empty(run.Errors);
        Assert.Empty(run.Output);
    }

    [Fact]
    public async Task Tab_completion_quotes_a_name_that_needs_quoting_so_the_completed_line_parses()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("1", "with space"), ("2", "it's"), ("3", "$(boom)"), ("4", "a;b")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("$s = 'Remove-ZbxHost -InputObject '; (TabExpansion2 -inputScript $s -cursorColumn $s.Length).CompletionMatches.CompletionText");

        Assert.Empty(run.Errors);
        string[] texts = [.. run.Output.Select(o => o.BaseObject.ToString()!)];
        Assert.Equal(4, texts.Length);
        Assert.All(texts, text =>
        {
            System.Management.Automation.Language.Parser.ParseInput("Remove-ZbxHost -InputObject " + text, out _, out System.Management.Automation.Language.ParseError[] errors);
            Assert.Empty(errors);
            Assert.StartsWith("'", text, StringComparison.Ordinal);
        });
    }
    [Theory]
    [InlineData("error")]
    [InlineData("down")]
    public async Task A_completion_whose_server_call_fails_offers_nothing_and_warns_event_14100_on_the_next_command(string failure)
    {
        // The module's only Warning+ event no other test asserts: ZabbixPowerShellLog.CompletionFailed (EventId 14100),
        // logged off any pipeline by the completer and flushed as a warning onto the next command on that connection.
        MethodInfo completionFailed = typeof(Diagnostics.ZabbixPowerShellLog).GetMethod("CompletionFailed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert.Equal(14100, completionFailed.GetCustomAttribute<Microsoft.Extensions.Logging.LoggerMessageAttribute>()!.EventId);
        FakeZabbix server = await FakeZabbix.StartAsync();
        try
        {
            server.On("host.get", FakeZabbix.Error(-32500, "Application error.", "No permissions to call \\\"host.get\\\"."));
            using Shell shell = Shell.Open();
            shell.Connect(server, "-Name prod");
            if (failure == "down") await server.DisposeAsync();

            Run completed = shell.Invoke("$s = 'Remove-ZbxHost -InputObject al'; (TabExpansion2 -inputScript $s -cursorColumn $s.Length).CompletionMatches");
            Run next = shell.Invoke("Get-ZbxApiVersion");

            Assert.Empty(completed.Errors);
            Assert.Empty(completed.Output);
            Assert.Empty(next.Errors);
            Assert.Contains(next.Warnings, warning => warning.StartsWith("Completing host names on prod failed; no names offered", StringComparison.Ordinal));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }
    [Fact]
    public async Task A_watch_whose_connection_is_disconnected_ends_its_stream_so_a_reader_does_not_hang()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", WatchedItem).On("host.get", FakeZabbix.Hosts(("10084", "web-01"))).On("history.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        Assert.Empty(shell.Invoke("Watch-ZbxItem probe web-01 -Interval 01:00:00").Errors);
        shell.Invoke("Disconnect-ZbxServer");

        await using Call call = shell.Start("Receive-ZbxWatch probe");
        PSDataCollection<PSObject> read = await call.Running.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        // A closed connection retires its watches: the reader gets "no watch is named", at once.
        Assert.StartsWith("ZabbixWatchNotFound,", Assert.Single(call.Streams.Error).FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.All(read, value => Assert.IsType<ZabbixValue>(value.BaseObject));
    }

    [Fact]
    public async Task A_watch_that_stopped_with_its_disconnected_connection_is_no_longer_listed_as_running()
    {
        // After Disconnect-ZbxServer the watcher has stopped ("its client was disposed": the container the watch polled
        // through is gone, and Receive-ZbxWatch ends at once, the test above), so Get-ZbxWatch no longer lists it as
        // running: a stopped watch still listed in ZabbixContext.Sessions, with no state that says it is no longer polling,
        // would mislead whoever reads the list.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", WatchedItem).On("host.get", FakeZabbix.Hosts(("10084", "web-01"))).On("history.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        Assert.Empty(shell.Invoke("Watch-ZbxItem probe web-01 -Interval 01:00:00").Errors);

        Run listed = shell.Invoke("Disconnect-ZbxServer; Get-ZbxWatch");

        Assert.Empty(listed.Output);
    }

    private static string WatchedItem(JsonElement parameters)
        => """[{"itemid":"1","hostid":"10084","key_":"agent.ping","value_type":"3","lastclock":"1000","lastns":"0","lastvalue":"1","hosts":[{"hostid":"10084","host":"web-01"}]}]""";
}
