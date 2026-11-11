// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using Community.Zabbix.Listening;
using Community.Zabbix.Storage;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>
/// Provides a push agent's trapper verb, the same on the builder <see cref="ZabbixPushAgent.CreateBuilder"/> returns and on
/// the one <c>AddZabbixPushAgent</c> returns. It lives in this package because the listener is this package's, and it takes
/// the options a proxy's <c>AddTrapper</c> takes, <see cref="ZabbixListenerOptions"/>.
/// </summary>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification = "The extension block is C# 14 syntax for extension members; the type the compiler emits for it is not one a caller names.")]
public static class ZabbixTrapperAgentBuilderExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IZabbixPushAgentBuilder
    {
        /// <summary>
        /// Adds a trapper on <paramref name="port"/> as a source: values that <c>zabbix_sender</c> or another agent sends flow
        /// into the agent's stores and on to its servers, a sender relay. The trapper starts after the agent and stops before
        /// it, and its intake holds as many values as each of the agent's stores, <see cref="ZabbixPushAgentOptions.MaxItems"/>.
        /// Calling it again for the same agent sets up the same trapper, the last word winning, and the listener is
        /// registered once. You must name its <see cref="ZabbixListenerOptions.AllowedPeers"/>. The options are read once, at
        /// start, so a reload doesn't move the port.
        /// </summary>
        /// <param name="port">The port; <c>0</c> lets the platform pick one.</param>
        /// <param name="configure">The rest of the trapper's options: its peers and its TLS.</param>
        /// <returns>The builder.</returns>
        public TBuilder AddTrapper(int port, Action<ZabbixListenerOptions>? configure = null)
        {
            ArgumentNullException.ThrowIfNull(builder);
            OptionsBuilder<ZabbixListenerOptions> options = builder.Services.AddOptions<ZabbixListenerOptions>(builder.Name).Configure(o => { o.Port = port; configure?.Invoke(o); });
            return builder.RegisterTrapper(options);
        }

        /// <summary>
        /// Adds a trapper bound to a configuration section, a push agent's document row's <c>Trapper</c> subsection, as
        /// <c>AddTrapper(port, configure)</c> adds one in code. The port is the trapper port, 10051, when the section names none.
        /// </summary>
        /// <param name="section">The trapper's section.</param>
        /// <returns>The builder.</returns>
        public TBuilder AddTrapper(IConfiguration section)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(section);
            OptionsBuilder<ZabbixListenerOptions> options = builder.Services.AddOptions<ZabbixListenerOptions>(builder.Name)
                .Configure(o => o.Port = ZabbixServerNode.DefaultPort)
                .Bind(section);
            return builder.RegisterTrapper(options);
        }

        private TBuilder RegisterTrapper(OptionsBuilder<ZabbixListenerOptions> options)
        {
            options.ValidateOnStart();
            builder.Services.AddZabbixSocketTransport();
            builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ZabbixListenerOptions>, ZabbixListenerOptionsValidator>());
            var key = new ZabbixTrapperKey(builder.Name);
            int before = builder.Services.Count;
            builder.Services.TryAddKeyedSingleton(key, static (provider, k) =>
            {
                string name = ((ZabbixTrapperKey)k!).Name;
                int maxItems = provider.GetRequiredService<IOptionsMonitor<ZabbixPushAgentOptions>>().Get(name).MaxItems ?? ZabbixStorePolicy<ZabbixValue>.DefaultMaxItems;
                return new ZabbixTrapperSource(provider.GetRequiredService<IOptionsMonitor<ZabbixListenerOptions>>().Get(name), maxItems,
                    provider.GetRequiredKeyedService<IConnectionListenerFactory>(ZabbixListener.TransportKey), provider.GetRequiredService<ILoggerFactory>(),
                    provider.GetRequiredService<TimeProvider>(), provider.GetService<IMeterFactory>());
            });
            if (builder.Services.Count == before) return builder;                            // this agent's trapper is registered: configured above, sourced once

            // Hosted services start in registration order and stop in reverse, and the agent was registered first: the
            // trapper therefore starts after the agent and stops before it, which is what a source wants: the agent is
            // pumping when the port opens, and the port closes (the source ends) before the agent drains. A builder's own
            // container has no host: the agent it builds starts and stops what the container hosts in the same order. The
            // intake is a source that ends by completion: the agent's stop doesn't cut it, so what the senders were told was
            // processed reaches the store.
            builder.Services.AddSingleton<IHostedService>(provider => provider.GetRequiredKeyedService<ZabbixTrapperSource>(key));
            return builder.AddSource(provider => provider.GetRequiredKeyedService<ZabbixTrapperSource>(key).TakeEdge());
        }
    }
}
