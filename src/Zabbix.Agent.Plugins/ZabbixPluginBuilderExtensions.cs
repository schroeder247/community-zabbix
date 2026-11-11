// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using Community.Zabbix.Hosting;
using Microsoft.Extensions.Configuration;

namespace Community.Zabbix;

/// <summary>
/// Loads plugins into an agent that answers keys, the active or the passive one: each plugin listed by name, from a
/// folder its configuration names, in a load context of its own; its entry registers its item providers as a library's
/// registration does (docs/adr/0008-item-providers-and-plugins.md). The plugins load when this is called, while the
/// agent's registration runs, and a listed plugin that cannot be loaded throws <see cref="InvalidOperationException"/>,
/// so the agent does not start without it.
/// </summary>
/// <remarks>
/// A plugin runs in the agent's process, with its account. A handler that throws is answered as not supported, as any
/// handler is; a crash on a thread of the plugin's own ends the process. Nothing unloads: a plugin is updated by
/// restarting the process. On Linux and macOS a plugin whose files other accounts can write is refused.
/// </remarks>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification = "The extension block is C# 14 syntax for extension members; the type the compiler emits for it is not one a caller names.")]
public static class ZabbixPluginBuilderExtensions
{
    private const string Reason = "Plugins load assemblies at run time, which trimming and native AOT do not support.";

    extension<TBuilder>(TBuilder builder) where TBuilder : IZabbixItemsBuilder
    {
        /// <summary>
        /// Loads the plugins a configuration section lists: <c>Path</c> (the folder), <c>Load</c> (the names, an array
        /// or a comma-separated list) and, for each plugin, its settings in the subsection named after it.
        /// </summary>
        /// <param name="configuration">The section, conventionally the agent's own <c>Plugins</c> (<c>Zabbix:ActiveAgents:web-01:Plugins</c>).</param>
        /// <returns>The builder.</returns>
        /// <exception cref="InvalidOperationException">A listed plugin cannot be loaded, or its entry fails to register.</exception>
        /// <example>
        /// <code>
        /// "Plugins": {
        ///   "Path": "/opt/zabbix-dotnet/plugins",
        ///   "Load": [ "SqlServer" ],
        ///   "SqlServer": { "ConnectionString": "Server=db-01;…" }
        /// }
        /// </code>
        /// </example>
        [RequiresUnreferencedCode(Reason)]
        [RequiresDynamicCode(Reason)]
        public TBuilder AddPlugins(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(configuration);
            ZabbixPluginLoader.Load(builder, ZabbixPluginOptions.From(configuration));
            return builder;
        }

        /// <summary>Loads the plugins <paramref name="options"/> lists.</summary>
        /// <param name="options">The folder, the names, and where each plugin's settings are.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="InvalidOperationException">A listed plugin cannot be loaded, or its entry fails to register.</exception>
        [RequiresUnreferencedCode(Reason)]
        [RequiresDynamicCode(Reason)]
        public TBuilder AddPlugins(ZabbixPluginOptions options)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(options);
            ZabbixPluginLoader.Load(builder, options);
            return builder;
        }
    }
}
