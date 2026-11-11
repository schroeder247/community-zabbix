// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The server wire: the frontend's requests on the trapper port, in the shapes <c>CZabbixServer.php</c> sends and
/// <c>src/libs/zbxtrapper/trapper.c</c> reads (<c>type</c>/<c>limit</c> at the root, <c>data</c> only where the handler
/// reads it), and the server's reply shape (<c>response</c>, <c>data</c>, <c>info</c>, <c>total</c>; a refusal is
/// <c>"response":"failed"</c> with <c>info</c> and nothing else — there is no <c>error</c> field).
/// </summary>
public sealed class ZabbixServerWireTests
{
    /// <summary>A trapper double answering as <c>trapper.c</c> does, and recording each request frame.</summary>
    private sealed class FakeTrapper
    {
        public List<JsonElement> Sent { get; } = [];

        public Func<JsonElement, string> Answer { get; init; } = Answering;

        public ZabbixExchangeCallback Exchange => (request, _) =>
        {
            JsonElement frame = JsonDocument.Parse(request).RootElement.Clone();
            Sent.Add(frame);
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(Answer(frame)));
        };

        private static string Answering(JsonElement frame)
        {
            bool root(string name, out JsonElement value) => frame.TryGetProperty(name, out value);
            string? type = root("type", out JsonElement t) ? t.GetString() : null;
            return frame.GetProperty("request").GetString() switch
            {
                "queue.get" when type == "overview" => """{"response":"success","data":[{"itemtype":0,"delay5":3,"delay10":1,"delay30":0,"delay60":0,"delay300":0,"delay600":0}]}""",
                "queue.get" when type == "overview by proxy" => """{"response":"success","data":[{"proxyid":0,"delay5":3,"delay10":1,"delay30":0,"delay60":0,"delay300":0,"delay600":0}]}""",
                "queue.get" when type == "details" && root("limit", out _) => """{"response":"success","data":[{"itemid":42,"nextcheck":1700000000}],"total":7}""",
                "queue.get" when type == "details" => """{"response":"failed","info":"Unsupported limit value."}""",
                "queue.get" => """{"response":"failed","info":"Unsupported request type."}""",
                "status.get" when type is "ping" => """{"response":"success","data":{}}""",
                "status.get" when type is "full" => """{"response":"success","data":{"server stats":{"version":"7.0.31"}}}""",
                "status.get" => """{"response":"failed","info":"Unsupported request type."}""",
                "zabbix.stats" when type == "queue" => """{"response":"success","queue":5}""",
                "zabbix.stats" => """{"response":"success","data":{"boottime":1700000000,"uptime":42}}""",
                "expressions.evaluate" => """{"response":"success","data":[{"expression":"1=1","value":1},{"expression":"x","error":"bad"}]}""",
                _ => """{"response":"failed","info":"Permission denied."}""",
            };
        }
    }

    [Fact]
    public async Task Queue_requests_carry_type_and_limit_at_the_root_as_the_trapper_reads_them()
    {
        var trapper = new FakeTrapper();
        using var wire = new ZabbixServerWire(trapper.Exchange, "sid-123");

        IReadOnlyList<ZabbixQueueDelays> byType = await wire.GetQueueAsync(ZabbixQueueOverview.ByItemType, TestContext.Current.CancellationToken);
        IReadOnlyList<ZabbixQueueDelays> byProxy = await wire.GetQueueAsync(ZabbixQueueOverview.ByProxy, TestContext.Current.CancellationToken);
        ZabbixQueueDetails details = await wire.GetQueueDetailsAsync(5, TestContext.Current.CancellationToken);

        Assert.Equal("overview", trapper.Sent[0].GetProperty("type").GetString());
        Assert.Equal("overview by proxy", trapper.Sent[1].GetProperty("type").GetString());
        Assert.Equal("details", trapper.Sent[2].GetProperty("type").GetString());
        Assert.Equal(5, trapper.Sent[2].GetProperty("limit").GetInt32());
        Assert.All(trapper.Sent, frame => Assert.False(frame.TryGetProperty("data", out _)));
        Assert.All(trapper.Sent, frame => Assert.Equal("sid-123", frame.GetProperty("sid").GetString()));
        Assert.Equal((0, 3L), (byType[0].ItemType, byType[0].Delay5));
        Assert.Equal(0UL, byProxy[0].ProxyId);
        Assert.Equal(42UL, details.Items[0].ItemId);
        Assert.Equal(new ZabbixClock(1_700_000_000, 0), details.Items[0].NextCheck);
        Assert.Equal(7, details.Total);
    }

    [Fact]
    public async Task Status_and_ping_carry_their_type_at_the_root()
    {
        var trapper = new FakeTrapper();
        using var wire = new ZabbixServerWire(trapper.Exchange, "sid-123");

        ZabbixServerStatus status = await wire.GetStatusAsync(TestContext.Current.CancellationToken);
        await wire.PingAsync(TestContext.Current.CancellationToken);

        Assert.Equal("full", trapper.Sent[0].GetProperty("type").GetString());
        Assert.Equal("ping", trapper.Sent[1].GetProperty("type").GetString());
        Assert.Equal(new Version(7, 0, 31), status.Version);
    }

    [Fact]
    public async Task Stats_go_without_a_session_and_the_queue_size_carries_its_params()
    {
        var trapper = new FakeTrapper();
        using var wire = new ZabbixServerWire(trapper.Exchange, "sid-123");

        ZabbixServerStatistics stats = await wire.GetStatisticsAsync(TestContext.Current.CancellationToken);
        long queue = await wire.GetQueueSizeAsync(TimeSpan.FromMinutes(10), cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(42), stats.Uptime);
        Assert.False(trapper.Sent[0].TryGetProperty("sid", out _));
        Assert.Equal("queue", trapper.Sent[1].GetProperty("type").GetString());
        Assert.Equal("600", trapper.Sent[1].GetProperty("params").GetProperty("from").GetString());
        Assert.Equal(5, queue);
    }

    [Fact]
    public async Task Expressions_go_under_data_as_the_trapper_reads_them()
    {
        var trapper = new FakeTrapper();
        using var wire = new ZabbixServerWire(trapper.Exchange, "sid-123");

        IReadOnlyList<ZabbixExpressionResult> evaluated = await wire.EvaluateExpressionsAsync(["1=1", "x"], TestContext.Current.CancellationToken);

        Assert.Equal(["1=1", "x"], trapper.Sent[0].GetProperty("data").GetProperty("expressions").EnumerateArray().Select(e => e.GetString()));
        Assert.True(evaluated[0].IsTrue);
        Assert.Equal("bad", evaluated[1].Error);
    }

    [Fact]
    public async Task A_report_test_carries_its_data_and_the_session_and_answers_the_server_s_reply()
    {
        // CZabbixServer::testReport: {"request":"report.test","sid":…,"data":{…}}; zbx_report_test answers the recipients.
        var trapper = new FakeTrapper { Answer = _ => """{"response":"success","data":{"recipients":[{"status":1,"recipient":"Admin"},{"status":0,"recipient":"ops","info":"No media."}]}}""" };
        using var wire = new ZabbixServerWire(trapper.Exchange, "sid-123");
        var request = new ZabbixReportTestRequest("weekly", 1, 1) { Period = 1, Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), Parameters = new Dictionary<string, string> { ["subject"] = "s" } };

        ZabbixReportTestResult result = await wire.TestReportAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal("report.test", trapper.Sent[0].GetProperty("request").GetString());
        Assert.Equal("sid-123", trapper.Sent[0].GetProperty("sid").GetString());
        Assert.Equal("""{"name":"weekly","dashboardid":1,"userid":1,"period":1,"now":1700000000,"params":{"subject":"s"}}""", trapper.Sent[0].GetProperty("data").GetRawText());
        Assert.Equal([1, 0], result.Recipients!.Select(recipient => recipient.Status!.Value));
        Assert.Equal("No media.", result.Recipients![1].Info);
    }

    [Fact]
    public async Task A_refused_report_test_throws_the_server_s_info()
    {
        var trapper = new FakeTrapper { Answer = _ => """{"response":"failed","info":"Cannot send report: no media."}""" };
        using var wire = new ZabbixServerWire(trapper.Exchange, "sid-123");

        ZabbixServerRefusedException refused = await Assert.ThrowsAsync<ZabbixServerRefusedException>(() => wire.TestReportAsync(new ZabbixReportTestRequest("weekly", 1, 1), TestContext.Current.CancellationToken));

        Assert.Contains("Cannot send report: no media.", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_test_alert_carries_a_webhook_s_parameters_by_name_and_a_script_s_in_order()
    {
        // The parameters are sent: without them a webhook would be tested without its inputs.
        var trapper = new FakeTrapper { Answer = _ => """{"response":"success","data":"{}"}""" };
        using var wire = new ZabbixServerWire(trapper.Exchange, "sid-123");

        ZabbixAlertSendResult sent = await wire.SendAlertAsync(7, "ops", "s", "m", ZabbixAlertParameters.Webhook([new("URL", "https://x"), new("To", "{ALERT.SENDTO}")]), TestContext.Current.CancellationToken);
        await wire.SendAlertAsync(8, "ops", "s", "m", ZabbixAlertParameters.Script(["{ALERT.SENDTO}", "b"]), TestContext.Current.CancellationToken);
        await wire.SendAlertAsync(9, "ops", "s", "m", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("""{"URL":"https://x","To":"{ALERT.SENDTO}"}""", trapper.Sent[0].GetProperty("data").GetProperty("parameters").GetRawText());
        Assert.Equal("""["{ALERT.SENDTO}","b"]""", trapper.Sent[1].GetProperty("data").GetProperty("parameters").GetRawText());
        Assert.False(trapper.Sent[2].GetProperty("data").TryGetProperty("parameters", out _));
        Assert.Equal("{}", sent.Value);
    }

    [Fact]
    public async Task A_refusal_throws_the_server_s_info_and_logs_it_as_a_warning()
    {
        var logs = new FakeLogCollector();
        var trapper = new FakeTrapper();
        using var wire = new ZabbixServerWire(trapper.Exchange, "bad-sid", logger: new FakeLogger<ZabbixServerWire>(logs), node: null);

        ZabbixServerRefusedException refused = await Assert.ThrowsAsync<ZabbixServerRefusedException>(() => wire.SendAlertAsync(1, "ops@example", "s", "m", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal("alert.send", refused.Request);
        Assert.Equal("Permission denied.", refused.Info);
        Assert.Equal("refused", ((IZabbixErrorTyped)refused).ErrorType);
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 1301);
    }

    [Fact]
    public async Task A_reply_that_is_not_the_server_s_shape_throws_invalid_data_and_logs_an_error()
    {
        var logs = new FakeLogCollector();
        var trapper = new FakeTrapper { Answer = _ => """{"data":{}}""" };
        using var wire = new ZabbixServerWire(trapper.Exchange, "sid", logger: new FakeLogger<ZabbixServerWire>(logs), node: null);

        await Assert.ThrowsAsync<InvalidDataException>(() => wire.GetStatusAsync(TestContext.Current.CancellationToken));

        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 1302);
    }

    [Fact]
    public async Task A_success_without_the_data_the_request_promises_throws_rather_than_answering_a_default()
    {
        var trapper = new FakeTrapper { Answer = _ => """{"response":"success"}""" };
        using var wire = new ZabbixServerWire(trapper.Exchange, "sid");

        await Assert.ThrowsAsync<InvalidDataException>(() => wire.GetStatisticsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_connection_reset_mid_exchange_is_thrown_as_the_exchange_raised_it_with_the_trapper_and_the_request_in_its_data()
    {
        // The raw failure a broken trapper connection gives ("Connection reset by peer") names neither the server nor the
        // request. It stays the type the sender and the getter throw for the same failure, and carries both in its Data.
        var node = new ZabbixServerNode("localhost", 10051);
        var reset = new IOException("Unable to read data from the transport connection: Connection reset by peer.", new SocketException((int)SocketError.ConnectionReset));
        ZabbixExchangeCallback breaking = (_, _) => throw reset;
        using var wire = new ZabbixServerWire(breaking, "sid-123", logger: null, node: node);

        IOException failure = await Assert.ThrowsAsync<IOException>(() => wire.GetStatusAsync(TestContext.Current.CancellationToken));

        Assert.Same(reset, failure);
        Assert.Equal("status.get", failure.Data["Zabbix.Request"]);
        Assert.Equal("localhost:10051", failure.Data["Zabbix.Node"]);
    }

    [Fact]
    public async Task A_deadline_is_a_timeout_exception_from_the_server_wire_as_from_the_sender_and_the_getter()
    {
        ZabbixExchangeCallback late = (_, _) => throw new TimeoutException("The exchange passed its deadline.");
        using var wire = new ZabbixServerWire(late, "sid");
        using var getter = new ZabbixGetter(late, "agent");
        using var sender = new ZabbixSender(late, "server");

        await Assert.ThrowsAsync<TimeoutException>(() => wire.GetStatusAsync(TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TimeoutException>(() => getter.GetAsync("agent.ping", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<TimeoutException>(() => sender.SendAsync([new ZabbixValue("h", "k", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_connection_reset_is_logged_at_debug_naming_the_request_and_the_trapper_because_the_caller_owns_the_exception()
    {
        var logs = new FakeLogCollector();
        var node = new ZabbixServerNode("localhost", 10051);
        ZabbixExchangeCallback breaking = (_, _) => throw new IOException("Connection reset by peer.", new SocketException((int)SocketError.ConnectionReset));
        using var wire = new ZabbixServerWire(breaking, "sid", logger: new FakeLogger<ZabbixServerWire>(logs), node: node);

        await Assert.ThrowsAsync<IOException>(() => wire.GetQueueAsync(cancellationToken: TestContext.Current.CancellationToken));

        FakeLogRecord failed = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 1302);
        Assert.Equal(LogLevel.Debug, failed.Level);
        Assert.Contains("localhost:10051", failed.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_reply_that_is_not_the_server_s_shape_is_invalid_data()
    {
        var trapper = new FakeTrapper { Answer = _ => """{"data":{}}""" };
        using var wire = new ZabbixServerWire(trapper.Exchange, "sid");

        await Assert.ThrowsAsync<InvalidDataException>(() => wire.GetStatusAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_disposed_server_wire_throws_naming_itself_before_any_exchange()
    {
        var trapper = new FakeTrapper();
        var wire = new ZabbixServerWire(trapper.Exchange, "sid");
        wire.Dispose();

        Task<ZabbixServerStatus>? pending = null;
        ObjectDisposedException disposed = Assert.Throws<ObjectDisposedException>(() => { pending = wire.GetStatusAsync(TestContext.Current.CancellationToken); });

        Assert.Null(pending);
        Assert.Equal(typeof(ZabbixServerWire).FullName, disposed.ObjectName);
    }

    [Fact]
    public async Task A_request_leaves_a_span_and_a_duration_tagged_with_its_outcome()
    {
        using var capture = new TelemetryCapture(ZabbixTelemetryNames.Wire);
        using var telemetry = new ZabbixWireTelemetry(capture.Meters);
        using var durations = new MetricCollector<double>(capture.Meters, ZabbixTelemetryNames.Wire, ZabbixWireTelemetry.DurationInstrument);
        var trapper = new FakeTrapper();
        using var wire = new ZabbixServerWire(trapper.Exchange, "bad", logger: null, telemetry: telemetry, node: null);

        await wire.PingAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ZabbixServerRefusedException>(() => wire.TestItemAsync(new ZabbixItemTestRequest(new ZabbixItemTestItem()), TestContext.Current.CancellationToken));

        Assert.Equal(["status.get", "item.test"], capture.Spans.Select(span => span.DisplayName));
        Assert.Equal(ActivityStatusCode.Ok, capture.Spans[0].Status);
        Assert.Equal(ActivityStatusCode.Error, capture.Spans[1].Status);
        Assert.Equal("refused", capture.Spans[1].GetTagItem("error.type"));
        IReadOnlyList<CollectedMeasurement<double>> samples = durations.GetMeasurementSnapshot();
        Assert.Equal(2, samples.Count);
        Assert.False(samples[0].Tags.ContainsKey("error.type"));
        Assert.Equal("refused", samples[1].Tags["error.type"]);
        Assert.Equal("item.test", samples[1].Tags["rpc.method"]);
    }
}
