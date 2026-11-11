// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>The root without a container: the same composition root, its own provider behind it.</summary>
public sealed class ZabbixClientTests
{
    [Fact]
    public void The_facade_builds_its_own_graph_from_the_same_composition_root()
    {
        using var client = new ZabbixClient(new Uri("https://zbx.example/api_jsonrpc.php"), new ZabbixApiTokenCredential("tok"));

        Assert.NotNull(client.Transport);
        Assert.Equal("https://zbx.example/api_jsonrpc.php", client.Endpoint.AbsoluteUri);                      // Uri.Equals ignores user info
    }

    [Fact]
    public void An_anonymous_server_is_asked_for_by_name()
    {
        using var client = new ZabbixClient(new Uri("https://zbx.example/api_jsonrpc.php"), ZabbixCredential.Anonymous);

        Assert.NotNull(client.Transport);
    }

    [Fact]
    public void A_token_in_the_options_beside_a_credential_given_in_code_is_refused_rather_than_ignored()
    {
        // The options type has an ApiToken; a client built with a credential and options carrying one would hold two
        // credentials, and one of them would be silently dropped.
        InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => ZabbixClient.CreateBuilder()
            .WithEndpoint("https://zbx.example/api_jsonrpc.php")
            .WithCredential(ZabbixCredential.Anonymous)
            .Configure(options => options.ApiToken = "tok")
            .Build()
            .Dispose());

        Assert.Contains("ApiToken", refused.Message, StringComparison.Ordinal);
        Assert.IsType<OptionsValidationException>(refused.InnerException);
    }

    [Fact]
    public void A_later_endpoint_replaces_an_earlier_one_whichever_verb_set_it()
    {
        using ZabbixClient configured = ZabbixClient.CreateBuilder()
            .WithEndpoint("https://other.example/")
            .Configure(options => options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"))
            .Build();
        using ZabbixClient named = ZabbixClient.CreateBuilder()
            .Configure(options => options.Endpoint = new Uri("https://other.example/"))
            .WithEndpoint("https://zbx.example/api_jsonrpc.php")
            .Build();

        Assert.Equal("https://zbx.example/api_jsonrpc.php", configured.Endpoint.AbsoluteUri);
        Assert.Equal("https://zbx.example/api_jsonrpc.php", named.Endpoint.AbsoluteUri);
    }

    [Fact]
    public async Task The_constructor_s_credential_and_options_reach_the_wire()
    {
        using var api = new ScriptedApi().On("host.get", "[]");
        var authorizations = new List<string?>();
        using var recording = new Recording(api, authorizations);
        var services = new ServiceCollection();
        services.AddZabbixClient().Configure(options => options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"))
            .WithCredential(new ZabbixApiTokenCredential("given-in-code"))
            .ConfigureHttpClientBuilder(http => http.ConfigurePrimaryHttpMessageHandler(() => recording));
        await using ServiceProvider provider = services.BuildValidated();
        ZabbixClient client = provider.GetRequiredService<ZabbixClient>();

        JsonElement result = await client.SendAsync(ZabbixApiMethod.HostGet, JsonDocument.Parse("""{"countOutput":true}""").RootElement, TestContext.Current.CancellationToken);

        Assert.Equal(JsonValueKind.Array, result.ValueKind);
        Assert.Equal(["Bearer given-in-code"], authorizations);
        Assert.True(api.ParamsOf("host.get").Single().GetProperty("countOutput").GetBoolean());
    }

    [Fact]
    public void A_credential_given_in_code_beside_a_token_in_the_options_fails_validation()
    {
        var services = new ServiceCollection();
        services.AddZabbixClient().Configure(options =>
        {
            options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php");
            options.ApiToken = "from-a-file";
        }).WithCredential(new ZabbixApiTokenCredential("given-in-code"));
        using ServiceProvider provider = services.BuildServiceProvider();

        OptionsValidationException refused = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptionsMonitor<ZabbixClientOptions>>().Get(Options.DefaultName));

        Assert.Contains("ApiToken", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_resilience_verb_writes_the_server_s_own_resilience_options()
    {
        var services = new ServiceCollection();
        services.AddZabbixClient("prod").Configure(options => options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"))
            .ConfigureResilience(resilience => resilience.Retry.MaxRetryAttempts = 7);
        using ServiceProvider provider = services.BuildServiceProvider();
        IOptionsMonitor<HttpStandardResilienceOptions> resilience = provider.GetRequiredService<IOptionsMonitor<HttpStandardResilienceOptions>>();

        Assert.Equal(7, resilience.Get(ZabbixHttp.GetResilienceName("prod")).Retry.MaxRetryAttempts);
        Assert.NotEqual(7, resilience.Get(ZabbixHttp.GetResilienceName()).Retry.MaxRetryAttempts);
    }

    [Theory]
    [InlineData("http://zbx.example/api_jsonrpc.php", false, false)]
    [InlineData("http://zbx.example/api_jsonrpc.php", true, true)]
    [InlineData("http://localhost/api_jsonrpc.php", false, true)]
    [InlineData("http://127.0.0.1:8080/api_jsonrpc.php", false, true)]
    [InlineData("https://zbx.example/api_jsonrpc.php", false, true)]
    public void A_credential_is_never_sent_in_clear_to_another_host_unless_allowed(string endpoint, bool allow, bool built)
    {
        ZabbixClientBuilder builder = ZabbixClient.CreateBuilder().WithEndpoint(endpoint).WithCredential(new ZabbixApiTokenCredential("tok"));
        if (allow) builder.AllowUnencryptedCredentials();

        if (built)
        {
            using ZabbixClient client = builder.Build();
            Assert.NotNull(client.Transport);
        }
        else
        {
            InvalidOperationException refused = Assert.Throws<InvalidOperationException>(() => builder.Build().Dispose());
            Assert.Contains("AllowUnencryptedCredentials", refused.Message, StringComparison.Ordinal);
            Assert.IsType<OptionsValidationException>(refused.InnerException);
            Assert.Throws<OptionsValidationException>(() => new ZabbixClient(new Uri(endpoint), "tok").Dispose());   // the constructor, as the builder
        }
    }

    [Fact]
    public void An_anonymous_server_may_speak_plain_http()
    {
        using var client = new ZabbixClient(new Uri("http://zbx.example/api_jsonrpc.php"), ZabbixCredential.Anonymous);

        Assert.NotNull(client.Transport);
    }

    [Fact]
    public void A_token_in_a_document_to_a_plain_http_endpoint_fails_validation()
    {
        var options = new ZabbixClientOptions { Endpoint = new Uri("http://zbx.example/api_jsonrpc.php"), ApiToken = "tok" };

        List<System.ComponentModel.DataAnnotations.ValidationResult> results = [.. options.Validate(new System.ComponentModel.DataAnnotations.ValidationContext(options))];

        Assert.Contains(results, result => result.MemberNames.Contains(nameof(ZabbixClientOptions.ApiToken)) && result.ErrorMessage!.Contains("in clear", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Code_that_takes_a_server_is_tested_with_a_derived_stand_in()
    {
        using ZabbixClient client = new StandIn();

        ZabbixValue value = await client.Hosts["web-01"].Items["agent.ping"].GetLastValueAsync(TestContext.Current.CancellationToken);

        Assert.Equal("1", value.Value);
        Assert.Equal("stand-in", client.Name);
    }

    private sealed class StandIn : ZabbixClient
    {
        public override string Name => "stand-in";

        public override ZabbixHostClient Hosts { get; } = new StandInHosts();
    }

    private sealed class StandInHosts : ZabbixHostClient
    {
        public override ZabbixHostNode this[string name] => new StandInHost();
    }

    private sealed class StandInHost : ZabbixHostNode
    {
        public override ZabbixItemScope Items { get; } = new StandInItems();
    }

    private sealed class StandInItems : ZabbixItemScope
    {
        public override ZabbixItemNode this[string key] => new StandInItem();
    }

    private sealed class StandInItem : ZabbixItemNode
    {
        public override Task<ZabbixValue> GetLastValueAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ZabbixValue("web-01", "agent.ping", "1", DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public async Task The_api_version_is_read_once_without_an_authorization_header_and_kept()
    {
        using var api = new ScriptedApi().On("apiinfo.version", "\"7.0.31\"");
        var authorizations = new List<string?>();
        using var recording = new Recording(api, authorizations);
        using Harness harness = TransportHarness.Build(recording);
        ZabbixClient client = harness.Provider.GetRequiredService<ZabbixClient>();

        Version first = await client.GetApiVersionAsync(TestContext.Current.CancellationToken);
        Version second = await client.GetApiVersionAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new Version(7, 0, 31), first);
        Assert.Same(first, second);
        Assert.Single(api.Calls);
        Assert.Equal([null], authorizations);
        Assert.Contains(harness.Logs.GetSnapshot(), record => record.Id.Id == 4005);
    }

    [Fact]
    public async Task A_failed_version_read_is_not_kept()
    {
        int calls = 0;
        using var api = new ScriptedApi().On("apiinfo.version", _ => ++calls == 1 ? "\"not a version\"" : "\"8.0.0\"");
        using Harness harness = TransportHarness.Build(api);
        ZabbixClient client = harness.Provider.GetRequiredService<ZabbixClient>();

        await Assert.ThrowsAsync<InvalidDataException>(() => client.GetApiVersionAsync(TestContext.Current.CancellationToken));

        Assert.Equal(new Version(8, 0, 0), await client.GetApiVersionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_shared_version_read_is_aborted_at_the_server_only_when_its_last_waiter_stops_waiting()
    {
        using var api = new ScriptedApi().On("apiinfo.version", "\"7.0.31\"");
        using var stalling = new Stalling(api);
        // A clock that never moves: no attempt timeout can be what aborts the request.
        using Harness harness = TransportHarness.Build(stalling, configure: services => services.AddSingleton<TimeProvider>(new Microsoft.Extensions.Time.Testing.FakeTimeProvider()));
        ZabbixClient client = harness.Provider.GetRequiredService<ZabbixClient>();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();

        Task<Version> one = client.GetApiVersionAsync(first.Token);
        Task<Version> two = client.GetApiVersionAsync(second.Token);
        CancellationToken request = await stalling.Arrived.Task.WaitAsync(TestContext.Current.CancellationToken);
        await first.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => one);
        bool abortedWhileOneStillWaited = request.IsCancellationRequested;               // a shared read outlives one waiter …
        await second.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => two);
        await stalling.Aborted.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);   // … and not the last one (a failsafe, not a pace)

        Assert.False(abortedWhileOneStillWaited);
        stalling.Pass = true;
        Assert.Equal(new Version(7, 0, 31), await client.GetApiVersionAsync(TestContext.Current.CancellationToken));   // an abandoned read is not kept
        // Nothing of the aborted read is still in flight, so the container disposes on a clock that never moves
        // (Polly's registry polls a pending execution on the container's clock). A failsafe, not a pace.
        await harness.Provider.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_endpoint_s_user_info_is_never_sent_logged_traced_or_handed_back()
    {
        // http://admin:<secret>@host must not reach the 4005 line or ZabbixClient.Endpoint whole. HttpClient never sends
        // a URI's user info, so the SDK keeps none: every endpoint it holds, logs or traces is the endpoint without it.
        const string Secret = "zbx-test-sdk-fix-URL-SECRET";
        using var api = new ScriptedApi().On("apiinfo.version", "\"7.0.31\"");
        var requests = new List<HttpRequestMessage>();
        using var recording = new Capturing(api, requests);
        using var spans = new System.Diagnostics.ActivityListener
        {
            ShouldListenTo = source => source.Name.StartsWith("Community.Zabbix", StringComparison.Ordinal),
            Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
        };
        var tags = new System.Collections.Concurrent.ConcurrentBag<string>();
        spans.ActivityStopped = activity => { foreach (KeyValuePair<string, string?> tag in activity.Tags) tags.Add(tag.Value ?? string.Empty); };
        System.Diagnostics.ActivitySource.AddActivityListener(spans);
        using Harness harness = TransportHarness.Build(recording, configure: services =>
            services.Configure<ZabbixClientOptions>(options => options.Endpoint = new Uri($"https://admin:{Secret}@zbx.example/api_jsonrpc.php")));
        ZabbixClient client = harness.Provider.GetRequiredService<ZabbixClient>();

        await client.GetApiVersionAsync(TestContext.Current.CancellationToken);

        Assert.Equal("https://zbx.example/api_jsonrpc.php", client.Endpoint.AbsoluteUri);                      // Uri.Equals ignores user info
        HttpRequestMessage request = Assert.Single(requests);
        Assert.Equal(string.Empty, request.RequestUri!.UserInfo);
        Assert.Null(request.Headers.Authorization);
        Assert.DoesNotContain(harness.Logs.GetSnapshot(), record => record.Message.Contains(Secret, StringComparison.Ordinal)
            || (record.StructuredState?.Any(pair => pair.Value?.Contains(Secret, StringComparison.Ordinal) == true) ?? false));
        Assert.DoesNotContain(tags, tag => tag.Contains(Secret, StringComparison.Ordinal));
        Assert.DoesNotContain(Secret, client.Endpoint.ToString(), StringComparison.Ordinal);
    }

    private sealed class Capturing(HttpMessageHandler inner, List<HttpRequestMessage> requests) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (requests) requests.Add(request);
            return base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class Stalling(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        public TaskCompletionSource<CancellationToken> Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Aborted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public volatile bool Pass;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Pass) return await base.SendAsync(request, cancellationToken);
            using CancellationTokenRegistration aborted = cancellationToken.Register(() => Aborted.TrySetResult());
            Arrived.TrySetResult(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed class Recording(HttpMessageHandler inner, List<string?> authorizations) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            authorizations.Add(request.Headers.Authorization?.ToString());
            return base.SendAsync(request, cancellationToken);
        }
    }
}
