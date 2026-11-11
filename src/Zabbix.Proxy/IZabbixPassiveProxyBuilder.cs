// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0

namespace Community.Zabbix;

/// <summary>
/// Represents a passive proxy being set up, in the shape of <c>IHttpClientBuilder</c>: its name and the container it's
/// registered in, from <see cref="IZabbixProxyBuilder"/>. <see cref="ZabbixPassiveProxyBuilderExtensions"/> holds its own
/// verbs, and <see cref="ZabbixProxyBuilderExtensions"/> the ones an active proxy has too.
/// <see cref="ZabbixPassiveProxy.CreateBuilder"/> returns one over a container of its own, and
/// <see cref="ZabbixPassiveProxyServiceCollectionExtensions.AddZabbixPassiveProxy"/> one over the application's.
/// </summary>
public interface IZabbixPassiveProxyBuilder : IZabbixProxyBuilder
{
}
