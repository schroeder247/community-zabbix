// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Http.Resilience;

namespace Community.Zabbix;

/// <summary>
/// Provides the verbs of a client builder: the credential, the HTTP pipeline and its resilience, each written to the
/// server's own named registrations, in the shape of <see cref="IHttpClientBuilder"/>'s verbs and the Azure client
/// builder's <c>WithCredential</c>. The order of the calls does not matter.
/// </summary>
public static class ZabbixBuilderExtensions
{
    /// <summary>
    /// Gives the client <paramref name="credential"/>, in place of the <see cref="ZabbixOptions.ApiToken"/> default. Giving
    /// both fails validation: they are two credentials for one client.
    /// </summary>
    /// <param name="builder">The server's builder, whose registrations the verb writes.</param>
    /// <param name="credential">How the client's requests prove who they are.</param>
    /// <returns>The builder, so that calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IZabbixBuilder WithCredential(this IZabbixBuilder builder, ZabbixCredential credential)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(credential);
        builder.Services.AddOptions<ZabbixOptions>(builder.Name)
            .Validate(options => ReferenceEquals(credential, ZabbixCredential.Anonymous) || ZabbixOptions.AllowsCredentialsTo(options.Endpoint, options.AllowUnencryptedCredentials),
                ZabbixOptions.UnencryptedCredential);
        return builder.WithCredential(_ => credential);
    }

    /// <summary>
    /// Gives the client the credential <paramref name="credential"/> builds from the container, in place of the
    /// <see cref="ZabbixOptions.ApiToken"/> default: a session credential whose password comes from a secret store the
    /// container holds. Giving both fails validation.
    /// </summary>
    /// <param name="builder">The server's builder, whose registrations the verb writes.</param>
    /// <param name="credential">Builds the credential, once, when the client is first resolved.</param>
    /// <returns>The builder, so that calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IZabbixBuilder WithCredential(this IZabbixBuilder builder, Func<IServiceProvider, ZabbixCredential> credential)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(credential);
        builder.Services.RemoveAllKeyed<ZabbixCredential>(builder.Name);
        builder.Services.AddKeyedSingleton(builder.Name, (provider, _) => credential(provider));
        builder.Services.AddOptions<ZabbixOptions>(builder.Name)
            .Validate(options => string.IsNullOrEmpty(options.ApiToken), "The options carry an ApiToken beside the credential given in code; give one of them.");
        return builder;
    }

    /// <summary>
    /// Configures the client's named <see cref="HttpClient"/>: a delegating handler (<c>AddHttpMessageHandler</c>), a proxy
    /// or a client certificate on the primary handler (<c>ConfigurePrimaryHttpMessageHandler</c>). The SDK's own pipeline
    /// (one long-lived <see cref="SocketsHttpHandler"/>, the standard resilience handler, the response buffering handler)
    /// is already in place; a handler added here runs inside it, and a primary handler given here replaces the SDK's.
    /// </summary>
    /// <param name="builder">The server's builder, whose registrations the verb writes.</param>
    /// <param name="configure">Configures the client's <see cref="IHttpClientBuilder"/>.</param>
    /// <returns>The builder, so that calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IZabbixBuilder ConfigureHttpClient(this IZabbixBuilder builder, Action<IHttpClientBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        configure(builder.Services.AddHttpClient(ZabbixHttp.GetClientName(builder.Name)));
        return builder;
    }

    /// <summary>
    /// Binds the client's options to a configuration section: the words of <see cref="ZabbixOptions"/> (<c>Endpoint</c>,
    /// <c>FrontendUri</c>, <c>ApiToken</c>, <c>AllowUnencryptedCredentials</c>, <c>MaxConnectionsPerServer</c>,
    /// <c>MaxResponseContentBytes</c>, <c>Timeouts</c>), and a <c>Resilience</c> subsection in the shape of
    /// <see cref="HttpStandardResilienceOptions"/>, exactly as the host builders' <c>Bind</c> verbs do. The section is
    /// bound, not copied; what a reload changes is documented on <see cref="ZabbixOptions"/>.
    /// </summary>
    /// <param name="builder">The server's builder, whose registrations the verb writes.</param>
    /// <param name="section">The server's section.</param>
    /// <returns>The builder, so that calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IZabbixBuilder Bind(this IZabbixBuilder builder, IConfiguration section)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(section);
        builder.Services.AddOptions<ZabbixOptions>(builder.Name).Bind(section);
        builder.Services.AddOptions<HttpStandardResilienceOptions>(ZabbixHttp.GetResilienceName(builder.Name)).Bind(section.GetSection("Resilience"));
        return builder;
    }

    /// <summary>
    /// Configures the client's standard resilience handler: its retry, attempt and total timeouts, circuit breaker and
    /// rate limiter. The SDK's read-only retry gate and per-method deadlines are set first; a <c>Retry.ShouldHandle</c>
    /// given here replaces the gate, so a write could then be sent twice.
    /// </summary>
    /// <param name="builder">The server's builder, whose registrations the verb writes.</param>
    /// <param name="configure">Configures the client's <see cref="HttpStandardResilienceOptions"/>.</param>
    /// <returns>The builder, so that calls can be chained.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IZabbixBuilder ConfigureResilience(this IZabbixBuilder builder, Action<HttpStandardResilienceOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);
        builder.Services.Configure(ZabbixHttp.GetResilienceName(builder.Name), configure);
        return builder;
    }
}
