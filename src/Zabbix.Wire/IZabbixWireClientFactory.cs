// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;

namespace Community.Zabbix;

/// <summary>
/// Returns a wire client registered by name, in the shape of <c>IHttpClientFactory</c>; keyed services are the veneer over it.
/// </summary>
/// <remarks>
/// A name's client is built once and shared, and the container owns it: a caller's <c>Dispose</c> or <c>DisposeAsync</c>
/// leaves it running for everyone else who has it, and the container disposes it when it's disposed, as disposing an
/// <c>HttpClient</c> from <c>IHttpClientFactory</c> leaves its handler. You don't need to dispose it.
/// </remarks>
public interface IZabbixWireClientFactory
{
    /// <summary>The client registered as <paramref name="name"/>.</summary>
    /// <param name="name">The name given to <c>AddZabbixWireClient</c>.</param>
    /// <returns>The client registered under the name, built once and shared; the container disposes it.</returns>
    /// <exception cref="KeyNotFoundException">No client is registered under <paramref name="name"/>.</exception>
    ZabbixWireClient Create(string name);

    /// <summary>The client registered as <paramref name="name"/>, if there's one.</summary>
    /// <param name="name">The name given to <c>AddZabbixWireClient</c>.</param>
    /// <param name="client">The client, when one is registered.</param>
    /// <returns>Whether a client is registered under <paramref name="name"/>.</returns>
    bool TryCreate(string name, [NotNullWhen(true)] out ZabbixWireClient? client);
}
