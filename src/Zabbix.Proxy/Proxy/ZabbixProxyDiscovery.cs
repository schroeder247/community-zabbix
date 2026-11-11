// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Threading.Channels;

namespace Community.Zabbix.Proxy;

/// <summary>The typed write of network discovery results to a proxy's discovery store.</summary>
public static class ZabbixProxyDiscovery
{
    /// <summary>
    /// Writes <paramref name="host"/> as the rows the server reads (<see cref="ZabbixProxyDiscoveredHost.ToRows"/>): its
    /// services, then the row that closes it. The one call for a discovery result; the raw rows are for a caller that
    /// already speaks the row protocol.
    /// </summary>
    /// <param name="writer">The discovery store (<see cref="ZabbixActiveProxy.DiscoveryStore"/>, <see cref="ZabbixPassiveProxy.DiscoveryStore"/>) or any writer of rows.</param>
    /// <param name="host">The host found.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>When every row is written.</returns>
    public static ValueTask WriteAsync(this ChannelWriter<ZabbixProxyDiscoveryItem> writer, ZabbixProxyDiscoveredHost host, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(host);
        return WriteCoreAsync(writer, host, cancellationToken);
    }

    private static async ValueTask WriteCoreAsync(ChannelWriter<ZabbixProxyDiscoveryItem> writer, ZabbixProxyDiscoveredHost host, CancellationToken cancellationToken)
    {
        foreach (ZabbixProxyDiscoveryItem row in host.ToRows())
            await writer.WriteAsync(row, cancellationToken).ConfigureAwait(false);
    }
}
