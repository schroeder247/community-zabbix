// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Net;

namespace Community.Zabbix.Hosting;

/// <summary>
/// Server text written by the builders' verbs: an agent's <c>AddServer</c> adds one destination, <c>,</c>-joined after the
/// ones before it, and a <c>WithServer</c> sets the one server of a wire client or an active proxy. A destination's HA
/// nodes are <c>;</c>-joined. The options' validator parses the whole at start.
/// </summary>
internal static class ZabbixServerActiveText
{
    /// <summary>The text with <paramref name="destination"/> after the destinations it names.</summary>
    /// <param name="serverActive">The options' text.</param>
    /// <param name="destination">One destination's text.</param>
    public static string Add(string? serverActive, string destination)
        => string.IsNullOrWhiteSpace(serverActive) ? destination : $"{serverActive},{destination}";

    /// <summary>One destination in Zabbix's spelling, for an agent's <c>AddServer</c>: a node, or the HA nodes of one cluster, <c>;</c>-joined.</summary>
    /// <param name="server">The text: a node, or the nodes of one cluster joined by <c>;</c>.</param>
    /// <param name="parameter">The caller's parameter, which an exception names.</param>
    /// <returns>The destination's nodes, each with its port.</returns>
    /// <exception cref="ArgumentException">The text isn't one destination.</exception>
    public static string Destination(string server, string parameter)
        => One(server, parameter, "and each AddServer adds one: call it once for each");

    /// <summary>
    /// The one server of a wire client or an active proxy, in Zabbix's spelling, for <c>WithServer</c>: a node, or the HA
    /// nodes of one cluster, <c>;</c>-joined.
    /// </summary>
    /// <param name="server">The text: a node, or the nodes of one cluster joined by <c>;</c>.</param>
    /// <param name="parameter">The caller's parameter, which an exception names.</param>
    /// <returns>The server's nodes, each with its port.</returns>
    /// <exception cref="ArgumentException">The text isn't one server.</exception>
    public static string Server(string server, string parameter)
        => One(server, parameter, "and this talks to one server or proxy: you must join the nodes of an HA cluster with ';'");

    /// <summary>One destination of <paramref name="nodes"/>, in failover order.</summary>
    /// <param name="nodes">The nodes; two or more are one HA cluster.</param>
    /// <param name="parameter">The caller's parameter, which an exception names.</param>
    /// <returns>The destination's nodes, each with its port.</returns>
    /// <exception cref="ArgumentException">No node, a node named twice, or a node on port 0.</exception>
    public static string Destination(ReadOnlySpan<DnsEndPoint> nodes, string parameter)
    {
        if (nodes.IsEmpty) throw new ArgumentException("A server needs at least one node.", parameter);
        var cluster = new ZabbixServerNode[nodes.Length];
        try
        {
            for (int i = 0; i < nodes.Length; i++)
            {
                ArgumentNullException.ThrowIfNull(nodes[i], parameter);
                cluster[i] = nodes[i];
            }
            return ZabbixServerActive.Of(cluster).ToString();
        }
        catch (ArgumentException refused) when (refused is not ArgumentNullException)
        {
            throw new ArgumentException(refused.Message, parameter, refused);
        }
    }

    private static string One(string server, string parameter, string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(server, parameter);
        if (!ZabbixServerActive.TryParse(server, CultureInfo.InvariantCulture, out ZabbixServerActive? parsed, out string? error))
            throw new ArgumentException(error, parameter);
        if (parsed.Count != 1)
            throw new ArgumentException($"\"{server}\" names {parsed.Count} destinations, {why}.", parameter);
        return parsed.ToString();
    }
}
