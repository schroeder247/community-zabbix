// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix;

/// <summary>
/// Represents a named client under construction: the <c>(Name, Services)</c> pair, in the shape of
/// <c>IHttpClientBuilder</c>. Its verbs (<see cref="ZabbixBuilderExtensions"/>) write the client's named registrations;
/// the container holds the state.
/// </summary>
public interface IZabbixBuilder
{
    /// <summary>
    /// The server's name: the options name and the keyed-service key, under which a document binds it from its
    /// <c>Servers:{name}</c> row.
    /// </summary>
    string Name { get; }

    /// <summary>The container.</summary>
    IServiceCollection Services { get; }
}
