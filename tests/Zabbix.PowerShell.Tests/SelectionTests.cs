// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>Ids, references, the <c>Get-</c> base's parameter sets, and completion.</summary>
public sealed class SelectionTests
{
    [Fact]
    public async Task A_bad_id_fails_at_binding()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -Id web01");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.StartsWith("CannotConvertArgumentNoMessage,", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Contains("web01", error.Exception.Message, StringComparison.Ordinal);
        Assert.Empty(server.ParamsOf("host.get"));
    }

    [Fact]
    public async Task Get_selects_by_id_by_name_by_wildcard_or_by_the_typed_parameters()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "Zabbix server")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            Get-ZbxHost -Id 10084, 10085
            Get-ZbxHost 'Zabbix server', web01
            Get-ZbxHost 'web*' -Limit 5
            Get-ZbxHost -Filter ([Community.Zabbix.ZabbixHostGetParameters]@{ Limit = 2 })
            Get-ZbxHost
            """);

        // web01 matched nothing: one ObjectNotFound naming it (Get-Service's rule for a name without a wildcard).
        Assert.Equal("web01", Assert.Single(run.Errors).TargetObject);
        Assert.Equal(5, run.Output.Count);
        JsonElement[] calls = [.. server.ParamsOf("host.get")];
        // -Id selects by the entity's own top-level *ids option (hostids), not
        // filter.hostid — host.get's own ZabbixEntityShape (used by both this hand-written Get-ZbxHost and
        // New-/Set-ZbxHost's readback) carries HostIds, same as every generated entity's shape does when it has one.
        Assert.Equal("""["10084","10085"]""", calls[0].GetProperty("hostids").GetRawText());
        Assert.False(calls[0].TryGetProperty("filter", out _));
        // An exact name is a case-insensitive search (a pattern with no wildcard), not a filter.
        Assert.Equal("""["Zabbix server","web01"]""", calls[1].GetProperty("search").GetProperty("host").GetRawText());
        Assert.True(calls[1].GetProperty("searchWildcardsEnabled").GetBoolean());
        Assert.True(calls[1].GetProperty("searchByAny").GetBoolean());
        Assert.Equal("""["web*"]""", calls[2].GetProperty("search").GetProperty("host").GetRawText());
        Assert.True(calls[2].GetProperty("searchWildcardsEnabled").GetBoolean());
        Assert.Equal(5, calls[2].GetProperty("limit").GetInt32());
        Assert.Equal(2, calls[3].GetProperty("limit").GetInt32());
        Assert.False(calls[4].TryGetProperty("filter", out _));
        Assert.IsType<ZabbixHost>(run.Output[0].BaseObject);
    }

    [Fact]
    public async Task Host_names_complete_from_a_bounded_search_for_the_word_once_per_word()
    {
        // Completion asks for the names starting with the word (not for every host to offer a few names), at most
        // ZabbixCompletion.Limit of them, and keeps each word's answer for a minute.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "Zabbix server"), ("10085", "web01"), ("10086", "web02")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        string[] first = Complete(shell, "Get-ZbxHost -Name we");
        string[] again = Complete(shell, "Get-ZbxHost -Name WE");
        string[] second = Complete(shell, "Get-ZbxHost -Name Zab");

        Assert.Equal(["web01", "web02"], first);
        Assert.Equal(first, again);
        Assert.Equal(["'Zabbix server'"], second);
        JsonElement[] fetches = [.. server.ParamsOf("host.get")];
        Assert.Equal(2, fetches.Length);
        Assert.Equal("""["hostid","host"]""", fetches[0].GetProperty("output").GetRawText());
        Assert.Equal("""["we"]""", fetches[0].GetProperty("search").GetProperty("host").GetRawText());
        Assert.True(fetches[0].GetProperty("startSearch").GetBoolean());
        Assert.Equal(ZabbixCompletion.Limit, fetches[0].GetProperty("limit").GetInt32());
        Assert.Equal("""["Zab"]""", fetches[1].GetProperty("search").GetProperty("host").GetRawText());
    }

    [Fact]
    public async Task A_completion_that_fails_offers_nothing_and_warns_on_the_next_command()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Error(-32500, "Application error.", "No permissions to call \\\"host.get\\\"."));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        string[] offered = Complete(shell, "Get-ZbxHost -Name we");
        Run next = shell.Invoke("Get-ZbxApiVersion");

        Assert.Empty(offered);
        Assert.Contains(next.Warnings, warning => warning.StartsWith("Completing host names on", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connection_names_complete_from_the_context()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server, "-Name prod");
        shell.Connect(server, "-Name test");

        Assert.Equal(["prod"], Complete(shell, "Get-ZbxHost -Connection pr"));
    }

    private static string[] Complete(Shell shell, string input)
    {
        CommandCompletion completion = shell.Complete(input);
        return [.. completion.CompletionMatches.Select(match => match.CompletionText)];
    }

    [Fact]
    public async Task First_is_the_servers_limit_and_Skip_passes_over_what_it_sent()
    {
        // The engine's paging: Get-ZbxItem | Select-Object -First 1 downloaded every item; -First 1 asks for one.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("1", "a"), ("2", "b"), ("3", "c"))).On("template.get", "[]");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run first = shell.Invoke("Get-ZbxHost -First 2");
        Run skipped = shell.Invoke("Get-ZbxTemplate -First 1 -Skip 2");
        Run host = shell.Invoke("Get-ZbxHost -Skip 1 | ForEach-Object HostId");

        Assert.Empty(first.Errors);
        Assert.Equal(2, server.ParamsOf("host.get").First().GetProperty("limit").GetInt32());
        Assert.Equal(3, server.ParamsOf("template.get").Single().GetProperty("limit").GetInt32());
        Assert.Empty(skipped.Errors);
        Assert.Equal(["2", "3"], host.Output.Select(o => o.BaseObject.ToString()));
    }

    [Fact]
    public async Task IncludeTotalCount_writes_the_count_of_the_whole_selection()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", request => request.TryGetProperty("countOutput", out _) ? "\"42\"" : FakeZabbix.Hosts(("1", "a")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHost -First 1 -IncludeTotalCount");

        Assert.Empty(run.Errors);
        Assert.Equal(2, run.Output.Count);
        Assert.Equal(42UL, run.Output[^1].BaseObject);
    }
}
