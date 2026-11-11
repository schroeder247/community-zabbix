// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// Working with problems: <c>Confirm-ZbxProblem</c> over <c>event.acknowledge</c> (each switch one bit of the action
/// mask), <c>Get-ZbxProblem -Severity</c>, <c>Get-ZbxTrigger -ExpandExpression</c>, and a problem's
/// <c>Acknowledged</c> read as a boolean.
/// </summary>
public sealed class ProblemCommandsTests
{
    private const string OneProblem = """[{"eventid":"501","name":"Too many requests on web-01","severity":"4","acknowledged":"0","clock":"1700000000"}]""";

    private static async Task<FakeZabbix> ServerAsync()
    {
        FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("problem.get", OneProblem);
        server.On("event.get", """[{"eventid":"502","name":"Disk full","acknowledged":"1"}]""");
        server.On("event.acknowledge", request => """{"eventids":[""" + string.Join(",", request.GetProperty("eventids").EnumerateArray().Select(id => "\"" + id.GetString() + "\"")) + "]}");
        return server;
    }

    private static JsonElement Acknowledged(FakeZabbix server) => Assert.Single(server.ParamsOf("event.acknowledge"));

    [Fact]
    public async Task A_piped_problem_is_acknowledged_by_its_event_id()
    {
        await using FakeZabbix server = await ServerAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxProblem | Confirm-ZbxProblem");

        Assert.Empty(run.Errors);
        Assert.Empty(run.Output);
        JsonElement request = Acknowledged(server);
        Assert.Equal(["501"], request.GetProperty("eventids").EnumerateArray().Select(id => id.GetString()));
        Assert.Equal((int)ZabbixEventActions.Acknowledge, request.GetProperty("action").GetInt32());
        Assert.False(request.TryGetProperty("message", out _));
        Assert.False(request.TryGetProperty("suppress_until", out _));
    }

    [Fact]
    public async Task A_piped_event_binds_by_its_EventId_and_an_id_by_value()
    {
        await using FakeZabbix server = await ServerAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run piped = shell.Invoke("Get-ZbxEvent | Confirm-ZbxProblem");
        Run byId = shell.Invoke("Confirm-ZbxProblem 503, 504");

        Assert.Empty(piped.Errors);
        Assert.Empty(byId.Errors);
        string[][] sent = [.. server.ParamsOf("event.acknowledge").Select(request => request.GetProperty("eventids").EnumerateArray().Select(id => id.GetString()!).ToArray())];
        Assert.Equal([["502"], ["503", "504"]], sent);
    }

    [Fact]
    public async Task Every_change_is_its_bit_of_the_action_mask_with_its_field()
    {
        await using FakeZabbix server = await ServerAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Confirm-ZbxProblem 501 -Message 'Looking into it' -Severity Disaster -SuppressUntil 1900000000");

        Assert.Empty(run.Errors);
        JsonElement request = Acknowledged(server);
        Assert.Equal((int)(ZabbixEventActions.Acknowledge | ZabbixEventActions.Message | ZabbixEventActions.Severity | ZabbixEventActions.Suppress), request.GetProperty("action").GetInt32());
        Assert.Equal("Looking into it", request.GetProperty("message").GetString());
        Assert.Equal((int)ZabbixSeverity.Disaster, request.GetProperty("severity").GetInt32());
        Assert.Equal(1900000000, request.GetProperty("suppress_until").GetInt64());
    }

    [Theory]
    [InlineData("-Unacknowledge", ZabbixEventActions.Unacknowledge)]
    [InlineData("-NoAcknowledge -Message m", ZabbixEventActions.Message)]
    [InlineData("-Close", ZabbixEventActions.Acknowledge | ZabbixEventActions.Close)]
    [InlineData("-Unsuppress -NoAcknowledge", ZabbixEventActions.Unsuppress)]
    [InlineData("-ConvertToCause -NoAcknowledge", ZabbixEventActions.RankToCause)]
    public async Task A_switch_sets_its_action(string switches, ZabbixEventActions action)
    {
        await using FakeZabbix server = await ServerAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Confirm-ZbxProblem 501 " + switches);

        Assert.Empty(run.Errors);
        Assert.Equal((int)action, Acknowledged(server).GetProperty("action").GetInt32());
    }

    [Fact]
    public async Task Suppress_alone_suppresses_until_unsuppressed_and_a_cause_ranks_the_problems_as_its_symptoms()
    {
        await using FakeZabbix server = await ServerAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Confirm-ZbxProblem 501 -Suppress; Confirm-ZbxProblem 501 -CauseEventId 400 -NoAcknowledge");

        Assert.Empty(run.Errors);
        JsonElement[] requests = [.. server.ParamsOf("event.acknowledge")];
        Assert.Equal(0, requests[0].GetProperty("suppress_until").GetInt64());
        Assert.Equal((int)ZabbixEventActions.RankToSymptom, requests[1].GetProperty("action").GetInt32());
        Assert.Equal("400", requests[1].GetProperty("cause_eventid").GetString());
    }

    [Theory]
    [InlineData("-Suppress -Unsuppress")]
    [InlineData("-Close -SuppressUntil 1900000000")]
    [InlineData("-Unacknowledge -NoAcknowledge")]
    [InlineData("-ConvertToCause -CauseEventId 400")]
    [InlineData("-NoAcknowledge")]
    public async Task A_combination_Zabbix_refuses_is_refused_before_anything_is_sent(string switches)
    {
        await using FakeZabbix server = await ServerAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Confirm-ZbxProblem 501 " + switches);

        Assert.StartsWith("ZabbixParameterInvalid,", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Empty(server.ParamsOf("event.acknowledge"));
    }

    [Fact]
    public async Task The_confirmation_names_each_problem_and_the_update_and_WhatIf_sends_nothing()
    {
        await using FakeZabbix server = await ServerAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run confirmed = shell.Invoke("Get-ZbxProblem | Confirm-ZbxProblem -Message m -Close -Confirm:$false -Verbose");
        Run previewed = shell.Invoke("Get-ZbxProblem | Confirm-ZbxProblem -WhatIf");

        Assert.Empty(confirmed.Errors);
        Assert.Contains(confirmed.Verbose, line => line.Contains("\"Acknowledge, add a message, close\" on target \"problem 'Too many requests on web-01' (501)\"", StringComparison.Ordinal));
        Assert.Empty(previewed.Errors);
        Assert.Single(server.ParamsOf("event.acknowledge"));
    }

    [Fact]
    public async Task PassThru_reads_the_problems_back_resolved_ones_included()
    {
        await using FakeZabbix server = await ServerAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Confirm-ZbxProblem 501 -PassThru");

        Assert.Empty(run.Errors);
        Assert.IsType<ZabbixProblem>(Assert.Single(run.Output).BaseObject);
        JsonElement readBack = Assert.Single(server.ParamsOf("problem.get"));
        Assert.Equal(["501"], readBack.GetProperty("eventids").EnumerateArray().Select(id => id.GetString()));
        Assert.True(readBack.GetProperty("recent").GetBoolean());
    }

    [Fact]
    public async Task A_problem_reads_Acknowledged_as_a_boolean()
    {
        await using FakeZabbix server = await ServerAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("(Get-ZbxProblem).Acknowledged; (Get-ZbxEvent).Acknowledged");

        Assert.Empty(run.Errors);
        Assert.Equal([false, true], run.Output.Select(value => (bool)value.BaseObject));
    }

    [Fact]
    public async Task Get_ZbxProblem_Severity_sends_the_severities()
    {
        await using FakeZabbix server = await ServerAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxProblem -Severity High, Disaster");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("problem.get"));
        Assert.Equal([(int)ZabbixSeverity.High, (int)ZabbixSeverity.Disaster], request.GetProperty("severities").EnumerateArray().Select(severity => severity.GetInt32()));
    }

    [Fact]
    public async Task Get_ZbxTrigger_ExpandExpression_sends_expandExpression_true()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("trigger.get", """[{"triggerid":"7","description":"Too many requests on web-01","expression":"last(/web-01/app.requests)>100"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("(Get-ZbxTrigger -ExpandExpression).Expression");

        Assert.Empty(run.Errors);
        Assert.Equal("last(/web-01/app.requests)>100", Assert.Single(run.Output).BaseObject);
        Assert.True(Assert.Single(server.ParamsOf("trigger.get")).GetProperty("expandExpression").GetBoolean());
    }
}
