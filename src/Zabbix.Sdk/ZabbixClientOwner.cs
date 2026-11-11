// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// A container's hold on an API client it builds and hands out. The client is shared: a caller's dispose leaves it running,
/// and disposing this, which the container does when it's disposed, releases it.
/// </summary>
/// <param name="client">The client the container built.</param>
internal sealed class ZabbixClientOwner(ZabbixClient client) : IAsyncDisposable, IDisposable
{
    /// <summary>The shared client.</summary>
    public ZabbixClient Client { get; } = client.Share();

    /// <summary>Releases the client, ending its login session first.</summary>
    /// <returns>A task that completes when the client is released.</returns>
    public ValueTask DisposeAsync() => Client.ReleaseAsync();

    /// <summary>Releases the client at once.</summary>
    public void Dispose() => Client.Release();
}
