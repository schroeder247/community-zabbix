// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Returns a named client: the <c>IHttpClientFactory</c> shape, over the keyed services
/// <see cref="ZabbixClientServiceCollectionExtensions.AddZabbixClient(Microsoft.Extensions.DependencyInjection.IServiceCollection, string)"/>
/// registers. The container shares each client it returns: it owns the client and disposes it when it's disposed, and a
/// caller's <see cref="ZabbixClient.Dispose()"/> or <see cref="ZabbixClient.DisposeAsync"/> leaves it running.
/// </summary>
public interface IZabbixClientFactory
{
    /// <summary>The client registered as <paramref name="name"/>.</summary>
    /// <param name="name">The name given to <c>AddZabbixClient</c>.</param>
    /// <returns>The client registered under the name, built once and shared: disposing it leaves it running.</returns>
    /// <exception cref="KeyNotFoundException">No client is registered under <paramref name="name"/>.</exception>
    ZabbixClient Create(string name);

    /// <summary>The client registered as <paramref name="name"/>, if there is one.</summary>
    /// <param name="name">The name given to <c>AddZabbixClient</c>.</param>
    /// <param name="client">The client, when registered: shared, so disposing it leaves it running.</param>
    /// <returns>Whether a client is registered under <paramref name="name"/>.</returns>
    bool TryCreate(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ZabbixClient? client);
}
