// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Community.Zabbix.Hosting;
using Community.Zabbix.Listening;
using Community.Zabbix.Proxy;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix;

/// <summary>
/// Provides the verbs of a passive proxy's builder, the same on the builder <see cref="ZabbixPassiveProxy.CreateBuilder"/>
/// returns and on the one <see cref="ZabbixPassiveProxyServiceCollectionExtensions.AddZabbixPassiveProxy"/> returns. Each verb
/// writes the proxy's named options, which apply in the order of the calls, and returns the builder it was called on. The
/// verbs an active proxy has too are <see cref="ZabbixProxyBuilderExtensions"/>'.
/// </summary>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification = "The extension block is C# 14 syntax for extension members; the type the compiler emits for it is not one a caller names.")]
public static class ZabbixPassiveProxyBuilderExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IZabbixPassiveProxyBuilder
    {
        /// <summary>
        /// Takes the server's connections over TLS with the proxy's pre-shared key, as Zabbix's <c>TLSAccept=psk</c>,
        /// <c>TLSPSKIdentity</c> and <c>TLSPSKFile</c> do: the server connects with the identity and key it has for the proxy.
        /// A listener that accepted only unencrypted connections, as it does unless set, then accepts pre-shared-key ones only;
        /// one that accepted more kinds keeps them. A later <c>WithPsk</c> replaces it.
        /// </summary>
        /// <param name="identity">The proxy's identity, as the server has it.</param>
        /// <param name="key">
        /// The key, 16 to 256 bytes: what Zabbix's <c>TLSPSKFile</c> holds as hex digits. The options keep it as that text.
        /// </param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="identity"/> is empty, or <paramref name="key"/> isn't 16 to 256 bytes.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="identity"/> is <see langword="null"/>.</exception>
        public TBuilder WithPsk(string identity, [ZabbixSecret] ReadOnlySpan<byte> key)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentException.ThrowIfNullOrEmpty(identity);
            string hex = ZabbixListenerTls.Hex(key, nameof(key));
            builder.ConfigureListener(options =>
            {
                options.Tls ??= new ZabbixTlsOptions();
                options.Tls.PskIdentity = identity;
                options.Tls.Psk = hex;
                ZabbixListenerTls.Accept(options, ZabbixTlsAcceptModes.Psk);
            });
            return builder;
        }

        /// <summary>
        /// Shows each configuration the server pushes to <paramref name="handler"/> before it replaces the one held, which it
        /// may refuse: <see cref="ZabbixProxyConfigVerdict.Refuse"/> keeps the one held, and the server reads the reason in
        /// the proxy's <c>failed</c> answer; <see cref="ZabbixProxyConfigVerdict.Accepted"/> takes it. Without it, every
        /// configuration is taken. A later <c>OnConfig</c> replaces it.
        /// </summary>
        /// <param name="handler">Takes the configuration pushed and the one held, and answers whether to take it.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
        /// <example>
        /// <code language="csharp">
        /// builder.OnConfig((incoming, held, cancellationToken) => ValueTask.FromResult(incoming.Hosts.Count > 10_000
        ///     ? ZabbixProxyConfigVerdict.Refuse("This proxy takes at most 10,000 hosts.")
        ///     : ZabbixProxyConfigVerdict.Accepted));
        /// </code>
        /// </example>
        public TBuilder OnConfig(Func<ZabbixProxyConfig, ZabbixProxyConfig, CancellationToken, ValueTask<ZabbixProxyConfigVerdict>> handler)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(handler);
            builder.Services.Configure<ZabbixProxyOptions>(builder.Name, options => options.ConfigHandler = handler);
            return builder;
        }

        /// <summary>
        /// Answers the <c>zabbix.stats</c> requests the proxy receives with what <paramref name="handler"/> returns, as the
        /// reply's <c>data</c>. Without it, or when it answers <see langword="null"/> or throws, the reply is <c>success</c> with
        /// an empty <c>data</c>, and a failure is logged. A later <c>OnStats</c> replaces it.
        /// </summary>
        /// <param name="handler">Takes the request's JSON and answers the reply's <c>data</c>.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
        public TBuilder OnStats(Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask<IReadOnlyDictionary<string, JsonElement>?>> handler)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(handler);
            builder.Services.Configure<ZabbixProxyOptions>(builder.Name, options => options.StatsHandler = handler);
            return builder;
        }

        /// <summary>
        /// Adds the proxy's health check to the container's health checks, as <c>zabbix-passive-proxy-{name}</c> with the
        /// <c>zabbix</c> and <c>proxy</c> tags: healthy while it listens, degraded before it starts, and unhealthy when it
        /// stopped listening. A web host maps them with <c>MapHealthChecks</c>, and a worker publishes them through an
        /// <c>IHealthCheckPublisher</c>.
        /// </summary>
        /// <returns>The builder.</returns>
        public TBuilder AddHealthCheck()
        {
            ArgumentNullException.ThrowIfNull(builder);
            string name = builder.Name;
            ZabbixComponentHealthChecks.Add(builder.Services, "zabbix-passive-proxy-" + name, provider =>
            {
                ZabbixPassiveProxy proxy = provider.GetRequiredKeyedService<ZabbixPassiveProxy>(name);
                return new ZabbixListenerHealthCheck(proxy.Name, "proxy", () => proxy.Listener);
            }, ZabbixComponentHealthChecks.Proxy);
            return builder;
        }
    }
}
