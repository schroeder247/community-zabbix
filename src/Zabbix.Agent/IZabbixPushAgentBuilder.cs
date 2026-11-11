// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using Community.Zabbix.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix;

/// <summary>
/// Represents a push agent being set up, in the shape of <c>IHttpClientBuilder</c>: its name and the container it's
/// registered in. <see cref="ZabbixPushAgentBuilderExtensions"/> holds its verbs, which write the agent's named options to
/// <see cref="Services"/>, and <see cref="ZabbixStoreBuilderExtensions"/> the store verbs it shares with the proxies.
/// <see cref="ZabbixPushAgent.CreateBuilder"/> returns one over a container of its own, and
/// <see cref="ZabbixPushAgentServiceCollectionExtensions.AddZabbixPushAgent"/> one over the application's.
/// </summary>
public interface IZabbixPushAgentBuilder : IZabbixStoreBuilder
{
    /// <summary>The agent's name: the name of its options, the key it's registered under, and its host name unless set.</summary>
    string Name { get; }

    /// <summary>The container the agent is registered in.</summary>
    IServiceCollection Services { get; }

    // A push agent keeps its store in its named options, built at start once for each destination.
    [SuppressMessage("Design", "CA1033:Interface methods should be callable by child types",
        Justification = "The member is internal: the shared verbs call it to reach this builder's options, and a type that implements the interface has no use for it.")]
    void IZabbixStoreBuilder.SetStore(Func<IServiceProvider, ZabbixStorePolicy<ZabbixValue>, ZabbixStoreServices, ZabbixStore<ZabbixValue>>? create)
        => Services.Configure<ZabbixPushAgentOptions>(Name, options => options.Storage = create);
}
