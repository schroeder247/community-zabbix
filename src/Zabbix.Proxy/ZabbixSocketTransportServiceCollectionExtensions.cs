// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Listening;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>Kestrel's socket transport, the connection layer a listener (a trapper, a passive agent, a passive proxy) needs.</summary>
public static class ZabbixSocketTransportServiceCollectionExtensions
{
    /// <summary>
    /// Kestrel's socket transport as the listeners' connection layer, keyed <see cref="ZabbixListener.TransportKey"/> with its
    /// own <see cref="SocketTransportOptions"/> named the same, unless the host registered another under that key. Keyed,
    /// so a web application's own <see cref="IConnectionListenerFactory"/> (Kestrel's, named pipes, QUIC) and its options
    /// stay the web server's, and the Zabbix listeners always bind sockets.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <returns>The container, for chaining.</returns>
    public static IServiceCollection AddZabbixSocketTransport(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddLogging();
        services.AddOptions<SocketTransportOptions>(ZabbixListener.TransportKey);
        services.TryAddKeyedSingleton<IConnectionListenerFactory>(ZabbixListener.TransportKey, static (provider, _) => new SocketTransportFactory(
            Options.Create(provider.GetRequiredService<IOptionsMonitor<SocketTransportOptions>>().Get(ZabbixListener.TransportKey)),
            provider.GetRequiredService<ILoggerFactory>()));
        return services;
    }
}
