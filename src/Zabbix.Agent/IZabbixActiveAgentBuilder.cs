// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0

namespace Community.Zabbix;

/// <summary>
/// Represents an active agent being set up, in the shape of <c>IHttpClientBuilder</c>: its name and the container it's
/// registered in, from <see cref="IZabbixItemsBuilder"/>. <see cref="ZabbixActiveAgentBuilderExtensions"/> holds its own
/// verbs, and the item verbs (<c>AddItem</c>, <c>AddItems</c>, <c>AddPlugins</c>) are the ones a passive agent has.
/// <see cref="ZabbixActiveAgent.CreateBuilder"/> returns one over a container of its own, and
/// <see cref="ZabbixActiveAgentServiceCollectionExtensions.AddZabbixActiveAgent"/> one over the application's.
/// </summary>
public interface IZabbixActiveAgentBuilder : IZabbixItemsBuilder
{
}
