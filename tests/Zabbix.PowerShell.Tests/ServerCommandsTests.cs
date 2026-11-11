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
    public async Task Get_ZbxServerStatus_shapes_the_reply_as_ConvertFrom_Json_would()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("status.get", """{"response":"success","data":{"host":{"count":3}}}""");
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("Get-ZbxServerStatus");

        Assert.Empty(run.Errors);
        PSObject shaped = Assert.Single(run.Output);
        var host = Assert.IsAssignableFrom<PSObject>(shaped.Properties["host"].Value);
        Assert.Equal(3L, host.Properties["count"].Value);
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

    [Fact]
    public async Task Test_ZbxItem_sends_the_datas_shell_shape_and_shapes_the_reply_back()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("item.test", request => request.GetProperty("data").GetProperty("item").GetProperty("key_").GetString() == "agent.ping"
            ? """{"response":"success","data":{"result":"1"}}"""
            : """{"response":"failed","info":"unexpected key"}""");
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("""Test-ZbxItem -Data @{ item = @{ key_ = 'agent.ping' } }""");

        Assert.Empty(run.Errors);
        PSObject shaped = Assert.Single(run.Output);
        Assert.Equal("1", shaped.Properties["result"].Value);
    }

    [Fact]
    public async Task Test_ZbxItem_writes_the_replys_envelope_when_the_server_sends_no_data_never_null()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("item.test", _ => """{"response":"success"}""");
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("""Test-ZbxItem -Data @{ item = @{ key_ = 'agent.ping' } }""");

        Assert.Empty(run.Errors);
        PSObject shaped = Assert.Single(run.Output);
        Assert.NotNull(shaped);
        Assert.Equal("success", shaped.Properties["response"].Value);
    }

    [Fact]
    public async Task Test_ZbxMediaType_resolves_the_name_then_sends_the_alert()
    {
        await using FakeZabbix api = await FakeZabbix.StartAsync();
        api.On("mediatype.get", """[{"mediatypeid":"7","name":"Email"}]""");
        await using FakeTrapper trapper = FakeTrapper.Start();
        trapper.On("alert.send", request => request.GetProperty("data").GetProperty("mediatypeid").GetString() == "7"
                                             && request.GetProperty("data").GetProperty("sendto").GetString() == "ops@example.com"
            ? """{"response":"success","data":{"eventid":"0"}}"""
            : """{"response":"failed","info":"unexpected data"}""");
        using Shell shell = Shell.Open();
        shell.Connect(api, $"-Trapper 127.0.0.1:{trapper.Port}");

        Run run = shell.Invoke("Test-ZbxMediaType Email -SendTo ops@example.com -Confirm:$false");

        Assert.Empty(run.Errors);
        Assert.Single(run.Output);
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
            ? """{"response":"success","data":{"eventid":"0"}}"""
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
