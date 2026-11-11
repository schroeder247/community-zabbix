// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace Community.Zabbix;

/// <summary>
/// Registers a <see cref="ZabbixClient"/> in a container: its named options, validated when the host starts, the named
/// <see cref="HttpClient"/> on one long-lived <see cref="SocketsHttpHandler"/> with the standard resilience handler, redaction,
/// metrics, the transport, and the client, which the container builds, shares and disposes: a caller's dispose leaves it
/// running. A named client is a keyed service, which <see cref="IZabbixClientFactory"/> returns by name.
/// <see cref="ZabbixClient.CreateBuilder"/> and the client's constructors build their client through the same registration,
/// in a container of its own.
/// </summary>
public static class ZabbixClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers a client for every server <paramref name="document"/> declares under <see cref="ZabbixConfigurationSections.Servers"/>, by name, bound
    /// to its own subsection. The section is read once here to learn the names, so a name added by a reload is not
    /// registered; everything under a name is bound, not copied, so its options monitor sees a reload, and what each
    /// consumer does with that is its own contract (for a client: <see cref="ZabbixClientOptions"/>, where only the
    /// <c>Resilience</c> words take effect live). The agents' and proxies' sections are bound by their own packages'
    /// roots (<see cref="ZabbixConfigurationSections"/>).
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="document">
    /// The <c>Zabbix</c> section, or the host's whole configuration (<c>builder.Configuration</c>): a configuration that
    /// holds none of the document's maps but a <see cref="ZabbixConfigurationSections.Section"/> is read from that section, as the agent's
    /// and the proxy's roots read it.
    /// </param>
    /// <returns>The container, so that calls can be chained.</returns>
    /// <example>
    /// <code language="json">
    /// { "Zabbix": { "Servers": { "prod": { "Endpoint": "https://zbx.example/api_jsonrpc.php", "ApiToken": "..." } } } }
    /// </code>
    /// <code language="csharp">
    /// builder.Services.AddZabbixClients(builder.Configuration.GetSection(ZabbixConfigurationSections.Section));
    /// // later, from the container
    /// ZabbixClient prod = services.GetRequiredService&lt;IZabbixClientFactory&gt;().Create("prod");
    /// </code>
    /// </example>
    public static IServiceCollection AddZabbixClients(this IServiceCollection services, IConfiguration document)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(document);

        foreach (IConfigurationSection server in DocumentOf(document).GetSection(ZabbixConfigurationSections.Servers).GetChildren())
            services.AddZabbixClient(server.Key).Bind(server);

        return services;
    }

    // The host's whole configuration passed for the document would otherwise register nothing, and say so only when a
    // server is first resolved by its name (ZabbixConfigurationSections.Maps; Zabbix.Agent's roots apply the same rule).
    private static IConfiguration DocumentOf(IConfiguration configuration)
        => !ZabbixConfigurationSections.Maps.Any(map => configuration.GetSection(map).Exists())
           && configuration.GetSection(ZabbixConfigurationSections.Section) is { } section && section.Exists()
            ? section
            : configuration;

    /// <summary>
    /// Registers the unnamed client, which is also injected as plain <see cref="ZabbixClient"/>. The container shares the
    /// client: it disposes it, and a caller's dispose leaves it running.
    /// </summary>
    /// <param name="services">The container to register the client in.</param>
    /// <returns>The client's builder, whose verbs set it up.</returns>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddZabbixClient()
    ///     .WithEndpoint("https://zabbix.example/")
    ///     .WithToken(token);
    /// </code>
    /// </example>
    public static IZabbixClientBuilder AddZabbixClient(this IServiceCollection services) => services.AddZabbixClient(Options.DefaultName);

    /// <summary>
    /// Registers a client named <paramref name="name"/>. Registering a name again continues setting up that client, as
    /// <c>AddHttpClient</c> does: the second call adds nothing to the HTTP pipeline, so the standard resilience handler is never
    /// stacked. The container shares the client: it disposes it, and a caller's dispose leaves it running.
    /// </summary>
    /// <param name="services">The container to register the client in.</param>
    /// <param name="name">The name; <see cref="Options.DefaultName"/> is the unnamed client.</param>
    /// <returns>The client's builder, whose verbs set it up.</returns>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddZabbixClient("prod")
    ///     .WithEndpoint("https://zabbix.example/")
    ///     .WithToken(token)
    ///     .WithTimeout("configuration.import", TimeSpan.FromMinutes(5));
    /// // later, from the container
    /// ZabbixClient prod = services.GetRequiredService&lt;IZabbixClientFactory&gt;().Create("prod");
    /// </code>
    /// </example>
    public static IZabbixClientBuilder AddZabbixClient(this IServiceCollection services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(name);

        var builder = new Registration(name, services);
        // The keyed root is registered exactly once per name, below; its presence says this name's pipeline exists.
        if (services.Any(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(ZabbixClient) && Equals(descriptor.ServiceKey, name)))
            return builder;

        services.AddOptions<ZabbixClientOptions>(name).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ZabbixClientOptions>, ZabbixClientOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ZabbixClientOptions>, ZabbixCredentialValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddLogging();
        services.AddZabbixTelemetry();
        services.AddRedaction();
        services.TryAddSingleton<IZabbixClientFactory, ZabbixClientFactory>();

        // The credential is a keyed service: the builder's credential verbs replace this default, a file's ApiToken, or
        // anonymous without one.
        services.TryAddKeyedSingleton<ZabbixCredential>(name, (provider, key) =>
            provider.GetRequiredService<IOptionsMonitor<ZabbixClientOptions>>().Get(NameOf(key)).ApiToken is { Length: > 0 } token
                ? new ZabbixApiTokenCredential(token)
                : ZabbixCredential.Anonymous);

        AddHttpPipeline(services, name);

        services.TryAddKeyedSingleton<IZabbixTransport>(name, static (provider, key) =>
        {
            string name = NameOf(key);
            ZabbixClientOptions options = provider.GetRequiredService<IOptionsMonitor<ZabbixClientOptions>>().Get(name);
            Uri endpoint = ZabbixClientOptions.GetApiEndpoint(options.Endpoint ?? throw new OptionsValidationException(name, typeof(ZabbixClientOptions), ["Endpoint is required"]));
            ZabbixCredential credential = provider.GetRequiredKeyedService<ZabbixCredential>(name);
            // Whatever registered the credential, none goes out in clear: the options' own validation sees only a file's
            // ApiToken, and this is where the credential and the endpoint first meet.
            if (!ReferenceEquals(credential, ZabbixCredential.Anonymous) && !ZabbixClientOptions.AllowsCredentialsTo(endpoint, options.AllowUnencryptedCredentials))
                throw new OptionsValidationException(name, typeof(ZabbixClientOptions), [ZabbixClientOptions.UnencryptedCredential]);
            return new JsonRpcTransport(
                name,
                ZabbixHttp.WithoutUserInfo(endpoint),
                provider.GetRequiredService<IHttpClientFactory>(),
                credential,
                new Dictionary<string, TimeSpan>(options.Timeouts, StringComparer.OrdinalIgnoreCase),
                provider.GetRequiredService<ILoggerFactory>().CreateLogger<JsonRpcTransport>(),
                provider.GetRequiredService<ZabbixApiTelemetry>(),
                provider.GetRequiredService<TimeProvider>());
        });
        // The container holds the client through its owner, which releases it when the container is disposed; the client it
        // hands out is shared, so a caller's dispose leaves it running.
        services.TryAddKeyedSingleton(name, static (provider, key) =>
        {
            string name = NameOf(key);
            // The server's HTTP pipeline is built before the server, so the container (which releases what it created in
            // reverse order) releases the pipeline after the server, and the server's DisposeAsync can still log out.
            using (provider.GetRequiredService<IHttpClientFactory>().CreateClient(ZabbixHttp.GetClientName(name)))
            {
            }
            return new ZabbixClientOwner(new ZabbixClient(name, provider));
        });
        services.TryAddKeyedSingleton(name, static (provider, key) => provider.GetRequiredKeyedService<ZabbixClientOwner>(key).Client);

        if (name == Options.DefaultName)
        {
            // The unnamed server is the plain unkeyed service too, so the one-server case injects IZabbixTransport.
            services.TryAddSingleton(static provider => provider.GetRequiredKeyedService<IZabbixTransport>(Options.DefaultName));
            services.TryAddSingleton(static provider => provider.GetRequiredKeyedService<ZabbixClient>(Options.DefaultName));
        }

        return builder;
    }

    /// <summary>
    /// The server's named <see cref="HttpClient"/>: one long-lived <see cref="SocketsHttpHandler"/>, no client-wide deadline,
    /// the factory's request logging removed, the standard resilience handler with the read-only retry gate and the
    /// per-method deadlines, and the buffering handler innermost.
    /// </summary>
    private static void AddHttpPipeline(IServiceCollection services, string name)
    {
        services.AddHttpClient(ZabbixHttp.GetClientName(name))
            // No client-wide deadline: every deadline is the resilience handler's (attempt and total), and the body is
            // read inside them by the innermost handler below, so HttpClient.Timeout would only be a second, outer clock.
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(provider =>
            {
                ZabbixClientOptions options = provider.GetRequiredService<IOptionsMonitor<ZabbixClientOptions>>().Get(name);
                return new SocketsHttpHandler
                {
                    MaxConnectionsPerServer = options.MaxConnectionsPerServer > 0 ? options.MaxConnectionsPerServer : int.MaxValue,
                    PooledConnectionLifetime = TimeSpan.FromMinutes(5),                          // DNS rotation without handler rotation
                    PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                    // A JSON-RPC endpoint never redirects. Followed, a 301 or 302 turns the POST into a GET without a
                    // body, whose answer then reads as "not JSON"; the redirect status is the caller's answer instead.
                    AllowAutoRedirect = false,
                };
            })
            // One primary handler for the client's lifetime: the socket cap is one pool, never fragmented by the
            // factory's rotation, without skipping the factory.
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan)
            // The factory's default request logging is removed for this client. At Trace it logs the request headers, and
            // although the message text shows "Authorization: *", the structured state carries the raw values
            // (Authorization=["Bearer …"]) on Microsoft.Extensions.Http 10.0.11, even with RedactLoggedHeaders(_ => true) in
            // place; the test "a token never renders" pins this. Any structured sink (OTLP, Seq) would ship
            // the token. The SDK's own events (ApiLog 4000-4007), the Polly events and System.Net.Http's own metrics and
            // spans still describe every exchange.
            .RemoveAllLoggers()
            .AddStandardResilienceHandler()
            .Configure(options =>
            {
                // Every JSON-RPC call is a POST, so the HTTP verb cannot say what is safe to send twice; the method
                // can. Reads retry, on a transient HTTP outcome or on a transient API error delivered with HTTP 200
                // (ZabbixApiException.IsTransient: -32603, the frontend could not start); an application error (-32500,
                // "Database error occurred." included) is the method's own answer and is never retried. A write that
                // failed may have happened, and is never repeated. When the retries run out the last reply is the
                // outcome, so the caller sees its ZabbixApiException, never the pipeline's.
                Func<RetryPredicateArguments<HttpResponseMessage>, ValueTask<bool>> transient = options.Retry.ShouldHandle;
                options.Retry.ShouldHandle = async arguments =>
                {
                    HttpRequestMessage? request = arguments.Context.GetRequestMessage();
                    if (request is null || !request.Options.TryGetValue(ZabbixHttp.Method, out string? method) || !ZabbixHttp.IsIdempotent(method))
                        return false;
                    if (await transient(arguments).ConfigureAwait(false)) return true;
                    return arguments.Outcome.Result is { } response
                           && ZabbixTransientReply.IsTransient(response);
                };

                // A method named in ZabbixClientOptions.Timeouts carries its own deadline (configuration.import is not a
                // microservice call); every other request keeps the configured ones, read when the request starts.
                options.AttemptTimeout.TimeoutGenerator = arguments => new(DeadlineOf(arguments.Context) ?? options.AttemptTimeout.Timeout);
                options.TotalRequestTimeout.TimeoutGenerator = arguments => new(DeadlineOf(arguments.Context) ?? options.TotalRequestTimeout.Timeout);
            });
        // Innermost: the body is read under the attempt's token, so a stalled body is a timed-out attempt.
        services.AddHttpClient(ZabbixHttp.GetClientName(name)).AddHttpMessageHandler(provider =>
            new ZabbixResponseBufferingHandler(provider.GetRequiredService<IOptionsMonitor<ZabbixClientOptions>>().Get(name).MaxResponseContentBytes));
    }

    // A keyed registration is resolved by the server's name, the key every one of these was registered under.
    private static string NameOf(object? key) => key as string ?? throw new InvalidOperationException("A Zabbix server is resolved by its name.");

    // The builder over the application's container: the verbs write there, and the container builds the client.
    private sealed record Registration(string Name, IServiceCollection Services) : IZabbixClientBuilder;

    private static TimeSpan? DeadlineOf(ResilienceContext context)
        => context.GetRequestMessage() is { } request && request.Options.TryGetValue(ZabbixHttp.Deadline, out TimeSpan deadline) ? deadline : null;
}
