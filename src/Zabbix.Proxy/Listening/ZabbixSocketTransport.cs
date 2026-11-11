// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Listening;

/// <summary>Kestrel's socket transport for a listener with no container: what <c>AddZabbixSocketTransport</c> registers, on the defaults.</summary>
internal static class ZabbixSocketTransport
{
    public static IConnectionListenerFactory Create(ILoggerFactory loggers) => new SocketTransportFactory(Options.Create(new SocketTransportOptions()), loggers);
}
