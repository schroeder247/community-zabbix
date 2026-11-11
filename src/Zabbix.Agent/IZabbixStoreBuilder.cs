// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Storage;

namespace Community.Zabbix;

/// <summary>
/// Represents a builder of a component that keeps values in a store until its server has them: a push agent or a proxy.
/// <see cref="ZabbixStoreBuilderExtensions"/> holds the verbs that choose the store, <c>UseStore</c> and <c>UseMemoryStore</c>,
/// and <c>Community.Zabbix.Agent.Storage.EntityFrameworkCore</c> adds <c>UseEntityFrameworkStore</c>, so each verb is written
/// once for every such builder.
/// </summary>
/// <remarks>
/// It has no public members, as Azure's <c>IAzureClientBuilder</c> has none: the component's own builder,
/// <see cref="IZabbixPushAgentBuilder"/> or <c>IZabbixProxyBuilder</c>, has the name and the container, and says where the
/// store it chooses is kept. Only those builders implement it.
/// </remarks>
public interface IZabbixStoreBuilder
{
    /// <summary>Sets the store the component builds when it starts.</summary>
    /// <param name="create">Builds the store from the component's container, policy and services; <see langword="null"/> for the in-memory store.</param>
    internal void SetStore(Func<IServiceProvider, ZabbixStorePolicy<ZabbixValue>, ZabbixStoreServices, ZabbixStore<ZabbixValue>>? create);
}
