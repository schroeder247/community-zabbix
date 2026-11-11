// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0

namespace Community.Zabbix;

/// <summary>
/// Represents an active proxy being set up, in the shape of <c>IHttpClientBuilder</c>: its name and the container it's
/// registered in, from <see cref="IZabbixProxyBuilder"/>. <see cref="ZabbixActiveProxyBuilderExtensions"/> holds its own
/// verbs, and <see cref="ZabbixProxyBuilderExtensions"/> the ones a passive proxy has too.
/// <see cref="ZabbixActiveProxy.CreateBuilder"/> returns one over a container of its own, and
/// <see cref="ZabbixActiveProxyServiceCollectionExtensions.AddZabbixActiveProxy"/> one over the application's.
/// </summary>
public interface IZabbixActiveProxyBuilder : IZabbixProxyBuilder
{
}
