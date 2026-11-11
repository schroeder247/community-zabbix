// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Polly.Timeout;

namespace Community.Zabbix.Tests;

/// <summary>Resilience is the platform's standard handler; the one adapter is the retry gate keyed on the JSON-RPC method.</summary>
public sealed class ResilienceTests
{
    private sealed class FlakyHandler(int failuresBefore) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            LastUri = request.RequestUri;
            return Task.FromResult(Attempts <= failuresBefore
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"jsonrpc":"2.0","result":[],"id":1}""", Encoding.UTF8, "application/json") });
        }
    }

    private static void FastRetries(IServiceCollection services)
        => services.Configure<HttpStandardResilienceOptions>(ZabbixHttp.GetResilienceName(), options =>
        {
            options.Retry.Delay = TimeSpan.FromMilliseconds(1);
            options.Retry.MaxRetryAttempts = 3;
            options.CircuitBreaker.MinimumThroughput = 1000;
        });

    [Fact]
    public async Task A_read_that_hits_a_transient_failure_is_retried()
    {
        using var flaky = new FlakyHandler(failuresBefore: 2);
        using Harness harness = TransportHarness.Build(flaky, configure: FastRetries);

        await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken);

        Assert.Equal(3, flaky.Attempts);
    }

    [Fact]
    public async Task A_write_that_hits_a_transient_failure_is_never_repeated()
    {
        using var flaky = new FlakyHandler(failuresBefore: 1);
        using Harness harness = TransportHarness.Build(flaky, configure: FastRetries);

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await harness.Transport.SendAsync("host.create", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));

        Assert.Equal(1, flaky.Attempts);
    }

    [Theory]
    [InlineData("host.get", true)]
    [InlineData("apiinfo.version", true)]
    [InlineData("user.checkAuthentication", true)]
    [InlineData("host.create", false)]
    [InlineData("host.delete", false)]
    [InlineData("user.login", false)]
    public void Only_reads_are_idempotent(string method, bool idempotent) => Assert.Equal(idempotent, ZabbixApiMethods.IsIdempotent(ZabbixApiMethod.Parse(method)));

    /// <summary>Answers by script, per attempt: each entry is the reply body of one attempt, the id echoed.</summary>
    private sealed class ReplyScript(params string[] results) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using JsonDocument envelope = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            string id = envelope.RootElement.GetProperty("id").GetInt64().ToString(CultureInfo.InvariantCulture);
            string member = results[Math.Min(Attempts++, results.Length - 1)];
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"jsonrpc":"2.0",{{member}},"id":{{id}}}""", Encoding.UTF8, "application/json") };
        }
    }

    private const string FrontendDown = "\"error\":{\"code\":-32603,\"message\":\"Internal error.\",\"data\":\"SQLSTATE[08006] could not connect\"}";
    private const string Rows = "\"result\":[]";

    [Fact]
    public async Task A_read_answered_with_a_transient_api_error_is_retried()
    {
        using var script = new ReplyScript(FrontendDown, FrontendDown, Rows);
        using Harness harness = TransportHarness.Build(script, configure: FastRetries);

        await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken);

        Assert.Equal(3, script.Attempts);
    }

    [Fact]
    public async Task A_write_answered_with_a_transient_api_error_is_not_repeated()
    {
        using var script = new ReplyScript(FrontendDown, Rows);
        using Harness harness = TransportHarness.Build(script, configure: FastRetries);

        ZabbixApiException failure = await Assert.ThrowsAsync<ZabbixApiException>(async () =>
            await harness.Transport.SendAsync("host.create", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));

        Assert.True(failure.IsTransient);
        Assert.Equal(1, script.Attempts);
    }

    [Fact]
    public async Task A_read_answered_with_a_validation_error_is_not_retried()
    {
        using var script = new ReplyScript("\"error\":{\"code\":-32602,\"message\":\"Invalid params.\",\"data\":\"Invalid parameter.\"}", Rows);
        using Harness harness = TransportHarness.Build(script, configure: FastRetries);

        await Assert.ThrowsAsync<ZabbixApiException>(async () =>
            await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));

        Assert.Equal(1, script.Attempts);
    }

    // Measured on 7.0.31 and 8.0.0rc1: correlation.get with correlationids [null] reaches the SQL, which fails, so DBend rolls
    // back and CLocalApiClient answers ZBX_API_ERROR_DB (-32500), the same every time it is sent.
    private const string DatabaseError = "\"error\":{\"code\":-32500,\"message\":\"Application error.\",\"data\":\"Database error occurred.\"}";

    [Fact]
    public async Task A_read_answered_with_an_application_database_error_is_not_retried_and_surfaces_as_the_api_exception()
    {
        using var script = new ReplyScript(DatabaseError, Rows);
        using Harness harness = TransportHarness.Build(script, configure: FastRetries);

        ZabbixApiException failure = await Assert.ThrowsAsync<ZabbixApiException>(async () =>
            await harness.Transport.SendAsync("correlation.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));

        Assert.Equal(1, script.Attempts);
        Assert.Equal((-32500, ZabbixApiErrorKind.Database), (failure.Code, failure.Kind));
        Assert.False(failure.IsTransient);
        Assert.Equal("correlation.get: Application error. Database error occurred.", failure.Message);
    }

    [Fact]
    public async Task A_read_that_stays_transient_through_every_retry_surfaces_as_the_api_exception_not_the_pipeline_s()
    {
        using var script = new ReplyScript(FrontendDown);
        using Harness harness = TransportHarness.Build(script, configure: FastRetries);

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));

        Assert.Equal(4, script.Attempts);                                                          // one and three retries
        ZabbixApiException api = Assert.IsType<ZabbixApiException>(failure);
        Assert.True(api.IsTransient);
        Assert.StartsWith("host.get: Internal error. SQLSTATE", api.Message, StringComparison.Ordinal);
    }

    /// <summary>Headers at once, then a body that never finishes until the pipeline gives up on it.</summary>
    private sealed class StallingBody : HttpMessageHandler
    {
        public TaskCompletionSource Stalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StallingContent(Stalled) });

        private sealed class StallingContent(TaskCompletionSource stalled) : HttpContent
        {
            protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
            {
                await stream.WriteAsync("{\"jsonrpc\":\"2.0\",\"result\":["u8.ToArray(), cancellationToken);
                stalled.TrySetResult();
                await new TaskCompletionSource().Task.WaitAsync(cancellationToken);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = 0;
                return false;
            }
        }
    }

    private static Action<IServiceCollection> OnFakeTime(FakeTimeProvider time, Action<ZabbixClientOptions>? options = null) => services =>
    {
        services.AddSingleton<TimeProvider>(time);
        services.Configure<HttpStandardResilienceOptions>(ZabbixHttp.GetResilienceName(), resilience =>
        {
            resilience.Retry.MaxRetryAttempts = 1;
            resilience.CircuitBreaker.MinimumThroughput = 1000;
        });
        if (options is not null) services.Configure(options);
    };

    [Fact]
    public async Task A_stalled_body_fails_within_the_attempt_timeout_as_a_timeout_on_every_signal()
    {
        var time = new FakeTimeProvider();
        using var stall = new StallingBody();
        using Harness harness = TransportHarness.Build(stall, configure: OnFakeTime(time));
        using var capture = new ApiTelemetryCapture(harness.Provider);

        Task call = harness.Transport.SendAsync("host.create", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken);
        await stall.Stalled.Task;
        time.Advance(TimeSpan.FromSeconds(11));                         // past the standard 10 s attempt timeout

        TimeoutException late = await Assert.ThrowsAsync<TimeoutException>(() => call);
        Assert.IsType<TimeoutRejectedException>(late.InnerException);
        Assert.Equal("timeout", Assert.Single(capture.Spans).GetTagItem("error.type"));
        Assert.Equal("timeout", Assert.Single(capture.Durations.GetMeasurementSnapshot()).Tags["error.type"]);
        Assert.Contains(harness.Logs.GetSnapshot(), record => record.Id.Id == 4003 && record.Level == LogLevel.Debug);
    }

    /// <summary>A reply body of <paramref name="size"/> bytes, streamed without a declared length, counting the requests.</summary>
    private sealed class HugeBody(long size, bool declareLength) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => _requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new Filler(size, declareLength) });
        }

        private sealed class Filler(long size, bool declareLength) : HttpContent
        {
            protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

            protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
            {
                byte[] chunk = new byte[64 * 1024];
                chunk.AsSpan().Fill((byte)' ');
                for (long written = 0; written < size; written += chunk.Length)
                    await stream.WriteAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, size - written)), cancellationToken);
            }

            protected override bool TryComputeLength(out long length)
            {
                length = size;
                return declareLength;
            }
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_reply_larger_than_the_bound_fails_once_as_invalid_data_and_is_not_retried(bool declareLength)
    {
        // A bound is a feature. A reply the process would have to hold whole (the body is buffered inside the deadline)
        // is refused at the bound, whether the server declares its length or streams it; it is not a transient failure, so
        // the read is not sent again.
        using var huge = new HugeBody(3 * 1024 * 1024, declareLength);
        using Harness harness = TransportHarness.Build(huge, configure: services => services.Configure<ZabbixClientOptions>(options => options.MaxResponseContentBytes = 1024 * 1024));
        using var capture = new ApiTelemetryCapture(harness.Provider);

        InvalidDataException refused = await Assert.ThrowsAsync<InvalidDataException>(() => harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));

        Assert.Contains("MaxResponseContentBytes", refused.Message, StringComparison.Ordinal);
        Assert.Equal(1, huge.Requests);                                                      // a read, yet not retried: it would be as large again
        Assert.Equal("invalid_response", Assert.Single(capture.Spans).GetTagItem("error.type"));
    }

    [Fact]
    public void The_reply_bound_defaults_to_the_wire_s_one_gibibyte_and_must_be_positive()
    {
        Assert.Equal(1L << 30, new ZabbixClientOptions().MaxResponseContentBytes);
        var services = new ServiceCollection();
        services.AddZabbixClient().Configure(options => { options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"); options.MaxResponseContentBytes = 0; });
        using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptionsMonitor<ZabbixClientOptions>>().Get(Options.DefaultName));
    }

    [Fact]
    public async Task A_method_named_in_the_timeouts_waits_for_its_own_deadline()
    {
        var time = new FakeTimeProvider();
        using var stall = new StallingBody();
        using Harness harness = TransportHarness.Build(stall, configure: OnFakeTime(time, options => options.Timeouts["configuration.import"] = TimeSpan.FromMinutes(10)));

        Task call = harness.Transport.SendAsync("configuration.import", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken);
        await stall.Stalled.Task;
        time.Advance(TimeSpan.FromMinutes(5));                          // far past the standard 10 s / 30 s

        Assert.False(call.IsCompleted);
        time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        await Assert.ThrowsAsync<TimeoutException>(() => call);
    }

    [Fact]
    public async Task The_resilience_words_reload_and_the_endpoint_does_not()
    {
        using var flaky = new FlakyHandler(failuresBefore: 1000);
        var source = new Dictionary<string, string?>
        {
            ["Endpoint"] = "https://zbx.example/api_jsonrpc.php",
            ["Resilience:Retry:MaxRetryAttempts"] = "1",
            ["Resilience:Retry:Delay"] = "00:00:00.001",
            ["Resilience:CircuitBreaker:MinimumThroughput"] = "1000",
        };
        IConfigurationRoot configuration = new ConfigurationBuilder().AddInMemoryCollection(source).Build();
        var services = new ServiceCollection();
        services.AddZabbixClient().Bind(configuration);
        services.AddHttpClient(ZabbixHttp.GetClientName()).ConfigurePrimaryHttpMessageHandler(() => flaky);
        using ServiceProvider provider = services.BuildValidated();
        IZabbixTransport transport = provider.GetRequiredService<IZabbixTransport>();

        await Assert.ThrowsAsync<HttpRequestException>(async () => await transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));
        Assert.Equal(2, flaky.Attempts);

        configuration["Resilience:Retry:MaxRetryAttempts"] = "3";
        configuration["Endpoint"] = "https://elsewhere.example/api_jsonrpc.php";
        configuration.Reload();
        await Assert.ThrowsAsync<HttpRequestException>(async () => await transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));

        Assert.Equal(2 + 4, flaky.Attempts);
        Assert.Equal(new Uri("https://zbx.example/api_jsonrpc.php"), flaky.LastUri);
    }
}
