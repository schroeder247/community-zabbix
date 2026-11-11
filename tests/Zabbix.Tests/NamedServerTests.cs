// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>Several servers in one container, each with its own options, client and credential, reached by name through keyed DI and the factory alike.</summary>
public sealed class NamedServerTests
{
    [Fact]
    public void A_named_server_resolves_through_keyed_di_and_the_factory_alike()
    {
        var services = new ServiceCollection();
        services.AddZabbix("prod", options => options.Endpoint = new Uri("https://prod.example/api_jsonrpc.php"));
        services.AddZabbix("staging", options => options.Endpoint = new Uri("https://staging.example/api_jsonrpc.php"));
        using ServiceProvider provider = services.BuildValidated();

        ZabbixClient prod = provider.GetRequiredService<IZabbixClientFactory>().Create("prod");
        ZabbixClient staging = provider.GetRequiredKeyedService<ZabbixClient>("staging");

        Assert.Same(prod, provider.GetRequiredKeyedService<ZabbixClient>("prod"));
        Assert.Equal(new Uri("https://prod.example/api_jsonrpc.php"), prod.Endpoint);
        Assert.Equal(new Uri("https://staging.example/api_jsonrpc.php"), staging.Endpoint);
        Assert.NotSame(prod.Transport, staging.Transport);
        Assert.Equal("Zabbix:prod", ZabbixHttp.GetClientName("prod"));
    }

    [Fact]
    public void Registering_a_name_twice_continues_configuring_that_server()
    {
        var services = new ServiceCollection();
        services.AddZabbix("prod", options => options.Endpoint = new Uri("https://prod.example/api_jsonrpc.php"));
        services.AddZabbix("prod", options => options.MaxConnectionsPerServer = 7);
        using ServiceProvider provider = services.BuildValidated();

        ZabbixOptions options = provider.GetRequiredService<IOptionsMonitor<ZabbixOptions>>().Get("prod");

        Assert.Equal(new Uri("https://prod.example/api_jsonrpc.php"), options.Endpoint);
        Assert.Equal(7, options.MaxConnectionsPerServer);
        Assert.Single(provider.GetKeyedServices<ZabbixClient>("prod"));
    }

    [Fact]
    public void The_unnamed_server_is_also_the_plain_service()
    {
        var services = new ServiceCollection();
        services.AddZabbix(options => options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"));
        using ServiceProvider provider = services.BuildValidated();

        Assert.Same(provider.GetRequiredService<ZabbixClient>(), provider.GetRequiredKeyedService<ZabbixClient>(Options.DefaultName));
        Assert.Same(provider.GetRequiredService<IZabbixTransport>(), provider.GetRequiredService<ZabbixClient>().Transport);
    }

    [Fact]
    public async Task A_session_credential_logs_in_once_and_then_sends_the_session_as_the_token()
    {
        using var handler = new SessionHandler();
        using Harness harness = TransportHarness.Build(handler, token: null,
            configure: services => services.AddKeyedSingleton<ZabbixCredential>(Options.DefaultName, new ZabbixSessionCredential("Admin", "zabbix")));

        await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken);
        await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken);

        Assert.Equal(["user.login", "host.get", "host.get"], handler.Methods);
        Assert.Equal(["Bearer session-1", "Bearer session-1"], handler.Authorizations);
        Assert.Equal(1, handler.Logins);
    }

    [Fact]
    public async Task A_request_is_timed_on_the_meter_with_its_method()
    {
        using var handler = new CapturingHandler("""{"jsonrpc":"2.0","result":[],"id":1}""");
        using Harness harness = TransportHarness.Build(handler);
        using var collector = new MetricCollector<double>(harness.Provider.GetRequiredService<IMeterFactory>(), ZabbixApiTelemetry.MeterName, ZabbixApiTelemetry.CallDuration);

        await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken);

        CollectedMeasurement<double> measurement = Assert.Single(collector.GetMeasurementSnapshot());
        Assert.Equal("host.get", measurement.Tags["rpc.method"]);
        Assert.False(measurement.Tags.ContainsKey("error.type"));
    }

    [Fact]
    public async Task Adding_a_server_twice_does_not_stack_the_resilience_handler()
    {
        using var failing = new AlwaysFailing();
        using Harness harness = TransportHarness.Build(failing, configure: services =>
        {
            services.AddZabbix(options => options.MaxConnectionsPerServer = 7);
            services.Configure<Microsoft.Extensions.Http.Resilience.HttpStandardResilienceOptions>(ZabbixHttp.GetResilienceName(), options =>
            {
                options.Retry.Delay = TimeSpan.FromMilliseconds(1);
                options.Retry.MaxRetryAttempts = 2;
                options.CircuitBreaker.MinimumThroughput = 1000;
            });
        });

        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await harness.Transport.SendAsync("host.get", new OutputParameters("extend"), TestJson.Default.OutputParameters, TestContext.Current.CancellationToken));

        Assert.Equal(3, failing.Attempts);                              // one pipeline: 1 + 2 retries, not 3 × 3
    }

    [Fact]
    public void The_factory_answers_an_unknown_name_with_KeyNotFoundException_and_TryCreate_with_false()
    {
        var services = new ServiceCollection();
        services.AddZabbix("prod", options => options.Endpoint = new Uri("https://prod.example/api_jsonrpc.php"));
        using ServiceProvider provider = services.BuildValidated();
        IZabbixClientFactory factory = provider.GetRequiredService<IZabbixClientFactory>();

        Assert.Throws<KeyNotFoundException>(() => factory.Create("typo"));
#pragma warning disable CA2000 // TryCreate hands out the container's instance (asserted Same below): the provider disposes it
        Assert.False(factory.TryCreate("typo", out ZabbixClient? none));
        Assert.Null(none);
        Assert.True(factory.TryCreate("prod", out ZabbixClient? prod));
#pragma warning restore CA2000
        Assert.Same(factory.Create("prod"), prod);
    }

    private sealed class AlwaysFailing : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        }
    }

    private sealed class SessionHandler : HttpMessageHandler
    {
        public List<string> Methods { get; } = [];
        public List<string?> Authorizations { get; } = [];
        public int Logins { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using System.Text.Json.JsonDocument envelope = System.Text.Json.JsonDocument.Parse(body);
            string method = envelope.RootElement.GetProperty("method").GetString()!;
            long id = envelope.RootElement.GetProperty("id").GetInt64();
            Methods.Add(method);
            string reply;
            if (method == "user.login")
            {
                Logins++;
                Assert.Null(request.Headers.Authorization);
                Assert.Equal("Admin", envelope.RootElement.GetProperty("params").GetProperty("username").GetString());
                reply = $$"""{"jsonrpc":"2.0","result":{"userid":"1","mfaid":0,"sessionid":"session-{{Logins}}"},"id":{{id}}}""";   // userData: true
            }
            else
            {
                Authorizations.Add(request.Headers.Authorization?.ToString());
                reply = $$"""{"jsonrpc":"2.0","result":[],"id":{{id}}}""";
            }
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(reply, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
