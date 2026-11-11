// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix;

/// <summary>
/// Represents an API client being set up, in the shape of <c>IHttpClientBuilder</c>: its name and the container it's
/// registered in. <see cref="ZabbixBuilderExtensions"/> holds its verbs, which write the client's named registrations to
/// <see cref="Services"/>. <see cref="ZabbixClient.CreateBuilder"/> returns one over a container of its own, and
/// <see cref="ZabbixServiceCollectionExtensions.AddZabbix(IServiceCollection, string)"/> one over the application's.
/// </summary>
public interface IZabbixBuilder
{
    /// <summary>
    /// The client's name: the name of its options, and the key it's registered under, which a configuration document binds
    /// from its <c>Servers:{name}</c> row.
    /// </summary>
    string Name { get; }

    /// <summary>The container the client is registered in.</summary>
    IServiceCollection Services { get; }
}
