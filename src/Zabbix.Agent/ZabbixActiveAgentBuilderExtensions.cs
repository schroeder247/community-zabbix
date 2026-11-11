// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Net;
using Community.Zabbix.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>
/// Provides the verbs of an active agent's builder, the same on the builder <see cref="ZabbixActiveAgent.CreateBuilder"/>
/// returns and on the one <see cref="ZabbixActiveAgentServiceCollectionExtensions.AddZabbixActiveAgent"/> returns. Each verb
/// writes the agent's named options, which apply in the order of the calls, and returns the builder it was called on. The
/// item verbs, the ones a passive agent has too, are <see cref="ZabbixItemBuilderExtensions"/>' and
/// <see cref="ZabbixItemsBuilderExtensions"/>'.
/// </summary>
/// <remarks>
/// The options keep the text that configuration spells, a server as <c>host:port</c>, so <see cref="Bind"/> can fill them.
/// The verbs take the .NET types and write that text.
/// </remarks>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification = "The extension block is C# 14 syntax for extension members; the type the compiler emits for it is not one a caller names.")]
[SuppressMessage("Naming", "CA1708:Identifiers should differ by more than case",
    Justification = "The compiler names the types it emits for the extension blocks; a caller never names them.")]
public static class ZabbixActiveAgentBuilderExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IZabbixActiveAgentBuilder
    {
        /// <summary>
        /// Adds a destination, a server or proxy whose active checks the agent answers, in Zabbix's spelling. Each call adds
        /// one destination, with its own item list, session, buffer and heartbeat. The nodes of one HA cluster are one
        /// destination, joined by <c>;</c>: <c>"zbx-1;zbx-2"</c>.
        /// </summary>
        /// <param name="server">
        /// The destination: <c>host</c> or <c>host:port</c>, on port 10051 when it names none, or the nodes of one HA cluster,
        /// tried in order. An IPv6 address with a port is bracketed: <c>[::1]:10051</c>.
        /// </param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException"><paramref name="server"/> is empty, isn't a node, names a node twice, or names more than one destination (<c>,</c>).</exception>
        /// <exception cref="ArgumentNullException"><paramref name="server"/> is <see langword="null"/>.</exception>
        public TBuilder AddServer(string server)
        {
            ArgumentNullException.ThrowIfNull(builder);
            string destination = ZabbixServerActiveText.Destination(server, nameof(server));
            builder.Services.Configure<ZabbixActiveAgentOptions>(builder.Name, options => options.ServerActive = ZabbixServerActiveText.Add(options.ServerActive, destination));
            return builder;
        }

        /// <summary>
        /// Adds a destination whose active checks the agent answers: one server or proxy, or the nodes of one HA cluster, tried
        /// in order. Each call adds one destination, with its own item list, session, buffer and heartbeat.
        /// </summary>
        /// <param name="nodes">The destination's nodes, in failover order.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentException">No node, a node named twice, or a node that isn't a host and port.</exception>
        public TBuilder AddServer(params ReadOnlySpan<DnsEndPoint> nodes)
        {
            ArgumentNullException.ThrowIfNull(builder);
            string destination = ZabbixServerActiveText.Destination(nodes, nameof(nodes));
            builder.Services.Configure<ZabbixActiveAgentOptions>(builder.Name, options => options.ServerActive = ZabbixServerActiveText.Add(options.ServerActive, destination));
            return builder;
        }

        /// <summary>
        /// Adds the agent's health check to the container's health checks, as <c>zabbix-active-agent-{name}</c> with the
        /// <c>zabbix</c> and <c>agent</c> tags. A web host maps them with <c>MapHealthChecks</c>, and a worker publishes them
        /// through an <c>IHealthCheckPublisher</c>. With several destinations, the check reports the worst.
        /// </summary>
        /// <returns>The builder.</returns>
        public TBuilder AddHealthCheck() => builder.AddHealthCheck(static _ => { });

        /// <summary>Adds the agent's health check, as <c>AddHealthCheck()</c> does, with its thresholds set.</summary>
        /// <param name="configure">Sets the thresholds.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="OptionsValidationException">The thresholds don't validate.</exception>
        public TBuilder AddHealthCheck(Action<ZabbixComponentHealthCheckOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            string name = builder.Name;
            ZabbixComponentHealthChecks.Add(builder.Services, "zabbix-active-agent-" + name, provider => provider.GetRequiredKeyedService<ZabbixActiveAgent>(name).Statuses, configure);
            return builder;
        }

        /// <summary>Sets any of the agent's options, in order with the other verbs.</summary>
        /// <param name="configure">Sets the options.</param>
        /// <returns>The builder.</returns>
        /// <example>
        /// <code language="csharp">
        /// builder.Configure(options => options.HostMetadata = "Linux web");
        /// </code>
        /// </example>
        public TBuilder Configure(Action<ZabbixActiveAgentOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            builder.Services.Configure(builder.Name, configure);
            return builder;
        }

        /// <summary>
        /// Binds the agent's options to a configuration section, in order with the other verbs: <c>ServerActive</c>,
        /// <c>HostName</c>, <c>HostMetadata</c>, <c>RefreshInterval</c> and the rest, as <see cref="ZabbixActiveAgentOptions"/>
        /// names them. The section is read when the agent is built.
        /// </summary>
        /// <param name="section">The agent's section.</param>
        /// <returns>The builder.</returns>
        public TBuilder Bind(IConfiguration section)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(section);
            builder.Services.AddOptions<ZabbixActiveAgentOptions>(builder.Name).Bind(section);
            return builder;
        }
    }

    extension(IZabbixActiveAgentBuilder builder)
    {
        /// <summary>
        /// Adds a provider that the agent's container builds, with constructor injection, and disposes with the agent. Its keys
        /// are answered after the application's own <c>AddItem</c> keys and before the built-in ones. A key name is one
        /// provider's.
        /// </summary>
        /// <typeparam name="TProvider">The provider.</typeparam>
        /// <returns>The builder.</returns>
        public IZabbixActiveAgentBuilder AddItems<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProvider>()
            where TProvider : ZabbixItemProvider
        {
            ArgumentNullException.ThrowIfNull(builder);
            ZabbixItemsBuilderExtensions.AddProvider<TProvider>(builder);
            return builder;
        }
    }

    extension(ZabbixActiveAgentBuilder builder)
    {
        /// <summary>
        /// Adds a provider that the agent's container builds, with constructor injection, and disposes with the agent. Its keys
        /// are answered after the application's own <c>AddItem</c> keys and before the built-in ones. A key name is one
        /// provider's.
        /// </summary>
        /// <typeparam name="TProvider">The provider.</typeparam>
        /// <returns>The builder.</returns>
        public ZabbixActiveAgentBuilder AddItems<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProvider>()
            where TProvider : ZabbixItemProvider
        {
            ArgumentNullException.ThrowIfNull(builder);
            ZabbixItemsBuilderExtensions.AddProvider<TProvider>(builder);
            return builder;
        }
    }
}
