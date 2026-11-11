// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using Community.Zabbix.Hosting;
using Community.Zabbix.Listening;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix;

/// <summary>
/// Provides the verbs of a passive agent's builder, the same on the builder <see cref="ZabbixPassiveAgent.CreateBuilder"/>
/// returns and on the one <see cref="ZabbixPassiveAgentServiceCollectionExtensions.AddZabbixPassiveAgent"/> returns. Each verb
/// writes the agent's named options, which apply in the order of the calls, and returns the builder it was called on. The
/// item verbs, the ones an active agent has too, are <see cref="ZabbixItemBuilderExtensions"/>' and
/// <see cref="ZabbixItemsBuilderExtensions"/>', and the verbs that say who may poll it, the ones a proxy has too,
/// <see cref="ZabbixListenerBuilderExtensions"/>'.
/// </summary>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification = "The extension block is C# 14 syntax for extension members; the type the compiler emits for it is not one a caller names.")]
[SuppressMessage("Naming", "CA1708:Identifiers should differ by more than case",
    Justification = "The compiler names the types it emits for the extension blocks; a caller never names them.")]
public static class ZabbixPassiveAgentBuilderExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IZabbixPassiveAgentBuilder
    {
        /// <summary>
        /// Adds the agent's health check to the container's health checks, as <c>zabbix-passive-agent-{name}</c> with the
        /// <c>zabbix</c> and <c>agent</c> tags: healthy while it listens, degraded before it starts, and unhealthy when it
        /// stopped listening. A web host maps them with <c>MapHealthChecks</c>, and a worker publishes them through an
        /// <c>IHealthCheckPublisher</c>.
        /// </summary>
        /// <returns>The builder.</returns>
        public TBuilder AddHealthCheck()
        {
            ArgumentNullException.ThrowIfNull(builder);
            string name = builder.Name;
            ZabbixComponentHealthChecks.Add(builder.Services, "zabbix-passive-agent-" + name, provider =>
            {
                ZabbixPassiveAgent agent = provider.GetRequiredKeyedService<ZabbixPassiveAgent>(name);
                return new ZabbixListenerHealthCheck(agent.Name, "agent", () => agent.Listener);
            });
            return builder;
        }

        /// <summary>Sets any of the agent's options, in order with the other verbs.</summary>
        /// <param name="configure">Sets the options.</param>
        /// <returns>The builder.</returns>
        /// <example>
        /// <code language="csharp">
        /// builder.Configure(options => options.Port = 10070);
        /// </code>
        /// </example>
        public TBuilder Configure(Action<ZabbixPassiveAgentOptions> configure)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configure);
            builder.Services.Configure(builder.Name, configure);
            return builder;
        }

        /// <summary>
        /// Binds the agent's options to a configuration section, in order with the other verbs: <c>HostName</c>,
        /// <c>AllowedPeers</c>, <c>Address</c>, <c>Port</c>, <c>CheckTimeout</c> and the rest, as
        /// <see cref="ZabbixPassiveAgentOptions"/> names them. The section is read when the agent is built.
        /// </summary>
        /// <param name="section">The agent's section.</param>
        /// <returns>The builder.</returns>
        public TBuilder Bind(IConfiguration section)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(section);
            builder.Services.AddOptions<ZabbixPassiveAgentOptions>(builder.Name).Bind(section);
            return builder;
        }
    }

    extension(IZabbixPassiveAgentBuilder builder)
    {
        /// <summary>
        /// Adds a provider that the agent's container builds, with constructor injection, and disposes with the agent. Its keys
        /// are answered after the application's own <c>AddItem</c> keys and before the built-in ones. A key name is one
        /// provider's.
        /// </summary>
        /// <typeparam name="TProvider">The provider.</typeparam>
        /// <returns>The builder.</returns>
        public IZabbixPassiveAgentBuilder AddItems<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProvider>()
            where TProvider : ZabbixItemProvider
        {
            ArgumentNullException.ThrowIfNull(builder);
            ZabbixItemsBuilderExtensions.AddProvider<TProvider>(builder);
            return builder;
        }
    }

    extension(ZabbixPassiveAgentBuilder builder)
    {
        /// <summary>
        /// Adds a provider that the agent's container builds, with constructor injection, and disposes with the agent. Its keys
        /// are answered after the application's own <c>AddItem</c> keys and before the built-in ones. A key name is one
        /// provider's.
        /// </summary>
        /// <typeparam name="TProvider">The provider.</typeparam>
        /// <returns>The builder.</returns>
        public ZabbixPassiveAgentBuilder AddItems<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProvider>()
            where TProvider : ZabbixItemProvider
        {
            ArgumentNullException.ThrowIfNull(builder);
            ZabbixItemsBuilderExtensions.AddProvider<TProvider>(builder);
            return builder;
        }
    }
}
