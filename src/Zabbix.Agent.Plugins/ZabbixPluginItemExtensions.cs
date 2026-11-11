// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using Community.Zabbix.Hosting;
using Microsoft.Extensions.Configuration;

namespace Community.Zabbix;

/// <summary>
/// Loads plugins into a running agent's keys, <c>agent.Items.AddPlugins(…)</c>, as a builder's <c>AddPlugins</c> loads them
/// into the agent it builds.
/// </summary>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification = "The extension block is C# 14 syntax for extension members; the type the compiler emits for it is not one a caller names.")]
public static class ZabbixPluginItemExtensions
{
    private const string Reason = "Plugins load assemblies at run time, which trimming and native AOT do not support.";

    extension<TRegistry>(TRegistry items) where TRegistry : IZabbixItemRegistry
    {
        /// <summary>Loads the plugins <paramref name="options"/> lists. What they build is released with the agent.</summary>
        /// <param name="options">The folder, the names, and where each plugin's settings are.</param>
        /// <returns>The registry.</returns>
        /// <exception cref="InvalidOperationException">A listed plugin can't be loaded, or its entry fails to register.</exception>
        [RequiresUnreferencedCode(Reason)]
        [RequiresDynamicCode(Reason)]
        public TRegistry AddPlugins(ZabbixPluginOptions options)
        {
            ArgumentNullException.ThrowIfNull(items);
            ArgumentNullException.ThrowIfNull(options);
            items.Register(builder => ZabbixPluginLoader.Load(builder, options));
            return items;
        }

        /// <summary>
        /// Loads the plugins a configuration section lists: <c>Path</c> (the folder), <c>Load</c> (the names, an array or a
        /// comma-separated list) and, for each plugin, its settings in the subsection named after it. What they build is
        /// released with the agent.
        /// </summary>
        /// <param name="configuration">The section, conventionally the agent's own <c>Plugins</c>.</param>
        /// <returns>The registry.</returns>
        /// <exception cref="InvalidOperationException">A listed plugin can't be loaded, or its entry fails to register.</exception>
        [RequiresUnreferencedCode(Reason)]
        [RequiresDynamicCode(Reason)]
        public TRegistry AddPlugins(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(configuration);
            return items.AddPlugins(ZabbixPluginOptions.From(configuration));
        }
    }
}
