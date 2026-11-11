// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix;

/// <summary>
/// Provides the verbs that add <see cref="ZabbixItemProvider"/>s to an agent that answers keys, the active or the passive
/// one, on any of their builders. A provider given as an instance or a factory keeps the chain's own builder type.
/// </summary>
/// <remarks>
/// C# infers an extension's builder type only when none of the verb's type arguments is written, so the form that names its
/// provider's type, <c>AddItems&lt;TProvider&gt;()</c>, is on each builder: here for a library's registration over
/// <see cref="IZabbixItemsBuilder"/>, and beside each agent's builder for an application's chain.
/// </remarks>
[SuppressMessage("Design", "CA1034:Nested types should not be visible",
    Justification = "The extension block is C# 14 syntax for extension members; the type the compiler emits for it is not one a caller names.")]
public static class ZabbixItemsBuilderExtensions
{
    extension<TBuilder>(TBuilder builder) where TBuilder : IZabbixItemsBuilder
    {
        /// <summary>Adds a provider the caller built. The caller keeps owning it.</summary>
        /// <param name="provider">The provider.</param>
        /// <returns>The builder.</returns>
        public TBuilder AddItems(ZabbixItemProvider provider)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(provider);
            builder.Services.AddKeyedSingleton(new ZabbixItemsKey(builder.Name), provider);
            return builder;
        }

        /// <summary>Adds a provider that <paramref name="factory"/> builds from the agent's container. The container disposes it with the agent.</summary>
        /// <param name="factory">Builds the provider, once.</param>
        /// <returns>The builder.</returns>
        public TBuilder AddItems(Func<IServiceProvider, ZabbixItemProvider> factory)
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(factory);
            builder.Services.AddKeyedSingleton(new ZabbixItemsKey(builder.Name), (services, _) => factory(services));
            return builder;
        }
    }

    extension(IZabbixItemsBuilder builder)
    {
        /// <summary>
        /// Adds a provider that the agent's container builds, with constructor injection, and disposes with the agent. Its keys
        /// are answered after the application's own <c>AddItem</c> keys and before the built-in ones. A key name is one
        /// provider's.
        /// </summary>
        /// <typeparam name="TProvider">The provider.</typeparam>
        /// <returns>The builder.</returns>
        public IZabbixItemsBuilder AddItems<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProvider>()
            where TProvider : ZabbixItemProvider
        {
            ArgumentNullException.ThrowIfNull(builder);
            AddProvider<TProvider>(builder);
            return builder;
        }
    }

    /// <summary>Registers <typeparamref name="TProvider"/> as one of the named agent's providers: the one body of every builder's <c>AddItems&lt;TProvider&gt;()</c>.</summary>
    /// <typeparam name="TProvider">The provider.</typeparam>
    /// <param name="builder">The agent's builder.</param>
    internal static void AddProvider<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors)] TProvider>(IZabbixItemsBuilder builder)
        where TProvider : ZabbixItemProvider
        => builder.Services.AddKeyedSingleton<ZabbixItemProvider, TProvider>(new ZabbixItemsKey(builder.Name));
}
