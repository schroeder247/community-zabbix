// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using Community.Zabbix.Storage;

namespace Community.Zabbix;

/// <summary>
/// Provides the verbs that choose where a push agent or a proxy keeps values until its server has them, the same on every
/// builder of one: the builders <c>CreateBuilder</c> returns and the ones <c>AddZabbixPushAgent</c>,
/// <c>AddZabbixActiveProxy</c> and <c>AddZabbixPassiveProxy</c> return. A later <c>Use…Store</c> replaces an earlier one.
/// </summary>
/// <remarks>
/// A push agent builds a store for each destination, and a proxy one for its history. The store's policy is the
/// component's: its <c>MaxItems</c> and <c>Overflow</c>, and a proxy's offline buffer and admission rule.
/// </remarks>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification = "The extension block is C# 14 syntax for extension members; the type the compiler emits for it is not one a caller names.")]
public static class ZabbixStoreBuilderExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IZabbixStoreBuilder
    {
        /// <summary>
        /// Uses a store other than the in-memory one, a durable one, which <paramref name="create"/> builds at start from the
        /// component's policy and services: once for each destination of a push agent, and once for a proxy's history. Each
        /// must get a store of its own. A later <c>Use…Store</c> replaces it.
        /// </summary>
        /// <param name="create">Builds one store: <c>(policy, services) =&gt; new MyStore(policy, services)</c>.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="create"/> is <see langword="null"/>.</exception>
        public TBuilder UseStore(Func<ZabbixStorePolicy<ZabbixValue>, ZabbixStoreServices, ZabbixStore<ZabbixValue>> create)
        {
            ArgumentNullException.ThrowIfNull(create);
            return builder.UseStore((_, policy, services) => create(policy, services));
        }

        /// <summary>
        /// Uses a store other than the in-memory one, which <paramref name="create"/> builds at start from the component's
        /// container, policy and services: once for each destination of a push agent, and once for a proxy's history. Each
        /// must get a store of its own. A later <c>Use…Store</c> replaces it.
        /// </summary>
        /// <param name="create">Builds one store: <c>(provider, policy, services) =&gt; …</c>.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="create"/> is <see langword="null"/>.</exception>
        public TBuilder UseStore(Func<IServiceProvider, ZabbixStorePolicy<ZabbixValue>, ZabbixStoreServices, ZabbixStore<ZabbixValue>> create)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(create);
            builder.SetStore(create);
            return builder;
        }

        /// <summary>
        /// Uses the in-memory store, which is the default: it replaces a store an earlier <c>Use…Store</c> chose. Its cap is
        /// the component's <c>MaxItems</c>.
        /// </summary>
        /// <returns>The builder.</returns>
        public TBuilder UseMemoryStore()
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.SetStore(null);
            return builder;
        }
    }
}
