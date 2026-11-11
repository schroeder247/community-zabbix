// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>Mints a named client — the <c>IHttpClientFactory</c> shape; keyed DI is the veneer over it.</summary>
public interface IZabbixClientFactory
{
    /// <summary>The client registered as <paramref name="name"/>.</summary>
    /// <param name="name">The name given to <c>AddZabbix</c>.</param>
    /// <returns>The client registered under the name, built once and shared.</returns>
    /// <exception cref="KeyNotFoundException">No client is registered under <paramref name="name"/>.</exception>
    ZabbixClient Create(string name);

    /// <summary>The client registered as <paramref name="name"/>, if there is one.</summary>
    /// <param name="name">The name given to <c>AddZabbix</c>.</param>
    /// <param name="client">The client, when registered.</param>
    /// <returns>Whether a client is registered under <paramref name="name"/>.</returns>
    bool TryCreate(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ZabbixClient? client);
}
