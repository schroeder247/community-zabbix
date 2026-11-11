// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>The JSON-RPC transport: the envelope and bearer auth on the wire; the reply's error, id and result read honestly; every exchange logged out and in.</summary>
public sealed class JsonRpcTransportTests
{
    [Fact]
    public async Task A_request_carries_the_envelope_and_the_bearer_token()
    {
        using var handler = new CapturingHandler("""{"jsonrpc":"2.0","result":[{"hostid":"1"}],"id":1}""");
        using Harness harness = TransportHarness.Build(handler);

        List<HostRow> rows = await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestJson.Default.ListHostRow, TestContext.Current.CancellationToken);

        Assert.Equal("Bearer tok", handler.LastRequest!.Headers.Authorization!.ToString());
        using JsonDocument body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal("2.0", body.RootElement.GetProperty("jsonrpc").GetString());
        Assert.Equal("host.get", body.RootElement.GetProperty("method").GetString());
        Assert.Equal("extend", body.RootElement.GetProperty("params").GetProperty("output").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("id").GetInt64());
        Assert.Equal("1", Assert.Single(rows).HostId);
    }

    // 7.0 refuses a header on all three ("must be called without authorization header", measured on 7.0.31); 8.0 on the
    // last two (measured on 8.0.0rc1). The session credential's own login goes the same way.
    [Theory]
    [InlineData("apiinfo.version")]
    [InlineData("user.checkAuthentication")]
    [InlineData("user.login")]
    public async Task A_method_that_needs_no_authentication_is_sent_without_an_authorization_header(string method)
    {
        using var handler = new CapturingHandler("""{"jsonrpc":"2.0","result":"7.4.0","id":1}""");
        using Harness harness = TransportHarness.Build(handler);

        await harness.Transport.SendAsync(method, new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken);

        Assert.Null(handler.LastRequest!.Headers.Authorization);
    }

    // System.Text.Json's writer puts U+FFFD in place of a lone surrogate without a word, and PHP's json_decode
    // refuses one sent escaped; so it is the caller's argument, refused before the call, never sent changed.
    [Theory]
    [InlineData(0xD800)]
    [InlineData(0xDC00)]
    [InlineData(0xD83D)]
    public async Task A_lone_surrogate_in_a_parameter_is_refused_before_the_call_never_sent_replaced(int surrogate)
    {
        string output = "a" + (char)surrogate + "b";                   // built here: a lone surrogate does not survive test-case serialization
        using var handler = new CapturingHandler("""{"jsonrpc":"2.0","result":[],"id":1}""");
        using Harness harness = TransportHarness.Build(handler);

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await harness.Transport.SendAsync("host.get", new OutputParameters(output), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));

        Assert.Null(handler.LastRequest);
    }

    [Theory]
    [InlineData("a\uFFFDb")]
    [InlineData("\\uFFFD")]
    [InlineData("\uD83D\uDE00")]
    public async Task A_caller_s_own_replacement_character_and_a_whole_pair_are_sent_as_given(string output)
    {
        using var handler = new CapturingHandler("""{"jsonrpc":"2.0","result":[],"id":1}""");
        using Harness harness = TransportHarness.Build(handler);

        await harness.Transport.SendAsync("host.get", new OutputParameters(output), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken);

        using JsonDocument body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(output, body.RootElement.GetProperty("params").GetProperty("output").GetString());
    }

    [Fact]
    public async Task An_error_object_is_the_api_exception_with_its_code_and_detail()
    {
        using var handler = new CapturingHandler("""{"jsonrpc":"2.0","error":{"code":-32602,"message":"Invalid params","data":"detail"},"id":1}""");
        using Harness harness = TransportHarness.Build(handler);

        ZabbixApiException failure = await Assert.ThrowsAsync<ZabbixApiException>(async () =>
            await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));

        Assert.Equal(-32602, failure.Code);
        Assert.Equal("detail", failure.Detail);
        Assert.Equal("host.get", failure.Method);
        Assert.Equal(ZabbixApiErrorKind.Validation, failure.Kind);
        Assert.Contains(harness.Logs.GetSnapshot(), record => record.Id.Id == 4001 && record.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task An_api_error_s_message_says_what_the_server_said_was_wrong_not_only_the_generic_text()
    {
        // Measured on 7.0.31 and 8.0.0rc1: host.create with an empty host answers exactly this
        using var handler = new CapturingHandler("""{"jsonrpc":"2.0","error":{"code":-32602,"message":"Invalid params.","data":"Invalid parameter \"/1/host\": cannot be empty."},"id":1}""");
        using Harness harness = TransportHarness.Build(handler);

        ZabbixApiException failure = await Assert.ThrowsAsync<ZabbixApiException>(async () =>
            await harness.Transport.SendAsync("host.create", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));

        Assert.Equal("host.create: Invalid params. Invalid parameter \"/1/host\": cannot be empty.", failure.Message);
        Assert.Equal("Invalid params.", failure.ServerMessage);
        Assert.Equal("Invalid parameter \"/1/host\": cannot be empty.", failure.Detail);
        Assert.Equal((-32602, ZabbixApiErrorKind.Validation), (failure.Code, failure.Kind));
        // The detail is the peer's text, which echoes the request's values: classified, so the fallback redactor erases it
        // from the log, while the code, the kind and the server's generic message stay.
        FakeLogRecord logged = Assert.Single(harness.Logs.GetSnapshot(), record => record.Id.Id == 4001);
        Assert.Contains("Invalid params.", logged.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("cannot be empty", logged.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_api_error_without_a_detail_or_a_method_is_the_server_s_message()
    {
        Assert.Equal("Invalid params.", new ZabbixApiException(-32602, "Invalid params.", null, null).Message);
        Assert.Equal("host.get: Invalid params.", new ZabbixApiException(-32602, "Invalid params.", "", "host.get").Message);
    }

    [Fact]
    public async Task A_call_is_one_client_span_and_one_duration_under_the_current_rpc_names()
    {
        using var handler = new CapturingHandler("""{"jsonrpc":"2.0","result":[],"id":1}""");
        using Harness harness = TransportHarness.Build(handler);
        using var capture = new ApiTelemetryCapture(harness.Provider);

        await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken);

        Activity span = Assert.Single(capture.Spans);
        Assert.Equal("host.get", span.DisplayName);
        Assert.Equal(ActivityKind.Client, span.Kind);
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.Equal("jsonrpc", span.GetTagItem("rpc.system.name"));
        Assert.Equal("host.get", span.GetTagItem("rpc.method"));
        Assert.Equal("zbx.example", span.GetTagItem("server.address"));
        Assert.Equal(443, span.GetTagItem("server.port"));
        CollectedMeasurement<double> duration = Assert.Single(capture.Durations.GetMeasurementSnapshot());
        Assert.Equal("jsonrpc", duration.Tags["rpc.system.name"]);
        Assert.Equal("host.get", duration.Tags["rpc.method"]);
        Assert.Equal(443, duration.Tags["server.port"]);
        Assert.False(duration.Tags.ContainsKey("error.type"));
    }

    [Fact]
    public async Task An_api_error_fails_the_span_and_the_duration_with_its_status_code_and_its_kind()
    {
        using var handler = new CapturingHandler("""{"jsonrpc":"2.0","error":{"code":-32500,"message":"Application error.","data":"No permissions to referred object or it does not exist!"},"id":1}""");
        using Harness harness = TransportHarness.Build(handler);
        using var capture = new ApiTelemetryCapture(harness.Provider);

        await Assert.ThrowsAsync<ZabbixApiException>(async () =>
            await harness.Transport.SendAsync("host.update", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));

        Activity span = Assert.Single(capture.Spans);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("-32500", span.GetTagItem("error.type"));
        Assert.Equal("-32500", span.GetTagItem("rpc.response.status_code"));
        Assert.Equal("permission_or_not_found", span.GetTagItem("zabbix.api.error.kind"));
        CollectedMeasurement<double> duration = Assert.Single(capture.Durations.GetMeasurementSnapshot());
        Assert.Equal("-32500", duration.Tags["error.type"]);
        Assert.Equal(-32500, duration.Tags["zabbix.api.error.code"]);
        Assert.Equal("permission_or_not_found", duration.Tags["zabbix.api.error.kind"]);
        FakeLogRecord logged = Assert.Single(harness.Logs.GetSnapshot(), record => record.Id.Id == 4001);
        Assert.Contains("PermissionOrNotFound", logged.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_api_error_s_span_says_its_type_and_carries_none_of_the_peer_s_words()
    {
        // Zabbix builds the detail from the request ("Cannot inherit items with key ..."), and a span is exported unredacted.
        using var handler = new CapturingHandler("""{"jsonrpc":"2.0","error":{"code":-32602,"message":"Invalid params.","data":"Item with key \"vfs.file[/etc/s3cret]\" already exists."},"id":1}""");
        using Harness harness = TransportHarness.Build(handler);
        using var capture = new ApiTelemetryCapture(harness.Provider);

        await Assert.ThrowsAsync<ZabbixApiException>(async () =>
            await harness.Transport.SendAsync("item.create", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));

        Activity span = Assert.Single(capture.Spans);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(span.GetTagItem("error.type"), span.StatusDescription);
        Assert.Empty(span.Events);
        Assert.DoesNotContain(harness.Logs.GetSnapshot(), record => record.Message.Contains("s3cret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_result_the_model_cannot_read_is_a_failure_on_the_span_the_duration_and_the_log()
    {
        using var handler = new CapturingHandler("""{"jsonrpc":"2.0","result":{"not":"a list"},"id":1}""");
        using Harness harness = TransportHarness.Build(handler);
        using var capture = new ApiTelemetryCapture(harness.Provider);

        await Assert.ThrowsAsync<JsonException>(async () =>
            await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestJson.Default.ListHostRow, TestContext.Current.CancellationToken));

        Activity span = Assert.Single(capture.Spans);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("invalid_response", span.GetTagItem("error.type"));
        Assert.Equal("invalid_response", Assert.Single(capture.Durations.GetMeasurementSnapshot()).Tags["error.type"]);
        Assert.Contains(harness.Logs.GetSnapshot(), record => record.Id.Id == 4003 && record.Level == LogLevel.Debug);
        Assert.DoesNotContain(harness.Logs.GetSnapshot(), record => record.Id.Id == 4002);
    }

    [Fact]
    public async Task A_token_never_renders_in_a_log_a_span_or_an_exception()
    {
        const string Token = "tok-9f86d081884c7d659a2feaa0c55ad015";
        using var handler = new CapturingHandler("""{"jsonrpc":"2.0","error":{"code":-32602,"message":"Invalid params.","data":"Session terminated, re-login, please."},"id":1}""");
        using Harness harness = TransportHarness.Build(handler, token: Token);
        using var capture = new ApiTelemetryCapture(harness.Provider);

        ZabbixApiException failure = await Assert.ThrowsAsync<ZabbixApiException>(async () =>
            await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));

        Assert.Equal("Bearer " + Token, handler.LastRequest!.Headers.Authorization!.ToString());
        IEnumerable<string?> rendered = harness.Logs.GetSnapshot()
            .SelectMany(record => new[] { record.Message, record.Exception?.ToString() }
                .Concat(record.StructuredState?.Select(pair => pair.Value) ?? []))
            .Concat(capture.Spans.SelectMany(span => span.TagObjects.Select(tag => tag.Value?.ToString())))
            .Append(failure.ToString());
        Assert.DoesNotContain(rendered, text => text?.Contains(Token, StringComparison.Ordinal) == true);
    }

    [Fact]
    public async Task A_reply_whose_id_does_not_match_the_request_is_refused()
    {
        using var handler = new CapturingHandler("""{"jsonrpc":"2.0","result":[],"id":99}""");
        using Harness harness = TransportHarness.Build(handler);

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Request_ids_are_per_client_and_increase()
    {
        using var handler = new CapturingHandler("""{"jsonrpc":"2.0","result":[],"id":1}""", echoId: true);
        using Harness harness = TransportHarness.Build(handler);

        await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken);
        await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2], handler.Ids);
    }

    [Fact]
    public async Task Every_exchange_is_logged_out_and_in()
    {
        using var handler = new CapturingHandler("""{"jsonrpc":"2.0","result":[],"id":1}""");
        using Harness harness = TransportHarness.Build(handler);

        await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken);

        Assert.Contains(harness.Logs.GetSnapshot(), record => record.Id.Id == 4000);
        Assert.Contains(harness.Logs.GetSnapshot(), record => record.Id.Id == 4002);
    }

    [Fact]
    public async Task A_reply_that_is_not_json_is_an_http_failure_and_is_logged()
    {
        using var handler = new StatusHandler(HttpStatusCode.BadGateway, "<html>gateway down</html>", "text/html");
        using Harness harness = TransportHarness.Build(handler);

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await harness.Transport.SendAsync("host.create", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));

        Assert.Contains(harness.Logs.GetSnapshot(), record => record.Id.Id == 4003 && record.Level == LogLevel.Debug);
    }

    private sealed class StatusHandler(HttpStatusCode status, string body, string mediaType) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, mediaType) });
    }
}
