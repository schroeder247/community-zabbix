// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Community.Zabbix.Hosting;
using Community.Zabbix.Listening;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>
/// Provides the verbs of an active proxy's builder, the same on the builder <see cref="ZabbixActiveProxy.CreateBuilder"/>
/// returns and on the one <see cref="ZabbixActiveProxyServiceCollectionExtensions.AddZabbixActiveProxy"/> returns. Each verb
/// writes the proxy's named options, which apply in the order of the calls, and returns the builder it was called on. The
/// verbs a passive proxy has too are <see cref="ZabbixProxyBuilderExtensions"/>'.
/// </summary>
/// <remarks>
/// The options keep the text that configuration spells, a server as <c>host:port</c> and a key as hex digits, so
/// <c>Bind</c> can fill them. The verbs take the .NET types and write that text.
/// </remarks>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification = "The extension block is C# 14 syntax for extension members; the type the compiler emits for it is not one a caller names.")]
public static class ZabbixActiveProxyBuilderExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IZabbixActiveProxyBuilder
    {
        /// <summary>
        /// Sets the server the proxy reports to, in Zabbix's spelling: one server, or the nodes of one HA cluster joined by
        /// <c>;</c>, tried in order. A proxy reports to one server or cluster, as Zabbix's proxy does, so a later call
        /// replaces it.
        /// </summary>
        /// <param name="server">
        /// The server: <c>host</c> or <c>host:port</c>, on port 10051 when it names none, or the nodes of one HA cluster:
        /// <c>"zbx-1;zbx-2"</c>. An IPv6 address with a port is bracketed: <c>[::1]:10051</c>.
        /// </param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="server"/> is empty, isn't a node, names a node twice, or names more than one server (<c>,</c>).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
        public TBuilder WithServer(string server)
        {
            ArgumentNullException.ThrowIfNull(builder);
            string text = ZabbixServerActiveText.Server(server, nameof(server));
            builder.Services.Configure<ZabbixProxyOptions>(builder.Name, options => options.Server = text);
            return builder;
        }

        /// <summary>
        /// Sets the server the proxy reports to: one server, or the nodes of one HA cluster, tried in order. A proxy reports
        /// to one server or cluster, as Zabbix's proxy does, so a later call replaces it.
        /// </summary>
        /// <param name="nodes">The server's nodes, in failover order.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException">No node, a node named twice, or a node that isn't a host and port.</exception>
        public TBuilder WithServer(params ReadOnlySpan<DnsEndPoint> nodes)
        {
            ArgumentNullException.ThrowIfNull(builder);
            string text = ZabbixServerActiveText.Destination(nodes, nameof(nodes));
            builder.Services.Configure<ZabbixProxyOptions>(builder.Name, options => options.Server = text);
            return builder;
        }

        /// <summary>
        /// Encrypts the proxy's connection to its server with the proxy's pre-shared key, as Zabbix's <c>TLSConnect=psk</c>,
        /// <c>TLSPSKIdentity</c> and <c>TLSPSKFile</c> do. The server's settings for the proxy must accept the same identity
        /// and key. A later <c>WithPsk</c> replaces it.
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
            builder.Services.Configure<ZabbixProxyOptions>(builder.Name, options =>
                options.Connection.Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = identity, Psk = hex });
            return builder;
        }

        /// <summary>
        /// Adds the proxy's health check to the container's health checks, as <c>zabbix-active-proxy-{name}</c> with the
        /// <c>zabbix</c> and <c>proxy</c> tags: unhealthy when values wait and nothing has reached the server for a while,
        /// and degraded when the store fills or the server refuses a page. A web host maps them with <c>MapHealthChecks</c>,
        /// and a worker publishes them through an <c>IHealthCheckPublisher</c>.
        /// </summary>
        /// <returns>The builder.</returns>
        public TBuilder AddHealthCheck() => builder.AddHealthCheck(static _ => { });

        /// <summary>Adds the proxy's health check, as <c>AddHealthCheck()</c> does, with its thresholds set.</summary>
        /// <param name="configure">Sets the thresholds.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="OptionsValidationException">The thresholds don't validate.</exception>
        public TBuilder AddHealthCheck(Action<ZabbixComponentHealthCheckOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            string name = builder.Name;
            ZabbixComponentHealthChecks.Add(builder.Services, "zabbix-active-proxy-" + name, provider => [provider.GetRequiredKeyedService<ZabbixActiveProxy>(name).Status],
                configure, ZabbixComponentHealthChecks.Proxy);
            return builder;
        }
    }
}
