// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Community.Zabbix.Proxy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix;

/// <summary>
/// Provides the verbs both proxies' builders have, the same on the builder <c>CreateBuilder</c> returns and on the one
/// <c>AddZabbixActiveProxy</c> or <c>AddZabbixPassiveProxy</c> returns. Each verb writes the proxy's named options, which
/// apply in the order of the calls, and returns the builder it was called on. Each proxy's own verbs are
/// <see cref="ZabbixActiveProxyBuilderExtensions"/>' and <see cref="ZabbixPassiveProxyBuilderExtensions"/>'; the store's,
/// <see cref="ZabbixStoreBuilderExtensions"/>', and the listener's, <see cref="ZabbixListenerBuilderExtensions"/>'.
/// </summary>
/// <remarks>
/// The options keep the text that configuration spells, so <see cref="Bind"/> can fill them. A provider and a handler are
/// what only code can give.
/// </remarks>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification = "The extension block is C# 14 syntax for extension members; the type the compiler emits for it is not one a caller names.")]
public static class ZabbixProxyBuilderExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IZabbixProxyBuilder
    {
        /// <summary>
        /// Sets up the proxy's trapper, the listener that agents and senders reach it on, and the server too for a passive
        /// proxy: its port, Zabbix's <c>ListenPort</c>, and the rest of its options. Calling it again sets up the same trapper,
        /// the last word winning. The options are read once, at start, so a reload doesn't move the port.
        /// </summary>
        /// <param name="port">The port; <c>0</c> lets the platform pick one.</param>
        /// <param name="configure">The rest of the trapper's options, the ones a push agent's trapper takes too.</param>
        /// <returns>The builder.</returns>
        public TBuilder AddTrapper(int port, Action<ZabbixListenerOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.ConfigureListener(options =>
            {
                options.Port = port;
                configure?.Invoke(options);
            });
            return builder;
        }

        /// <summary>
        /// Adds a provider of the proxy's own values: before each upload, the proxy asks it for a page and sends what it answers
        /// with what the proxy holds. A provider that throws is logged and skipped for that turn.
        /// </summary>
        /// <param name="provider">Answers a page for the configuration held, or <see langword="null"/> for nothing this turn.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="provider"/> is <see langword="null"/>.</exception>
        public TBuilder AddProvider(Func<ZabbixProxyConfig, CancellationToken, ValueTask<ZabbixProxyDataPage?>> provider)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(provider);
            builder.Services.Configure<ZabbixProxyOptions>(builder.Name, options => options.Providers.Add(provider));
            return builder;
        }

        /// <summary>
        /// Runs each task the server hands back, a remote command among them, with <paramref name="handler"/>, and uploads the
        /// result it returns. Without it, the proxy logs a task and leaves it unrun. A handler that throws is logged, and the
        /// task is reported as failed. A later <c>OnTask</c> replaces it.
        /// </summary>
        /// <param name="handler">Runs one task and returns its result: <c>(task, ct) =&gt; tasks.RunAsync(task, ct)</c>.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is <see langword="null"/>.</exception>
        public TBuilder OnTask(Func<ZabbixProxyTask, CancellationToken, ValueTask<ZabbixProxyTaskResult>> handler)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(handler);
            builder.Services.Configure<ZabbixProxyOptions>(builder.Name, options => options.TaskHandler = handler);
            return builder;
        }

        /// <summary>
        /// Sets the protocol version the proxy announces, which a server must share: it configures only a proxy of its own
        /// major.minor. A passive proxy must have it, because no server message says the server's version; an active proxy
        /// announces 7.4.0 without it, and adopts the version a refusing server names.
        /// </summary>
        /// <param name="version">The server's version: <c>new Version(7, 4)</c>. Its major, minor and build are Zabbix's major, minor and patch.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="version"/> is <see langword="null"/>.</exception>
        public TBuilder WithVersion(Version version)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(version);
            string text = ZabbixComponentVersion.FromVersion(version).ToString();
            builder.Services.Configure<ZabbixProxyOptions>(builder.Name, options => options.Version = text);
            return builder;
        }

        /// <summary>
        /// Sets the protocol version the proxy announces, in Zabbix's spelling: <c>"7.4"</c>, <c>"8.0.0"</c> or
        /// <c>"8.0.0rc1"</c>, read as the <see cref="Version"/> Zabbix reads it as (8.0.0).
        /// </summary>
        /// <param name="version">The server's version: <c>major.minor[.patch]</c>.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="version"/> isn't <c>major.minor[.patch]</c>.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="version"/> is <see langword="null"/>.</exception>
        public TBuilder WithVersion(string version)
        {
            ArgumentNullException.ThrowIfNull(version);
            if (!ZabbixComponentVersion.TryParse(version, CultureInfo.InvariantCulture, out ZabbixComponentVersion parsed))
                throw new ArgumentException($"\"{version}\" isn't a Zabbix version: major.minor[.patch], such as 7.4 or 8.0.0.", nameof(version));
            return builder.WithVersion(parsed.ToVersion());
        }

        /// <summary>Sets any of the proxy's options, in order with the other verbs.</summary>
        /// <param name="configure">Sets the options.</param>
        /// <returns>The builder.</returns>
        /// <example>
        /// <code language="csharp">
        /// builder.Configure(options => options.OfflineBuffer = TimeSpan.FromHours(24));
        /// </code>
        /// </example>
        public TBuilder Configure(Action<ZabbixProxyOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            builder.Services.Configure(builder.Name, configure);
            return builder;
        }

        /// <summary>
        /// Binds the proxy's options to a configuration section, in order with the other verbs: <c>Server</c>, <c>Version</c>,
        /// <c>OfflineBuffer</c>, the <c>Trapper</c> and <c>Connection</c> subsections and the rest, as
        /// <see cref="ZabbixProxyOptions"/> names them. One <c>Proxies:{name}</c> row serves both proxies. The section is read
        /// when the proxy is built.
        /// </summary>
        /// <param name="section">The proxy's section.</param>
        /// <returns>The builder.</returns>
        public TBuilder Bind(IConfiguration section)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(section);
            builder.Services.AddOptions<ZabbixProxyOptions>(builder.Name).Bind(section);
            return builder;
        }
    }
}
