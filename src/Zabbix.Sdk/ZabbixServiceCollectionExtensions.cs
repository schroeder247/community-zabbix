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
/// Provides the <c>AddZabbix</c> composition root. <c>AddZabbix</c> registers a client — the options graph with its validator, the named
/// <see cref="HttpClient"/> on one long-lived <see cref="SocketsHttpHandler"/> with the standard resilience handler,
/// redaction, metrics, the transport and the root — in the three shapes every <c>Add*</c> has: defaults,
/// a delegate, a configuration section; named for several servers in one container, unnamed for the one-server case.
/// <see cref="ZabbixClient"/> builds its own container through the same method.
/// </summary>
public static class ZabbixServiceCollectionExtensions
{
    /// <summary>
    /// Registers a client for every server <paramref name="document"/> declares under <see cref="ZabbixConfigurationSections.Servers"/>, by name, bound
    /// to its own subsection. The section is read once here to learn the names, so a name added by a reload is not
    /// registered; everything under a name is bound, not copied, so its options monitor sees a reload, and what each
    /// consumer does with that is its own contract (for a client: <see cref="ZabbixOptions"/>, where only the
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
            services.AddZabbix(server.Key, server);

        return services;
    }

    // The host's whole configuration passed for the document would otherwise register nothing, and say so only when a
    // server is first resolved by its name (ZabbixConfigurationSections.Maps; Zabbix.Agent's roots apply the same rule).
    private static IConfiguration DocumentOf(IConfiguration configuration)
        => !ZabbixConfigurationSections.Maps.Any(map => configuration.GetSection(map).Exists())
           && configuration.GetSection(ZabbixConfigurationSections.Section) is { } section && section.Exists()
            ? section
            : configuration;

    /// <summary>The default server with defaults; the endpoint and token come from a later <c>Configure&lt;ZabbixOptions&gt;</c>.</summary>
    /// <param name="services">The container.</param>
    /// <returns>The server's builder, whose verbs add to its registration.</returns>
    public static IZabbixBuilder AddZabbix(this IServiceCollection services) => services.AddZabbix(Options.DefaultName);

    /// <summary>The default server with options written in code.</summary>
    /// <param name="services">The container.</param>
    /// <param name="configure">The options.</param>
    /// <returns>The server's builder, whose verbs add to its registration.</returns>
    public static IZabbixBuilder AddZabbix(this IServiceCollection services, Action<ZabbixOptions> configure) => services.AddZabbix(Options.DefaultName, configure);

    /// <summary>The default server bound to a configuration section.</summary>
    /// <param name="services">The container.</param>
    /// <param name="section">The section.</param>
    /// <returns>The server's builder, whose verbs add to its registration.</returns>
    public static IZabbixBuilder AddZabbix(this IServiceCollection services, IConfiguration section) => services.AddZabbix(Options.DefaultName, section);

    /// <summary>A server named <paramref name="name"/> with options written in code.</summary>
    /// <param name="services">The container.</param>
    /// <param name="name">The name.</param>
    /// <param name="configure">The options.</param>
    /// <returns>The server's builder, whose verbs add to its registration.</returns>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddZabbix("prod", options =>
    /// {
    ///     options.Endpoint = new Uri("https://zabbix.example/api_jsonrpc.php");
    ///     options.ApiToken = token;
    /// });
    /// // later, from the container
    /// ZabbixClient prod = services.GetRequiredService&lt;IZabbixClientFactory&gt;().Create("prod");
    /// </code>
    /// </example>
    public static IZabbixBuilder AddZabbix(this IServiceCollection services, string name, Action<ZabbixOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        IZabbixBuilder builder = services.AddZabbix(name);
        services.AddOptions<ZabbixOptions>(name).Configure(configure);
        return builder;
    }

    /// <summary>
    /// A server named <paramref name="name"/> bound to a configuration section: the words of <see cref="ZabbixOptions"/>
    /// (<c>Endpoint</c>, <c>FrontendUri</c>, <c>ApiToken</c>, <c>AllowUnencryptedCredentials</c>, <c>MaxConnectionsPerServer</c>,
    /// <c>MaxResponseContentBytes</c>, <c>Timeouts</c>), and a <c>Resilience</c> subsection in the shape of <see cref="HttpStandardResilienceOptions"/>.
    /// The section is bound, not copied; what a reload changes is documented on <see cref="ZabbixOptions"/> (the
    /// <c>Resilience</c> words reload, the endpoint, token and connection cap do not).
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="name">The name.</param>
    /// <param name="section">The section.</param>
    /// <returns>The server's builder, whose verbs add to its registration.</returns>
    public static IZabbixBuilder AddZabbix(this IServiceCollection services, string name, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(section);
        return services.AddZabbix(name).Bind(section);
    }

    /// <summary>
    /// A server named <paramref name="name"/>. Registering a name twice continues configuring that server (the
    /// <c>AddHttpClient</c> rule): the second call adds nothing to the HTTP pipeline, so the standard resilience handler is
    /// never stacked.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="name">The name; <see cref="Options.DefaultName"/> is the unnamed client.</param>
    /// <returns>The server's builder, whose verbs add to its registration.</returns>
    public static IZabbixBuilder AddZabbix(this IServiceCollection services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(name);

        var builder = new ZabbixBuilder(name, services);
        // The keyed root is registered exactly once per name, below; its presence says this name's pipeline exists.
        if (services.Any(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(ZabbixClient) && Equals(descriptor.ServiceKey, name)))
            return builder;

        services.AddOptions<ZabbixOptions>(name).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ZabbixOptions>, ZabbixOptionsValidator>());
        services.TryAddSingleton(TimeProvider.System);
        services.AddLogging();
        services.AddZabbixTelemetry();
        services.AddRedaction();
        services.TryAddSingleton<IZabbixClientFactory, ZabbixClientFactory>();

        // The credential is a keyed service: the builder's WithCredential replaces this default, a file's ApiToken, or
        // anonymous without one.
        services.TryAddKeyedSingleton<ZabbixCredential>(name, (provider, key) =>
            provider.GetRequiredService<IOptionsMonitor<ZabbixOptions>>().Get(NameOf(key)).ApiToken is { Length: > 0 } token
                ? new ZabbixApiTokenCredential(token)
                : ZabbixCredential.Anonymous);

        AddHttpPipeline(services, name);

        services.TryAddKeyedSingleton<IZabbixTransport>(name, static (provider, key) =>
        {
            string name = NameOf(key);
            ZabbixOptions options = provider.GetRequiredService<IOptionsMonitor<ZabbixOptions>>().Get(name);
            Uri endpoint = ZabbixOptions.GetApiEndpoint(options.Endpoint ?? throw new OptionsValidationException(name, typeof(ZabbixOptions), ["Endpoint is required"]));
            ZabbixCredential credential = provider.GetRequiredKeyedService<ZabbixCredential>(name);
            // Whatever registered the credential, none goes out in clear: the options' own validation sees only a file's
            // ApiToken, and this is where the credential and the endpoint first meet.
            if (!ReferenceEquals(credential, ZabbixCredential.Anonymous) && !ZabbixOptions.AllowsCredentialsTo(endpoint, options.AllowUnencryptedCredentials))
                throw new OptionsValidationException(name, typeof(ZabbixOptions), [ZabbixOptions.UnencryptedCredential]);
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
        services.TryAddKeyedSingleton(name, static (provider, key) =>
        {
            string name = NameOf(key);
            // The server's HTTP pipeline is built before the server, so the container (which releases what it created in
            // reverse order) releases the pipeline after the server, and the server's DisposeAsync can still log out.
            using (provider.GetRequiredService<IHttpClientFactory>().CreateClient(ZabbixHttp.GetClientName(name)))
            {
            }
            return new ZabbixClient(name, provider.GetRequiredKeyedService<IZabbixTransport>(name), provider.GetRequiredKeyedService<ZabbixCredential>(name),
                provider.GetRequiredService<IOptionsMonitor<ZabbixOptions>>().Get(name),
                provider.GetRequiredService<TimeProvider>(), provider.GetRequiredService<ILoggerFactory>(), provider.GetRequiredService<Diagnostics.ZabbixTreeTelemetry>());
        });

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
                ZabbixOptions options = provider.GetRequiredService<IOptionsMonitor<ZabbixOptions>>().Get(name);
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

                // A method named in ZabbixOptions.Timeouts carries its own deadline (configuration.import is not a
                // microservice call); every other request keeps the configured ones, read when the request starts.
                options.AttemptTimeout.TimeoutGenerator = arguments => new(DeadlineOf(arguments.Context) ?? options.AttemptTimeout.Timeout);
                options.TotalRequestTimeout.TimeoutGenerator = arguments => new(DeadlineOf(arguments.Context) ?? options.TotalRequestTimeout.Timeout);
            });
        // Innermost: the body is read under the attempt's token, so a stalled body is a timed-out attempt.
        services.AddHttpClient(ZabbixHttp.GetClientName(name)).AddHttpMessageHandler(provider =>
            new ZabbixResponseBufferingHandler(provider.GetRequiredService<IOptionsMonitor<ZabbixOptions>>().Get(name).MaxResponseContentBytes));
    }

    // A keyed registration is resolved by the server's name, the key every one of these was registered under.
    private static string NameOf(object? key) => key as string ?? throw new InvalidOperationException("A Zabbix server is resolved by its name.");

    private static TimeSpan? DeadlineOf(ResilienceContext context)
        => context.GetRequestMessage() is { } request && request.Options.TryGetValue(ZabbixHttp.Deadline, out TimeSpan deadline) ? deadline : null;
}
