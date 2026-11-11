// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;

namespace Community.Zabbix;

/// <summary>
/// Reads and writes a <see cref="DnsEndPoint"/> in Zabbix's own spelling (<c>zbx-server</c>, <c>zbx-server:10051</c>,
/// <c>10.0.0.1</c>, <c>fe80::1</c>, <c>[::1]:10051</c>), which <see cref="DnsEndPoint"/> has no parser for.
/// </summary>
public static class ZabbixEndPoint
{
    /// <summary>Reads <paramref name="text"/>, on the trapper port, 10051, where it names no port.</summary>
    /// <param name="text">A host, <c>host:port</c>, an IPv6 address, or <c>[ipv6]:port</c>.</param>
    /// <returns>The endpoint.</returns>
    /// <exception cref="ArgumentException"><paramref name="text"/> is empty or isn't an endpoint.</exception>
    public static DnsEndPoint Parse(string text) => Parse(text, ZabbixWireClient.DefaultServerPort);

    /// <summary>Reads <paramref name="text"/>, on <paramref name="defaultPort"/> where it names no port.</summary>
    /// <param name="text">A host, <c>host:port</c>, an IPv6 address, or <c>[ipv6]:port</c>.</param>
    /// <param name="defaultPort">The port when <paramref name="text"/> names none.</param>
    /// <returns>The endpoint.</returns>
    /// <exception cref="ArgumentException"><paramref name="text"/> is empty or isn't an endpoint.</exception>
    public static DnsEndPoint Parse(string text, int defaultPort) => ZabbixServerNode.Parse(text, defaultPort, nameof(text));

    /// <summary>Tries to read <paramref name="text"/>, on <paramref name="defaultPort"/> where it names no port.</summary>
    /// <param name="text">A host, <c>host:port</c>, an IPv6 address, or <c>[ipv6]:port</c>.</param>
    /// <param name="defaultPort">The port when <paramref name="text"/> names none.</param>
    /// <param name="endPoint">The endpoint, when the text is one.</param>
    /// <returns>Whether the text was an endpoint.</returns>
    public static bool TryParse([NotNullWhen(true)] string? text, int defaultPort, [NotNullWhen(true)] out DnsEndPoint? endPoint)
    {
        if (ZabbixServerNode.TryParse(text.AsSpan(), defaultPort, out ZabbixServerNode node))
        {
            endPoint = node;
            return true;
        }
        endPoint = null;
        return false;
    }

    /// <summary>
    /// Reads Zabbix's <c>ServerActive</c> text: <c>,</c> separates destinations, each of which gets every value, and <c>;</c>
    /// separates the HA nodes of one destination, tried in order.
    /// </summary>
    /// <param name="serverActive">The text, for example <c>zbx-1;zbx-2,zbx-3:10052</c>.</param>
    /// <returns>The destinations, each with its nodes in failover order.</returns>
    /// <exception cref="FormatException"><paramref name="serverActive"/> isn't a <c>ServerActive</c> value.</exception>
    public static IReadOnlyList<IReadOnlyList<DnsEndPoint>> ParseServerActive(string serverActive)
    {
        ArgumentNullException.ThrowIfNull(serverActive);
        return [.. ZabbixServerActive.Parse(serverActive, CultureInfo.InvariantCulture).Select(static destination => (IReadOnlyList<DnsEndPoint>)[.. destination.Select(static node => (DnsEndPoint)node)])];
    }

    /// <summary>Writes <paramref name="endPoint"/> in Zabbix's spelling: <c>zbx-server:10051</c>, or <c>[::1]:10051</c> for an IPv6 address.</summary>
    /// <param name="endPoint">The endpoint to write.</param>
    /// <returns>The text.</returns>
    public static string Format(DnsEndPoint endPoint)
    {
        ArgumentNullException.ThrowIfNull(endPoint);
        return ((ZabbixServerNode)endPoint).ToString();
    }
}
