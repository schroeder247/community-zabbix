// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace Community.Zabbix.Tests;

/// <summary>
/// The JSON-RPC transport against a hostile or broken HTTP peer — bodies that are not the envelope,
/// envelopes that are wrong in every way JSON allows, a token and a redirect, a gzip body nobody asked for, a declared
/// length far past what arrives, trickling and truncated bodies, and status storms. The designed failures are
/// <see cref="ZabbixApiException"/> (the API said no), <see cref="HttpRequestException"/> (HTTP said no),
/// <see cref="JsonException"/> or <see cref="InvalidDataException"/> (the reply is not the API's, <c>error.type</c>
/// <c>invalid_response</c>), and <see cref="TimeoutException"/> for the pipeline's deadline (its <see cref="TimeoutRejectedException"/> inside); anything else is a defect.
/// </summary>
[Collection("Adversarial wire stress")]
public sealed class AdversarialJsonRpcTests
{
    private const string Token = "tok-5e884898da28047151d0e56f8dc62927";

    /// <summary>Answers every request with a reply built from its id (or a fixed body where <c>ID</c> becomes the id).</summary>
    private sealed class Echoing(Func<long, HttpResponseMessage> answer) : HttpMessageHandler
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public Echoing(string body, string mediaType = "application/json", HttpStatusCode status = HttpStatusCode.OK)
            : this(id => new HttpResponseMessage(status) { Content = new StringContent(body.Replace("ID", id.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal), Encoding.UTF8, mediaType) })
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            using JsonDocument envelope = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            return answer(envelope.RootElement.GetProperty("id").GetInt64());
        }
    }

    private static Task<JsonElement> CallAsync(Harness harness, string method = "host.get")
        => harness.Transport.SendAsync(method, new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken);

    private static Action<IServiceCollection> Quick(FakeTimeProvider? time = null, int retries = 3) => services =>
    {
        if (time is not null) services.AddSingleton<TimeProvider>(time);
        services.Configure<HttpStandardResilienceOptions>(ZabbixHttp.GetResilienceName(), options =>
        {
            options.Retry.Delay = TimeSpan.FromMilliseconds(1);
            options.Retry.MaxRetryAttempts = retries;
            options.Retry.UseJitter = false;
            options.CircuitBreaker.MinimumThroughput = 1000;
        });
    };

    // ---- the reply's body -----------------------------------------------------------------------------------------------

    public static TheoryData<string, string> NotTheEnvelope => new()
    {
        { "", "application/json" },
        { "not json at all", "application/json" },
        { "<html><body>Zabbix is being updated</body></html>", "text/html" },
        { "{\"jsonrpc\":\"2.0\",\"result\":[{\"hostid\":\"1\"", "application/json" },
        { "{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":ID}trailing", "application/json" },
        { "{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":ID}{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":ID}", "application/json" },
        { "{\"jsonrpc\":\"2.0\",\"id\":ID}", "application/json" },
        { "{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":1.5}", "application/json" },
        { "{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":99999999999999999999999}", "application/json" },
        { "{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":-1}", "application/json" },
    };

    [Theory]
    [MemberData(nameof(NotTheEnvelope))]
    public async Task A_reply_that_is_not_the_envelope_is_invalid_response_on_the_span_never_another_exception(string body, string mediaType)
    {
        using var handler = new Echoing(body, mediaType);
        using Harness harness = TransportHarness.Build(handler, configure: Quick());
        using var capture = new ApiTelemetryCapture(harness.Provider);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => CallAsync(harness));

        Assert.True(failure is JsonException or InvalidDataException, failure.GetType().FullName + ": " + failure.Message);
        Assert.Equal("invalid_response", Assert.Single(capture.Spans).GetTagItem("error.type"));
    }

    [Theory]
    [InlineData("http://zbx.example:28080/index.php", "http://zbx.example:28080/api_jsonrpc.php")]
    [InlineData("http://zbx.example/zabbix/index.php?x=1", "http://zbx.example/zabbix/api_jsonrpc.php")]
    [InlineData("http://zbx.example/zabbix/zabbix.php", "http://zbx.example/zabbix/api_jsonrpc.php")]
    public async Task A_frontend_page_answered_for_the_api_names_the_endpoint_and_the_api_s_address(string endpoint, string suggested)
    {
        // The frontend's login page, as an endpoint naming another of its scripts gets it: 200 and HTML. (A folder is the
        // frontend's address, which the SDK turns into its api_jsonrpc.php before anything is sent.)
        using var handler = new Echoing("\r\n<!DOCTYPE html><html><head><title>Zabbix</title></head></html>", "text/html");
        using Harness harness = TransportHarness.Build(handler, token: null, configure: services =>
            services.Configure<ZabbixOptions>(options => options.Endpoint = new Uri(endpoint)));
        using var capture = new ApiTelemetryCapture(harness.Provider);

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => CallAsync(harness));

        Assert.Contains("is not JSON-RPC (text/html)", failure.Message, StringComparison.Ordinal);
        Assert.Contains(new Uri(endpoint).GetLeftPart(UriPartial.Path), failure.Message, StringComparison.Ordinal);
        Assert.Contains("such as " + suggested + ".", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("x=1", failure.Message, StringComparison.Ordinal);
        Assert.Equal("invalid_response", Assert.Single(capture.Spans).GetTagItem("error.type"));
        Assert.Equal(1, handler.Attempts);                                                 // not the transient kind: sent once
    }

    [Fact]
    public async Task A_page_answered_at_api_jsonrpc_php_says_something_else_answered()
    {
        using var handler = new Echoing("<html><body>Zabbix is being updated</body></html>", "text/html");
        using Harness harness = TransportHarness.Build(handler);

        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() => CallAsync(harness));

        Assert.Equal("The reply to host.get #1 from https://zbx.example/api_jsonrpc.php is not JSON-RPC (text/html): the endpoint names api_jsonrpc.php, so something other than the frontend's API answered it.", failure.Message);
    }

    [Fact]
    public async Task Bytes_that_are_not_utf8_in_a_modelled_string_are_invalid_response()
    {
        using var handler = new Echoing(id => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([.. "{\"jsonrpc\":\"2.0\",\"result\":[{\"hostid\":\""u8, 0xC3, 0x28, 0xFF, .. Encoding.ASCII.GetBytes("\"}],\"id\":" + id.ToString(CultureInfo.InvariantCulture) + "}")]) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        });
        using Harness harness = TransportHarness.Build(handler);

        await Assert.ThrowsAnyAsync<JsonException>(async () => await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestJson.Default.ListHostRow, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("\\ud800")]
    [InlineData("\\udc00x")]
    [InlineData("\\ud800\\ud800")]
    public async Task A_lone_surrogate_in_a_modelled_string_is_invalid_response_not_an_invalid_operation(string escaped)
    {
        using var handler = new Echoing("{\"jsonrpc\":\"2.0\",\"result\":[{\"hostid\":\"" + escaped + "\"}],\"id\":ID}");
        using Harness harness = TransportHarness.Build(handler);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(async () => await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestJson.Default.ListHostRow, TestContext.Current.CancellationToken));

        Assert.True(failure is JsonException, failure.GetType().FullName + ": " + failure.Message);
    }

    // A BOM is read through: the frontend never writes one, but JsonDocument.ParseAsync skips it, so a proxy that adds one costs nothing.
    [Fact]
    public async Task A_reply_behind_a_utf8_byte_order_mark_is_read()
    {
        using var handler = new Echoing(id => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":" + id.ToString(CultureInfo.InvariantCulture) + "}")]) { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } },
        });
        using Harness harness = TransportHarness.Build(handler);

        JsonElement result = await CallAsync(harness);

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
    }

    // The transport does not check `jsonrpc` (a documented choice); the frontend always writes "2.0" (CJsonRpc.php), and
    // the id and the result/error members carry everything the reply means.
    [Theory]
    [InlineData("{\"result\":[],\"id\":ID}")]
    [InlineData("{\"jsonrpc\":\"1.0\",\"result\":[],\"id\":ID}")]
    public async Task A_reply_without_jsonrpc_2_0_is_still_read_by_its_id_and_result(string body)
    {
        using var handler = new Echoing(body);
        using Harness harness = TransportHarness.Build(handler);

        JsonElement result = await CallAsync(harness);

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
    }

    // A reply whose members are the wrong JSON kind (a root that is not an object, an `error` that is not an object, an
    // error `message` that is not a string, an `id` that is a string or null) is `invalid_response`. JsonElement's
    // TryGetProperty/TryGetInt64/GetString throw InvalidOperationException on an element of the wrong kind ("The requested
    // operation requires an element of type 'Object'/'Number'/'String', but the target element has type '…'"), a
    // caller-misuse type that must not be what the caller sees for a bad reply.
    [Theory]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("\"x\"")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"error\":\"boom\",\"id\":ID}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"error\":null,\"id\":ID}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"error\":[],\"id\":ID}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":5},\"id\":ID}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":\"ID\"}")]
    [InlineData("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":null}")]
    public async Task A_reply_whose_members_are_the_wrong_json_kind_is_invalid_response_not_invalid_operation(string body)
    {
        using var handler = new Echoing(body);
        using Harness harness = TransportHarness.Build(handler, configure: Quick());
        using var capture = new ApiTelemetryCapture(harness.Provider);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => CallAsync(harness));

        Assert.True(failure is JsonException or InvalidDataException, failure.GetType().FullName + ": " + failure.Message);
        Assert.Equal("invalid_response", Assert.Single(capture.Spans).GetTagItem("error.type"));
    }

    // An error with `id: null` is what the frontend writes when it could not read the request's id (CJsonRpc.php,
    // -32700/-32600), so an error is taken whatever its id — that one case is correct.
    [Fact]
    public async Task An_error_with_a_null_id_is_the_api_s_refusal_of_this_request()
    {
        using var handler = new Echoing("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32600,\"message\":\"Invalid request.\",\"data\":\"Invalid parameter.\"},\"id\":null}");
        using Harness harness = TransportHarness.Build(handler);

        ZabbixApiException failure = await Assert.ThrowsAsync<ZabbixApiException>(() => CallAsync(harness));

        Assert.Equal(-32600, failure.Code);
    }

    // An error that answers another request's id (99 here, the request was #1) is not this call's reply: thrown as this
    // call's ZabbixApiException it would, for a session error, invalidate this call's session. JSON-RPC 2.0 §5: the id
    // "MUST be the same as the value of the id member in the Request Object"; only a null id (unreadable request) is
    // exempt, as the test above holds.
    [Fact]
    public async Task An_error_that_answers_another_request_s_id_is_refused_as_not_this_call_s_reply()
    {
        using var handler = new Echoing("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":-32602,\"message\":\"Invalid params.\",\"data\":\"Session terminated, re-login, please.\"},\"id\":99}");
        using Harness harness = TransportHarness.Build(handler);

        await Assert.ThrowsAsync<InvalidDataException>(() => CallAsync(harness));
    }

    [Fact]
    public async Task A_reply_with_both_result_and_error_is_the_error()
    {
        using var handler = new Echoing("{\"jsonrpc\":\"2.0\",\"result\":[],\"error\":{\"code\":-32500,\"message\":\"Application error.\",\"data\":\"No permissions.\"},\"id\":ID}");
        using Harness harness = TransportHarness.Build(handler);

        ZabbixApiException failure = await Assert.ThrowsAsync<ZabbixApiException>(() => CallAsync(harness));

        Assert.Equal(-32500, failure.Code);
    }

    [Fact]
    public async Task An_error_code_outside_int32_is_code_zero_not_an_overflow()
    {
        using var handler = new Echoing("{\"jsonrpc\":\"2.0\",\"error\":{\"code\":99999999999,\"message\":\"Odd.\",\"data\":\"\\u0000\\ud83d\\ude00\"},\"id\":ID}");
        using Harness harness = TransportHarness.Build(handler);

        ZabbixApiException failure = await Assert.ThrowsAsync<ZabbixApiException>(() => CallAsync(harness));

        Assert.Equal(0, failure.Code);
        Assert.Contains("\u0000\U0001F600", failure.Detail, StringComparison.Ordinal);
    }

    // ---- statuses and storms ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task A_read_answered_with_a_client_error_page_is_an_http_failure_and_is_not_retried(HttpStatusCode status)
    {
        using var handler = new Echoing("<html>no</html>", "text/html", status);
        using Harness harness = TransportHarness.Build(handler, configure: Quick());

        HttpRequestException failure = await Assert.ThrowsAsync<HttpRequestException>(() => CallAsync(harness));

        Assert.Equal((status, 1), (failure.StatusCode, handler.Attempts));
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.GatewayTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task A_read_in_a_storm_of_transient_statuses_is_sent_one_plus_the_configured_retries_and_no_more(HttpStatusCode status)
    {
        using var handler = new Echoing("busy", "text/plain", status);
        using Harness harness = TransportHarness.Build(handler, configure: Quick(retries: 5));

        await Assert.ThrowsAsync<HttpRequestException>(() => CallAsync(harness));

        Assert.Equal(6, handler.Attempts);
    }

    [Theory]
    [InlineData("host.create")]
    [InlineData("host.update")]
    [InlineData("host.delete")]
    [InlineData("user.login")]
    [InlineData("HOST.CREATE")]
    public async Task A_write_in_a_storm_of_transient_statuses_is_sent_once(string method)
    {
        using var handler = new Echoing("busy", "text/plain", HttpStatusCode.ServiceUnavailable);
        using Harness harness = TransportHarness.Build(handler, configure: Quick(retries: 5));

        await Assert.ThrowsAsync<HttpRequestException>(() => CallAsync(harness, method));

        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    [SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed", Justification = "The call is awaited, under a failsafe bound, before the harness is disposed.")]
    public async Task A_429_with_retry_after_waits_the_server_s_delay_on_the_injected_clock_before_the_retry()
    {
        var time = new WatchedClock();
        int calls = 0;
        using var handler = new Echoing(id => Interlocked.Increment(ref calls) == 1
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(7)) }, Content = new StringContent("slow down") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":" + id.ToString(CultureInfo.InvariantCulture) + "}", Encoding.UTF8, "application/json") });
        using Harness harness = TransportHarness.Build(handler, configure: services =>
        {
            services.AddSingleton<TimeProvider>(time);
            services.Configure<HttpStandardResilienceOptions>(ZabbixHttp.GetResilienceName(), options => options.CircuitBreaker.MinimumThroughput = 1000);
        });

        Task<JsonElement> call = CallAsync(harness);
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);    // the retry's delay is the server's
        time.Advance(TimeSpan.FromSeconds(6));
        int before = Volatile.Read(ref calls);
        time.Advance(TimeSpan.FromSeconds(1));
        JsonElement result = await call.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken);

        Assert.Equal((1, 2), (before, Volatile.Read(ref calls)));
        Assert.Equal(JsonValueKind.Array, result.ValueKind);
    }

    [Fact]
    public async Task A_storm_that_opens_the_circuit_stops_reaching_the_server()
    {
        var time = new FakeTimeProvider();
        using var handler = new Echoing("busy", "text/plain", HttpStatusCode.ServiceUnavailable);
        using Harness harness = TransportHarness.Build(handler, configure: services =>
        {
            services.AddSingleton<TimeProvider>(time);
            services.Configure<HttpStandardResilienceOptions>(ZabbixHttp.GetResilienceName(), options =>
            {
                options.Retry.MaxRetryAttempts = 1;
                options.Retry.Delay = TimeSpan.Zero;
                options.CircuitBreaker.MinimumThroughput = 4;
                options.CircuitBreaker.FailureRatio = 0.5;
            });
        });

        var failures = new List<Exception>();
        for (int i = 0; i < 6; i++) failures.Add(await Assert.ThrowsAnyAsync<Exception>(() => CallAsync(harness, "host.create")));
        int reached = handler.Attempts;

        Assert.True(reached < 6, $"the server saw {reached} of 6 calls");
        // Polly's own rejection is the inner exception of the type the transport documents for a failed exchange.
        Assert.IsType<BrokenCircuitException>(Assert.IsType<HttpRequestException>(failures[^1]).InnerException);
        Assert.Contains(harness.Logs.GetSnapshot(), record => record.Id.Id == 4003);
    }

    // ---- bodies over time and size -------------------------------------------------------------------------------------------

    /// <summary>A body that writes one byte each time the test says so, forever.</summary>
    private sealed class Trickle : HttpMessageHandler
    {
        public Channel<bool> Tick { get; } = Channel.CreateUnbounded<bool>();

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new TrickleContent(this) });

        private sealed class TrickleContent(Trickle owner) : HttpContent
        {
            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            {
                await stream.WriteAsync("{\"jsonrpc\":\"2.0\",\"result\":["u8.ToArray(), cancellationToken);
                owner.Started.TrySetResult();
                await foreach (bool _ in owner.Tick.Reader.ReadAllAsync(cancellationToken))
                    await stream.WriteAsync(" "u8.ToArray(), cancellationToken);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }
    }

    [Fact]
    [SuppressMessage("Reliability", "CA2025:Ensure tasks using IDisposable instances complete before the instances are disposed", Justification = "The call is awaited, under a failsafe bound, before the harness is disposed.")]
    public async Task A_body_that_keeps_trickling_is_cut_by_the_attempt_timeout_on_the_injected_clock()
    {
        var time = new FakeTimeProvider();
        using var trickle = new Trickle();
        using Harness harness = TransportHarness.Build(trickle, configure: services =>
        {
            services.AddSingleton<TimeProvider>(time);
            services.Configure<HttpStandardResilienceOptions>(ZabbixHttp.GetResilienceName(), options => { options.Retry.MaxRetryAttempts = 1; options.CircuitBreaker.MinimumThroughput = 1000; });
        });

        Task<JsonElement> call = CallAsync(harness, "host.create");
        await trickle.Started.Task.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken);
        for (int second = 0; second < 11; second++)
        {
            trickle.Tick.Writer.TryWrite(true);                                                   // progress every second: still cut
            time.Advance(TimeSpan.FromSeconds(1));
        }

        await Assert.ThrowsAsync<TimeoutException>(() => call.WaitAsync(TimeSpan.FromSeconds(10), TimeProvider.System, TestContext.Current.CancellationToken));
    }

    /// <summary>A valid envelope whose result is <c>count</c> rows, produced as it is read, never held whole by the test.</summary>
    private sealed class Rows(int count) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using JsonDocument envelope = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            long id = envelope.RootElement.GetProperty("id").GetInt64();
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new RowsContent(count, id) };
        }

        private sealed class RowsContent(int count, long id) : HttpContent
        {
            public const int RowBytes = 64;

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
            {
                await stream.WriteAsync("{\"jsonrpc\":\"2.0\",\"result\":["u8.ToArray(), cancellationToken);
                byte[] row = Encoding.ASCII.GetBytes("{\"hostid\":\"10084\",\"host\":\"a-host-with-a-name-of-some-length\"}");
                byte[] block = [.. Enumerable.Range(0, 1024).SelectMany(i => i == 0 ? row : [(byte)',', .. row])];
                for (int written = 0; written < count; written += 1024)
                {
                    if (written > 0) await stream.WriteAsync(","u8.ToArray(), cancellationToken);
                    await stream.WriteAsync(block, cancellationToken);
                }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("],\"id\":" + id.ToString(CultureInfo.InvariantCulture) + "}"), cancellationToken);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }
    }

    // A reply is not held three times over. Buffering the whole body (LoadIntoBufferAsync, a MemoryStream that doubles,
    // bounded only by HttpContent's 2 GiB), parsing it whole into a JsonDocument (another growing copy), and then
    // deserializing the result element into the rows costs 83.7 to 133.9 MB for 16.25 MiB of rows (262 144 × 65 bytes,
    // 4.9× to 7.9×): gigabytes for a large history.get of hundreds of MB. Deserializing the envelope once from the
    // buffered bytes keeps it under four times the reply's size.
    [Fact]
    public async Task A_sixteen_mebibyte_reply_is_read_whole_for_less_than_four_times_its_size()
    {
        const int Count = 256 * 1024;                                                               // ~16 MiB of rows
        using var rows = new Rows(Count);
        using Harness harness = TransportHarness.Build(rows);

        long before = GC.GetTotalAllocatedBytes(precise: true);
        List<HostRow> read = await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestJson.Default.ListHostRow, TestContext.Current.CancellationToken);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        long body = (long)Count * 65;
        Assert.Equal(Count, read.Count);
        Assert.True(allocated < 4 * body, $"allocated {allocated} bytes for a {body}-byte reply ({(double)allocated / body:F1}×)");
    }

    [Fact]
    public async Task A_sixteen_mebibyte_reply_is_read_whole_and_its_cost_does_not_grow_past_what_was_measured()
    {
        const int Count = 256 * 1024;
        using var rows = new Rows(Count);
        using Harness harness = TransportHarness.Build(rows);

        long before = GC.GetTotalAllocatedBytes(precise: true);
        JsonElement read = await CallAsync(harness);
        long allocated = GC.GetTotalAllocatedBytes(precise: true) - before;

        long body = (long)Count * 65;
        Assert.Equal(Count, read.GetArrayLength());
        Assert.True(allocated < 10 * body, $"allocated {allocated} bytes for a {body}-byte reply ({(double)allocated / body:F1}×)");
    }

    // ---- a real socket: redirects, gzip, a declared length, a close mid-body ---------------------------------------------

    /// <summary>A minimal HTTP/1.1 peer on a raw socket: reads one request per connection and writes what it is told, verbatim.</summary>
    private sealed class RawHttp : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public RawHttp(Func<string, NetworkStream, CancellationToken, Task> respond)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _serving = Task.Run(async () =>
            {
                while (!_stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
                    catch (OperationCanceledException) { return; }
                    using (client)
                    {
                        try
                        {
                            NetworkStream stream = client.GetStream();
                            string head = await ReadRequestAsync(stream, _stop.Token);
                            await Requests.Writer.WriteAsync(head, _stop.Token);
                            await respond(head, stream, _stop.Token);
                        }
                        catch (IOException) { }
                        catch (OperationCanceledException) { }
                    }
                }
            });
        }

        public Channel<string> Requests { get; } = Channel.CreateUnbounded<string>();

        public Uri Endpoint => new($"http://{((IPEndPoint)_listener.LocalEndpoint).Address}:{((IPEndPoint)_listener.LocalEndpoint).Port}/api_jsonrpc.php");

        private static async Task<string> ReadRequestAsync(NetworkStream stream, CancellationToken cancellationToken)
        {
            var head = new List<byte>();
            var one = new byte[1];
            while (!(head.Count >= 4 && head[^4] == '\r' && head[^3] == '\n' && head[^2] == '\r' && head[^1] == '\n'))
            {
                if (await stream.ReadAsync(one, cancellationToken) == 0) throw new IOException("closed in the head");
                head.Add(one[0]);
            }
            string text = Encoding.ASCII.GetString([.. head]);
            string? length = text.Split("\r\n").FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            int body = length is null ? 0 : int.Parse(length["Content-Length:".Length..].Trim(), CultureInfo.InvariantCulture);
            var buffer = new byte[body];
            await stream.ReadExactlyAsync(buffer, cancellationToken);
            return text + Encoding.UTF8.GetString(buffer);
        }

        public static long IdOf(string request)
        {
            using JsonDocument envelope = JsonDocument.Parse(request[(request.IndexOf("\r\n\r\n", StringComparison.Ordinal) + 4)..]);
            return envelope.RootElement.GetProperty("id").GetInt64();
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _serving;     // before the listener goes: an accept already on its way must meet the token, not a disposed socket
            _listener.Stop();
            _listener.Dispose();
            _stop.Dispose();
        }
    }

    private static (ServiceProvider Provider, IZabbixTransport Transport, FakeLogCollector Logs) Real(Uri endpoint)
    {
        var services = new ServiceCollection();
        services.AddZabbix(options =>
        {
            options.Endpoint = endpoint;
            options.ApiToken = Token;
        });
        services.AddLogging(logging => logging.AddFakeLogging().SetMinimumLevel(LogLevel.Trace));
        services.Configure<HttpStandardResilienceOptions>(ZabbixHttp.GetResilienceName(), options =>
        {
            options.Retry.MaxRetryAttempts = 1;
            options.Retry.Delay = TimeSpan.FromMilliseconds(1);
            options.CircuitBreaker.MinimumThroughput = 1000;
        });
        ServiceProvider provider = services.BuildValidated();
        return (provider, provider.GetRequiredService<IZabbixTransport>(), provider.GetFakeLogCollector());
    }

    private static Task WriteAsync(NetworkStream stream, string head, byte[] body, CancellationToken cancellationToken)
        => stream.WriteAsync((byte[])[.. Encoding.ASCII.GetBytes(head), .. body], cancellationToken).AsTask();

    [Fact]
    public async Task A_redirect_to_another_host_never_carries_the_token_there()
    {
        await using var elsewhere = new RawHttp(async (request, stream, token) =>
        {
            byte[] body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":" + RawHttp.IdOf(request).ToString(CultureInfo.InvariantCulture) + "}");
            await WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n", body, token);
        });
        // Another host by name, which is how a client tells hosts apart: the origin is 127.0.0.1, and macOS answers on no other loopback address.
        Uri foreign = new UriBuilder(elsewhere.Endpoint) { Host = "localhost" }.Uri;
        await using var origin = new RawHttp((_, stream, token) => WriteAsync(stream, $"HTTP/1.1 307 Temporary Redirect\r\nLocation: {foreign}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", [], token));
        (ServiceProvider provider, IZabbixTransport transport, _) = Real(origin.Endpoint);
        using (provider)
        {
            await Record.ExceptionAsync(async () => await transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));
        }

        string first = await origin.Requests.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.Contains("Authorization: Bearer " + Token, first, StringComparison.Ordinal);
        if (elsewhere.Requests.Reader.TryRead(out string? followed))
            Assert.DoesNotContain(Token, followed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(301, "Moved Permanently")]
    [InlineData(302, "Found")]
    [InlineData(307, "Temporary Redirect")]
    [InlineData(308, "Permanent Redirect")]
    public async Task A_redirect_is_not_followed_even_to_the_same_host_and_surfaces_as_its_status(int status, string reason)
    {
        await using var target = new RawHttp(async (request, stream, token) =>
        {
            byte[] body = Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"result\":[],\"id\":1}");
            await WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n", body, token);
        });
        await using var origin = new RawHttp((_, stream, token) => WriteAsync(stream, $"HTTP/1.1 {status} {reason}\r\nLocation: {target.Endpoint}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", [], token));
        (ServiceProvider provider, IZabbixTransport transport, _) = Real(origin.Endpoint);
        Exception? failure;
        using (provider)
        {
            failure = await Record.ExceptionAsync(async () => await transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));
        }

        HttpRequestException refused = Assert.IsType<HttpRequestException>(failure);
        Assert.Equal((HttpStatusCode)status, refused.StatusCode);
        Assert.False(target.Requests.Reader.TryRead(out _));                                           // never sent where it pointed
    }

    [Fact]
    public async Task A_gzip_body_nobody_asked_for_is_not_inflated_and_costs_its_compressed_size()
    {
        byte[] bomb;
        using (var compressed = new MemoryStream())
        {
            using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                gzip.Write("{\"jsonrpc\":\"2.0\",\"result\":[\""u8);
                var zeros = new byte[1024 * 1024];
                Array.Fill(zeros, (byte)'0');
                for (int i = 0; i < 64; i++) gzip.Write(zeros);                                        // 64 MiB inflated
                gzip.Write("\"],\"id\":1}"u8);
            }
            bomb = compressed.ToArray();
        }
        await using var server = new RawHttp((_, stream, token) => WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Encoding: gzip\r\nContent-Length: {bomb.Length}\r\nConnection: close\r\n\r\n", bomb, token));
        (ServiceProvider provider, IZabbixTransport transport, _) = Real(server.Endpoint);
        Exception? failure;
        long allocated;
        using (provider)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            failure = await Record.ExceptionAsync(async () => await transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));
            allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        }

        string request = await server.Requests.Reader.ReadAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain("Accept-Encoding", request, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<InvalidDataException>(failure);                                       // the gzip bytes do not open a JSON object
        Assert.True(allocated < 16L * 1024 * 1024, $"allocated {allocated} bytes for a {bomb.Length}-byte gzip body");
    }

    [Fact]
    public async Task A_content_length_far_past_what_arrives_costs_what_arrived_not_what_was_declared()
    {
        const long Declared = 64L * 1024 * 1024;
        await using var server = new RawHttp((_, stream, token) => WriteAsync(stream, $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {Declared}\r\nConnection: close\r\n\r\n", "{\"jsonrpc\":\"2.0\""u8.ToArray(), token));
        (ServiceProvider provider, IZabbixTransport transport, _) = Real(server.Endpoint);
        Exception? failure;
        long allocated;
        using (provider)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            failure = await Record.ExceptionAsync(async () => await transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));
            allocated = GC.GetTotalAllocatedBytes(precise: true) - before;
        }

        Assert.IsType<HttpRequestException>(failure);                                                  // the response ended prematurely
        Assert.True(allocated < Declared / 4, $"allocated {allocated} bytes for a body that declared {Declared} and sent 16");
    }

    [Fact]
    public async Task A_server_that_closes_mid_body_is_an_http_failure_and_a_read_is_retried_a_bounded_number_of_times()
    {
        await using var server = new RawHttp((_, stream, token) => WriteAsync(stream, "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 100\r\nConnection: close\r\n\r\n", "{\"jsonrpc\":\"2.0\",\"res"u8.ToArray(), token));
        (ServiceProvider provider, IZabbixTransport transport, FakeLogCollector logs) = Real(server.Endpoint);
        Exception? failure;
        using (provider)
        {
            failure = await Record.ExceptionAsync(async () => await transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));
        }
        server.Requests.Writer.TryComplete();
        int attempts = 0;
        await foreach (string _ in server.Requests.Reader.ReadAllAsync(TestContext.Current.CancellationToken)) attempts++;

        Assert.IsType<HttpRequestException>(failure);
        Assert.Equal(2, attempts);                                                                     // one and the one configured retry
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 4003);
    }
}
