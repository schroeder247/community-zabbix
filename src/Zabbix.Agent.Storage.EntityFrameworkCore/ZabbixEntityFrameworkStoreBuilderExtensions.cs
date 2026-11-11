// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;

namespace Community.Zabbix.Storage.EntityFrameworkCore;

/// <summary>
/// Provides <c>UseEntityFrameworkStore</c>, which gives a push agent or a proxy a durable store on EF Core, the same on every
/// builder of one: <see cref="ZabbixPushAgent.CreateBuilder"/>'s and <c>AddZabbixPushAgent</c>'s, and a proxy's
/// <c>CreateBuilder</c>'s, <c>AddZabbixActiveProxy</c>'s and <c>AddZabbixPassiveProxy</c>'s.
/// </summary>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification = "The extension block is C# 14 syntax for extension members; the type the compiler emits for it is not one a caller names.")]
public static class ZabbixEntityFrameworkStoreBuilderExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IZabbixStoreBuilder
    {
        /// <summary>
        /// Uses a durable store on the database <paramref name="configureDatabase"/> describes, with the component's policy: one
        /// <see cref="ZabbixEntityFrameworkStore{T}"/> for each destination of a push agent, and one for a proxy's history, each
        /// in a table named after it. A value stays in the table until the server has it, so a restart loses nothing. A later
        /// <c>Use…Store</c> replaces it.
        /// </summary>
        /// <param name="configureDatabase">EF Core's options builder: <c>db =&gt; db.UseSqlite("Data Source=agent.db")</c>.</param>
        /// <returns>The builder.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="configureDatabase"/> is <see langword="null"/>.</exception>
        public TBuilder UseEntityFrameworkStore(Action<DbContextOptionsBuilder> configureDatabase)
        {
            ArgumentNullException.ThrowIfNull(configureDatabase);
            return builder.UseStore((policy, services) => new ZabbixEntityFrameworkStore<ZabbixValue>(configureDatabase, policy, services));
        }
    }
}
