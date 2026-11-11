// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>The server-wire commands: <c>Get-ZbxQueue</c>, <c>Get-ZbxServerStatus</c>, <c>Test-ZbxExpression</c>, <c>Test-ZbxItem</c>, <c>Test-ZbxMediaType</c>, <c>Get-ZbxServerInfo</c>.</summary>
public sealed class ServerCommandsTests
{
    [Fact]
    public async Task Get_ZbxQueue_reads_the_overview_from_the_trapper()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("queue.get", request => request.GetProperty("type").GetString() == "overview"
            ? """{"response":"success","data":[{"itemtype":0,"delay5":1,"delay10":0,"delay30":0,"delay60":0,"delay300":0,"delay600":0}]}"""
            : """{"response":"failed","info":"unexpected type"}""");
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("Get-ZbxQueue");

        Assert.Empty(run.Errors);
        object row = Assert.Single(run.Output).BaseObject;
        Assert.Equal(1L, ((ZabbixQueueDelays)row).Delay5);
    }

    [Fact]
    public async Task Get_ZbxQueue_Details_reads_the_overdue_items_and_the_total()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("queue.get", request => request.GetProperty("type").GetString() == "details"
            ? """{"response":"success","data":[{"itemid":"501","nextcheck":1758000000}],"total":7}"""
            : """{"response":"failed","info":"unexpected type"}""");
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("Get-ZbxQueue -Details -Limit 5");

        Assert.Empty(run.Errors);
        var details = Assert.IsType<ZabbixQueueDetails>(Assert.Single(run.Output).BaseObject);
        Assert.Equal(7, details.Total);
        Assert.Equal(501UL, details.Items[0].ItemId);
        Assert.Equal(5, Assert.Single(trapper.FramesOf("queue.get")).GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Get_ZbxServerStatus_writes_the_status_counts_with_each_proxy_s()
    {
        // status_stats_export (trapper.c): an entry per what it counts by, proxy 0 for the whole installation.
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("status.get", """{"response":"success","data":{"template stats":[{"count":312}],"host stats":[{"attributes":{"proxyid":0,"status":0},"count":3},{"attributes":{"proxyid":0,"status":1},"count":1},{"attributes":{"proxyid":11,"status":0},"count":2},{"attributes":{"proxyid":11,"status":1},"count":0}],"server stats":{"version":"7.0.31"}}}""");
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("Get-ZbxServerStatus");

        Assert.Empty(run.Errors);
        var status = Assert.IsType<ZabbixServerStatus>(Assert.Single(run.Output).BaseObject);
        Assert.Equal((312L, 3L, 1L), (status.TemplateCount!.Value, status.EnabledHostCount!.Value, status.DisabledHostCount!.Value));
        Assert.Equal(2L, Assert.Single(status.Proxies).EnabledHostCount);
        Assert.Equal(new Version(7, 0, 31), status.Version);
    }

    [Fact]
    public async Task A_refused_wire_request_is_one_non_terminating_error_named_by_its_kind()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("status.get", """{"response":"failed","info":"Permission denied."}""");
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("Get-ZbxServerStatus");

        Assert.Empty(run.Output);
        Assert.Contains("Permission denied.", Assert.Single(run.Errors).Exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_ZbxExpression_collects_the_pipeline_into_one_evaluate_call()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("expressions.evaluate", request =>
        {
            int count = request.GetProperty("data").GetProperty("expressions").GetArrayLength();
            return count == 2
                ? """{"response":"success","data":[{"expression":"a","value":1},{"expression":"b","value":0}]}"""
                : """{"response":"failed","info":"wrong count"}""";
        });
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("""'a', 'b' | Test-ZbxExpression""");

        Assert.Empty(run.Errors);
        Assert.Equal(2, run.Output.Count);
        Assert.True(((ZabbixExpressionResult)run.Output[0].BaseObject).IsTrue);
        Assert.False(((ZabbixExpressionResult)run.Output[1].BaseObject).IsTrue);
    }

    [Theory]
    [InlineData("@{ item = @{ key = 'agent.ping'; value_type = 3 }; host = @{ interface = @{ address = '10.0.0.2'; useip = 1; port = '10050' } } }")]
    [InlineData("@{ Item = @{ Key = 'agent.ping'; ValueType = 3 }; Host = @{ Interface = @{ Address = '10.0.0.2'; UseIP = $true; Port = '10050' } } }")]
    [InlineData("[Community.Zabbix.ZabbixItemTestRequest]::new([Community.Zabbix.ZabbixItemTestItem]@{ Key = 'agent.ping'; ValueType = 3 }) | ForEach-Object { $_.Host = [Community.Zabbix.ZabbixItemTestHost]@{ Interface = [Community.Zabbix.ZabbixItemTestInterface]@{ Address = '10.0.0.2'; UseIP = $true; Port = 10050 } }; $_ }")]
    public async Task Test_ZbxItem_sends_the_request_by_wire_or_member_names_and_writes_the_test_s_result(string data)
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("item.test", request => request.GetProperty("data") is var sent
            && sent.GetProperty("item").GetProperty("key").GetString() == "agent.ping"
            && sent.GetProperty("host").GetProperty("interface").GetProperty("address").GetString() == "10.0.0.2"
            && sent.GetProperty("host").GetProperty("interface").GetProperty("useip").GetString() == "1"
            ? """{"response":"success","data":{"item":{"result":"1","eol":"LF"},"preprocessing":{"steps":[],"result":"1"}}}"""
            : """{"response":"failed","info":"unexpected request"}""");
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke($"Test-ZbxItem -Data ({data})");

        Assert.Empty(run.Errors);
        var result = Assert.IsType<ZabbixItemTestResult>(Assert.Single(run.Output).BaseObject);
        Assert.Equal(("1", "LF", "1"), (result.Item!.Result, result.Item.EndOfLine, result.Preprocessing!.Result));
    }

    [Fact]
    public async Task Test_ZbxItem_passes_a_field_the_request_does_not_model_through_by_its_wire_name()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("item.test", request => request.GetProperty("data").GetProperty("item").GetProperty("lookback_limit").GetString() == "1h"
            ? """{"response":"success","data":{"item":{"result":"1"}}}"""
            : """{"response":"failed","info":"unexpected request"}""");
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("""Test-ZbxItem -Data @{ item = @{ key = 'telemetry.query'; lookback_limit = '1h' } }""");

        Assert.Empty(run.Errors);
        Assert.Equal("1", Assert.IsType<ZabbixItemTestResult>(Assert.Single(run.Output).BaseObject).Item!.Result);
    }

    [Fact]
    public async Task Test_ZbxItem_reports_a_success_without_its_data_as_an_error_never_a_null()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("item.test", _ => """{"response":"success"}""");
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("""Test-ZbxItem -Data @{ item = @{ key = 'agent.ping' } }""");

        Assert.Empty(run.Output);
        Assert.IsType<InvalidDataException>(Assert.Single(run.Errors).Exception);
    }

    [Fact]
    public async Task Test_ZbxMediaType_resolves_the_name_then_sends_the_alert()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        api.On("mediatype.get", """[{"mediatypeid":"7","name":"Email"}]""");
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("alert.send", request => request.GetProperty("data").GetProperty("mediatypeid").GetString() == "7"
                                             && request.GetProperty("data").GetProperty("sendto").GetString() == "ops@example.com"
            ? """{"response":"success"}"""
            : """{"response":"failed","info":"unexpected data"}""");
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("Test-ZbxMediaType Email -SendTo ops@example.com -Confirm:$false");

        Assert.Empty(run.Errors);
        var sent = Assert.IsType<ZabbixAlertSendResult>(Assert.Single(run.Output).BaseObject);
        Assert.Null(sent.Value);                                                        // an e-mail answers nothing but success
    }

    [Fact]
    public async Task Test_ZbxMediaType_writes_a_webhook_s_value_and_its_log()
    {
        // trapper_process_alert_send: the webhook's value is data, its Zabbix.log lines are the root's debug.
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        api.On("mediatype.get", """[{"mediatypeid":"7","name":"Hook","type":"4","parameters":[]}]""");
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("alert.send", _ => """{"response":"success","data":"{\"tags\":{}}","debug":{"logs":[{"level":4,"ms":3,"message":"sent"}],"ms":15}}""");
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("Test-ZbxMediaType Hook -SendTo ops -Confirm:$false");

        Assert.Empty(run.Errors);
        var sent = Assert.IsType<ZabbixAlertSendResult>(Assert.Single(run.Output).BaseObject);
        Assert.Equal("""{"tags":{}}""", sent.Value);
        Assert.Equal(("sent", TimeSpan.FromMilliseconds(15)), (Assert.Single(sent.Log!.Entries!).Message, sent.Log.Elapsed!.Value));
    }

    [Theory]
    [InlineData("", """{"URL":"https://hook","To":"{ALERT.SENDTO}"}""")]                  // the saved parameters, as the frontend's form
    [InlineData("-Parameter @{ URL = 'https://other' }", """{"URL":"https://other"}""")]
    public async Task Test_ZbxMediaType_sends_a_webhook_s_parameters(string given, string sent)
    {
        // alert.send carries the parameters: without them a webhook would be tested without its inputs.
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        api.On("mediatype.get", """[{"mediatypeid":"7","name":"Hook","type":"4","parameters":[{"name":"URL","value":"https://hook"},{"name":"To","value":"{ALERT.SENDTO}"}]}]""");
        await using FakeTrapper trapper = FakeTrapper.Start();
        string? parameters = null;
        trapper.On("alert.send", request =>
        {
            parameters = request.GetProperty("data").GetProperty("parameters").GetRawText();
            return """{"response":"success","data":"{}"}""";
        });
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke($"Test-ZbxMediaType Hook -SendTo ops {given} -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Equal(sent, parameters);
    }

    [Fact]
    public async Task A_piped_media_type_binds_to_Test_ZbxMediaType()
    {
        // Get-ZbxMediaType | Test-ZbxMediaType binds by property name: the object carries MediaTypeId, not Id.
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        api.On("mediatype.get", """[{"mediatypeid":"7","name":"Email"}]""");
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("alert.send", request => request.GetProperty("data").GetProperty("mediatypeid").GetString() == "7"
            ? """{"response":"success"}"""
            : """{"response":"failed","info":"unexpected data"}""");
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("Get-ZbxMediaType Email | Test-ZbxMediaType -SendTo ops@example.com -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Single(run.Output);
    }

    [Fact]
    public async Task Get_ZbxServerInfo_reports_the_connections_own_facts()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(api, "-Trapper trapper.example:10052, trapper-b.example");

        Run run = shell.Invoke("Get-ZbxServerInfo");

        Assert.Empty(run.Errors);
        PSObject info = Assert.Single(run.Output);
        Assert.Equal(["trapper.example:10052", "trapper-b.example:10051"], ((IEnumerable<System.Net.DnsEndPoint>)info.Properties["Trapper"].Value!).Select(ZabbixEndPoint.Format));
        Assert.Equal(new Version(7, 0, 31), info.Properties["ApiVersion"].Value);
    }
}
