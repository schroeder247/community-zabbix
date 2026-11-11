// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;

namespace Community.Zabbix;

/// <summary>
/// Provides the verbs of an API client's builder, the same on the builder <see cref="ZabbixClient.CreateBuilder"/> returns and
/// on the one <see cref="ZabbixClientServiceCollectionExtensions.AddZabbixClient(IServiceCollection, string)"/> returns. Each verb writes
/// the client's named registrations, which apply in the order of the calls, and returns the builder it was called on.
/// </summary>
/// <remarks>
/// The options keep the text that configuration spells, so <see cref="Bind"/> can fill them. <see cref="WithToken"/>,
/// <see cref="WithLogin"/> and <c>WithCredential</c> set the one credential, and a later one replaces an earlier one.
/// </remarks>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification = "The extension block is C# 14 syntax for extension members; the type the compiler emits for it is not one a caller names.")]
public static class ZabbixClientBuilderExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IZabbixClientBuilder
    {
        /// <summary>Sets where the server is: the web frontend's address, or the API's JSON-RPC endpoint.</summary>
        /// <param name="endpoint">
        /// The frontend's address, such as <c>https://zabbix.example/</c>, or the endpoint, <c>https://zabbix.example/api_jsonrpc.php</c>
        /// (<see cref="ZabbixClientOptions.GetApiEndpoint"/>).
        /// </param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="endpoint"/> isn't an absolute address.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is <see langword="null"/>.</exception>
        public TBuilder WithEndpoint(Uri endpoint)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(endpoint);
            if (!endpoint.IsAbsoluteUri) throw new ArgumentException("The endpoint is an absolute address, such as https://zabbix.example/.", nameof(endpoint));
            builder.Services.Configure<ZabbixClientOptions>(builder.Name, options => options.Endpoint = endpoint);
            return builder;
        }

        /// <summary>Sets where the server is: the web frontend's address, or the API's JSON-RPC endpoint.</summary>
        /// <param name="endpoint">
        /// The frontend's address, such as <c>https://zabbix.example/</c>, or the endpoint, <c>https://zabbix.example/api_jsonrpc.php</c>
        /// (<see cref="ZabbixClientOptions.GetApiEndpoint"/>).
        /// </param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="endpoint"/> is empty.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="endpoint"/> is <see langword="null"/>.</exception>
        /// <exception cref="UriFormatException"><paramref name="endpoint"/> isn't an absolute address.</exception>
        public TBuilder WithEndpoint(string endpoint)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
            return builder.WithEndpoint(new Uri(endpoint, UriKind.Absolute));
        }

        /// <summary>
        /// Sends an API token (<em>Users, API tokens</em> in the frontend) with every call, as <c>Authorization: Bearer</c>. The
        /// token is the client's credential: a later <see cref="WithToken"/>, <see cref="WithLogin"/> or
        /// <c>WithCredential</c> replaces it. It's never logged.
        /// </summary>
        /// <param name="token">The API token.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="token"/> is empty, or holds whitespace or a control character.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="token"/> is <see langword="null"/>.</exception>
        public TBuilder WithToken([ZabbixSecret] string token)
            => builder.WithCredential(ZabbixCredential.FromApiToken(token));

        /// <summary>
        /// Logs in with a user's name and password (<c>user.login</c>) on the first call that needs a session, and again when the
        /// server ends it. The login is the client's credential: a later <see cref="WithToken"/>, <see cref="WithLogin"/> or
        /// <c>WithCredential</c> replaces it.
        /// </summary>
        /// <param name="login">The user's name and password; only those two are read.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException">The user's name or password is empty.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="login"/> is <see langword="null"/>.</exception>
        public TBuilder WithLogin(NetworkCredential login)
        {
            ArgumentNullException.ThrowIfNull(login);
            return builder.WithCredential(ZabbixCredential.FromLogin(login.UserName, login.Password));
        }

        /// <summary>
        /// Gives the client <paramref name="credential"/>: your own, such as one that reads a vault, or
        /// <see cref="ZabbixCredential.Anonymous"/>, which sends no token. A later <see cref="WithToken"/>,
        /// <see cref="WithLogin"/> or <c>WithCredential</c> replaces it. Giving the options'
        /// <see cref="ZabbixClientOptions.ApiToken"/> too fails validation: they're two credentials for one client.
        /// </summary>
        /// <param name="credential">How the client's requests prove who they are.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="credential"/> is <see langword="null"/>.</exception>
        public TBuilder WithCredential(ZabbixCredential credential)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(credential);
            Give(builder, new ZabbixCredentialInCode(credential), _ => credential);
            return builder;
        }

        /// <summary>
        /// Gives the client the credential <paramref name="credential"/> builds from the container, such as a session whose
        /// password comes from a secret store the container holds. A later credential verb replaces it, and giving the options'
        /// <see cref="ZabbixClientOptions.ApiToken"/> too fails validation.
        /// </summary>
        /// <param name="credential">Builds the credential, once, when the client is first resolved.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="credential"/> is <see langword="null"/>.</exception>
        public TBuilder WithCredential(Func<IServiceProvider, ZabbixCredential> credential)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(credential);
            Give(builder, new ZabbixCredentialInCode(null), credential);
            return builder;
        }

        /// <summary>
        /// Lets the credential go over plain <c>http</c> to a host that isn't a loopback host, where it crosses the network in clear
        /// on every call (<see cref="ZabbixClientOptions.AllowUnencryptedCredentials"/>). Without it, such a client doesn't validate.
        /// </summary>
        /// <returns>The builder.</returns>
        public TBuilder AllowUnencryptedCredentials()
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.Configure<ZabbixClientOptions>(builder.Name, options => options.AllowUnencryptedCredentials = true);
            return builder;
        }

        /// <summary>Sets where the web frontend is, when it isn't at the endpoint's folder: where its page links open (<see cref="ZabbixClientOptions.FrontendUri"/>).</summary>
        /// <param name="frontend">The folder that holds <c>zabbix.php</c>, such as <c>https://zabbix.example/</c>: absolute, <c>http</c> or <c>https</c>.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="frontend"/> is <see langword="null"/>.</exception>
        public TBuilder WithFrontend(Uri frontend)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(frontend);
            builder.Services.Configure<ZabbixClientOptions>(builder.Name, options => options.FrontendUri = frontend);
            return builder;
        }

        /// <summary>Sets how many connections the client holds open to the server at most (<see cref="ZabbixClientOptions.MaxConnectionsPerServer"/>).</summary>
        /// <param name="maxConnectionsPerServer">The most connections; 100 unless set, and 0 for the platform's default.</param>
        /// <returns>The builder.</returns>
        public TBuilder WithMaxConnectionsPerServer(int maxConnectionsPerServer)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.Configure<ZabbixClientOptions>(builder.Name, options => options.MaxConnectionsPerServer = maxConnectionsPerServer);
            return builder;
        }

        /// <summary>Sets the largest reply the client reads, in bytes: a larger one fails its call (<see cref="ZabbixClientOptions.MaxResponseContentBytes"/>).</summary>
        /// <param name="maxResponseContentBytes">The most bytes one reply may hold; 1 GiB unless set.</param>
        /// <returns>The builder.</returns>
        public TBuilder WithMaxResponseContentBytes(long maxResponseContentBytes)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.Configure<ZabbixClientOptions>(builder.Name, options => options.MaxResponseContentBytes = maxResponseContentBytes);
            return builder;
        }

        /// <summary>
        /// Sets how long one call may take, its retries included: the resilience handler's attempt and total timeouts. A method
        /// <c>WithTimeout(method, timeout)</c> names keeps its own.
        /// </summary>
        /// <param name="timeout">The longest a call may take; unless set, 10 seconds an attempt and 30 seconds in all.</param>
        /// <returns>The builder.</returns>
        public TBuilder WithTimeout(TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(builder);
            // One call may take the whole deadline: the attempt's and the total's, and a circuit breaker that samples at least two
            // attempts, as the standard options' own validation asks.
            builder.Services.Configure<HttpStandardResilienceOptions>(ZabbixHttp.GetResilienceName(builder.Name), options =>
            {
                options.AttemptTimeout.Timeout = timeout;
                options.TotalRequestTimeout.Timeout = timeout;
                if (options.CircuitBreaker.SamplingDuration < timeout * 2) options.CircuitBreaker.SamplingDuration = timeout * 2;
            });
            return builder;
        }

        /// <summary>
        /// Sets how long a call to <paramref name="method"/> may take, in place of the attempt and total timeouts: for a call the
        /// others don't fit, such as <c>configuration.import</c> of a large template (<see cref="ZabbixClientOptions.Timeouts"/>).
        /// </summary>
        /// <param name="method">The API method, such as <c>history.get</c>, matched without regard to case.</param>
        /// <param name="timeout">The longest a call to the method may take.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="method"/> is empty.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="method"/> is <see langword="null"/>.</exception>
        public TBuilder WithTimeout(string method, TimeSpan timeout)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrWhiteSpace(method);
            builder.Services.Configure<ZabbixClientOptions>(builder.Name, options => options.Timeouts[method] = timeout);
            return builder;
        }

        /// <summary>Sets any of the client's options, in order with the other verbs.</summary>
        /// <param name="configure">Sets the options.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
        /// <example>
        /// <code language="csharp">
        /// builder.Configure(options => options.Timeouts["configuration.import"] = TimeSpan.FromMinutes(5));
        /// </code>
        /// </example>
        public TBuilder Configure(Action<ZabbixClientOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            builder.Services.Configure(builder.Name, configure);
            return builder;
        }

        /// <summary>
        /// Binds the client's options to a configuration section, in order with the other verbs: the words of
        /// <see cref="ZabbixClientOptions"/> (<c>Endpoint</c>, <c>FrontendUri</c>, <c>ApiToken</c>, <c>AllowUnencryptedCredentials</c>,
        /// <c>MaxConnectionsPerServer</c>, <c>MaxResponseContentBytes</c>, <c>Timeouts</c>), and a <c>Resilience</c> subsection in
        /// the shape of <see cref="HttpStandardResilienceOptions"/>. The section is bound, not copied; what a reload changes is
        /// documented on <see cref="ZabbixClientOptions"/>.
        /// </summary>
        /// <param name="section">The client's section.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="section"/> is <see langword="null"/>.</exception>
        public TBuilder Bind(IConfiguration section)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(section);
            builder.Services.AddOptions<ZabbixClientOptions>(builder.Name).Bind(section);
            builder.Services.AddOptions<HttpStandardResilienceOptions>(ZabbixHttp.GetResilienceName(builder.Name)).Bind(section.GetSection("Resilience"));
            return builder;
        }

        /// <summary>
        /// Configures the builder of the client's named <see cref="HttpClient"/>, as <c>ConfigureHttpClientDefaults</c> takes one: a
        /// delegating handler (<c>AddHttpMessageHandler</c>), a proxy or a client certificate on the primary handler
        /// (<c>ConfigurePrimaryHttpMessageHandler</c>). The SDK's own pipeline
        /// (one long-lived <see cref="SocketsHttpHandler"/>, the standard resilience handler, the response buffering handler)
        /// is already in place; a handler added here runs inside it, and a primary handler given here replaces the SDK's.
        /// </summary>
        /// <param name="configure">Configures the client's <see cref="IHttpClientBuilder"/>.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
        public TBuilder ConfigureHttpClientBuilder(Action<IHttpClientBuilder> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            configure(builder.Services.AddHttpClient(ZabbixHttp.GetClientName(builder.Name)));
            return builder;
        }

        /// <summary>
        /// Configures the client's standard resilience handler: its retry, attempt and total timeouts, circuit breaker and
        /// rate limiter. The SDK's read-only retry gate and per-method deadlines are set first; a <c>Retry.ShouldHandle</c>
        /// given here replaces the gate, so a write could then be sent twice.
        /// </summary>
        /// <param name="configure">Configures the client's <see cref="HttpStandardResilienceOptions"/>.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
        public TBuilder ConfigureResilience(Action<HttpStandardResilienceOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            builder.Services.Configure(ZabbixHttp.GetResilienceName(builder.Name), configure);
            return builder;
        }
    }

    // The one credential of a client: the last verb's, which the container builds once. The record beside it tells the options'
    // validation (ZabbixCredentialValidator) that a credential was given in code, and which, when it's an instance.
    private static void Give(IZabbixClientBuilder builder, ZabbixCredentialInCode given, Func<IServiceProvider, ZabbixCredential> credential)
    {
        builder.Services.RemoveAllKeyed<ZabbixCredential>(builder.Name);
        builder.Services.AddKeyedSingleton(builder.Name, (provider, _) => credential(provider));
        builder.Services.RemoveAllKeyed<ZabbixCredentialInCode>(builder.Name);
        builder.Services.AddKeyedSingleton(builder.Name, given);
    }
}
