// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Net;

namespace Community.Zabbix;

/// <summary>
/// Makes the interfaces a host's create and update hold, from the address the server reaches the host at and the port
/// Zabbix uses for the kind: <c>h.Interfaces = [ZabbixInterface.Agent(IPAddress.Parse("10.0.0.2"))]</c>.
/// </summary>
/// <remarks>
/// <para>
/// An interface reaches its host at an <see cref="IPAddress"/>, or at a DNS name the server resolves. A factory leaves
/// <see cref="ZabbixHostInterfaceRequest.Main"/> unset, and the host's list decides it: the first interface of a type is
/// the main one, unless another of that type is marked main, and Zabbix needs one main interface of each type a host has.
/// To mark one yourself, use <c>with</c>: <c>ZabbixInterface.Agent(address) with { Main = ZabbixInterfacePriority.Primary }</c>.
/// </para>
/// <para>
/// To add an interface to a host that exists, pass one to <c>zabbix.HostInterfaces.CreateAsync(hostId, ZabbixInterface.Snmp(address))</c>.
/// </para>
/// </remarks>
public static class ZabbixInterface
{
    // The port Zabbix fills in for each kind of interface.
    private const int AgentPort = 10050;
    private const int SnmpPort = 161;
    private const int IpmiPort = 623;
    private const int JmxPort = 12345;

    // The community Zabbix fills in for an SNMP interface: a user macro, set on the host, a template or globally.
    private const string SnmpCommunity = "{$SNMP_COMMUNITY}";

    /// <summary>Makes an interface to a Zabbix agent at <paramref name="address"/>, on port 10050.</summary>
    /// <param name="address">The address the server reaches the agent at.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    public static ZabbixHostInterfaceRequest Agent(IPAddress address) => Agent(address, AgentPort);

    /// <summary>Makes an interface to a Zabbix agent at <paramref name="address"/> and <paramref name="port"/>.</summary>
    /// <param name="address">The address the server reaches the agent at.</param>
    /// <param name="port">The agent's port.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> isn't from 0 to 65535.</exception>
    public static ZabbixHostInterfaceRequest Agent(IPAddress address, int port) => At(ZabbixInterfaceType.Agent, address, port);

    /// <summary>Makes an interface to a Zabbix agent at the host <paramref name="dnsName"/> names, on port 10050.</summary>
    /// <param name="dnsName">The DNS name the server resolves to reach the agent.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentException"><paramref name="dnsName"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="dnsName"/> is <see langword="null"/>.</exception>
    public static ZabbixHostInterfaceRequest Agent(string dnsName) => Agent(dnsName, AgentPort);

    /// <summary>Makes an interface to a Zabbix agent at the host <paramref name="dnsName"/> names, and <paramref name="port"/>.</summary>
    /// <param name="dnsName">The DNS name the server resolves to reach the agent.</param>
    /// <param name="port">The agent's port.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentException"><paramref name="dnsName"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="dnsName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> isn't from 0 to 65535.</exception>
    public static ZabbixHostInterfaceRequest Agent(string dnsName, int port) => At(ZabbixInterfaceType.Agent, dnsName, port);

    /// <summary>
    /// Makes an SNMPv2c interface at <paramref name="address"/>, on port 161, whose community is the
    /// <c>{$SNMP_COMMUNITY}</c> macro.
    /// </summary>
    /// <param name="address">The address the server polls.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    public static ZabbixHostInterfaceRequest Snmp(IPAddress address) => Snmp(address, SnmpPort);

    /// <summary>Makes an SNMPv2c interface at <paramref name="address"/> and <paramref name="port"/>, whose community is the <c>{$SNMP_COMMUNITY}</c> macro.</summary>
    /// <param name="address">The address the server polls.</param>
    /// <param name="port">The SNMP agent's port.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> isn't from 0 to 65535.</exception>
    public static ZabbixHostInterfaceRequest Snmp(IPAddress address, int port) => Snmp(address, port, SnmpCommunity);

    /// <summary>Makes an SNMPv2c interface at <paramref name="address"/> and <paramref name="port"/> that polls with <paramref name="community"/>.</summary>
    /// <param name="address">The address the server polls.</param>
    /// <param name="port">The SNMP agent's port.</param>
    /// <param name="community">The community, or a user macro that holds it.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentException"><paramref name="community"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> or <paramref name="community"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> isn't from 0 to 65535.</exception>
    public static ZabbixHostInterfaceRequest Snmp(IPAddress address, int port, string community) => At(ZabbixInterfaceType.Snmp, address, port) with { Details = V2c(community) };

    /// <summary>
    /// Makes an SNMP interface at <paramref name="address"/> and <paramref name="port"/> with the version and security
    /// <paramref name="details"/> set: an SNMPv3 interface's security name and level, for one. A version left unset is
    /// SNMPv2c, and an SNMPv1 or SNMPv2c community left unset is the <c>{$SNMP_COMMUNITY}</c> macro.
    /// </summary>
    /// <param name="address">The address the server polls.</param>
    /// <param name="port">The SNMP agent's port.</param>
    /// <param name="details">The SNMP version, community and security settings.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> or <paramref name="details"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> isn't from 0 to 65535.</exception>
    public static ZabbixHostInterfaceRequest Snmp(IPAddress address, int port, ZabbixHostInterfaceDetailRequest details) => At(ZabbixInterfaceType.Snmp, address, port) with { Details = Complete(details) };

    /// <summary>
    /// Makes an SNMPv2c interface at the host <paramref name="dnsName"/> names, on port 161, whose community is the
    /// <c>{$SNMP_COMMUNITY}</c> macro.
    /// </summary>
    /// <param name="dnsName">The DNS name the server resolves to poll the host.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentException"><paramref name="dnsName"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="dnsName"/> is <see langword="null"/>.</exception>
    public static ZabbixHostInterfaceRequest Snmp(string dnsName) => Snmp(dnsName, SnmpPort);

    /// <summary>Makes an SNMPv2c interface at the host <paramref name="dnsName"/> names, and <paramref name="port"/>, whose community is the <c>{$SNMP_COMMUNITY}</c> macro.</summary>
    /// <param name="dnsName">The DNS name the server resolves to poll the host.</param>
    /// <param name="port">The SNMP agent's port.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentException"><paramref name="dnsName"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="dnsName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> isn't from 0 to 65535.</exception>
    public static ZabbixHostInterfaceRequest Snmp(string dnsName, int port) => Snmp(dnsName, port, SnmpCommunity);

    /// <summary>Makes an SNMPv2c interface at the host <paramref name="dnsName"/> names, and <paramref name="port"/>, that polls with <paramref name="community"/>.</summary>
    /// <param name="dnsName">The DNS name the server resolves to poll the host.</param>
    /// <param name="port">The SNMP agent's port.</param>
    /// <param name="community">The community, or a user macro that holds it.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentException"><paramref name="dnsName"/> is empty or white space, or <paramref name="community"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="dnsName"/> or <paramref name="community"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> isn't from 0 to 65535.</exception>
    public static ZabbixHostInterfaceRequest Snmp(string dnsName, int port, string community) => At(ZabbixInterfaceType.Snmp, dnsName, port) with { Details = V2c(community) };

    /// <summary>
    /// Makes an SNMP interface at the host <paramref name="dnsName"/> names, and <paramref name="port"/>, with the version
    /// and security <paramref name="details"/> set. A version left unset is SNMPv2c, and an SNMPv1 or SNMPv2c community left
    /// unset is the <c>{$SNMP_COMMUNITY}</c> macro.
    /// </summary>
    /// <param name="dnsName">The DNS name the server resolves to poll the host.</param>
    /// <param name="port">The SNMP agent's port.</param>
    /// <param name="details">The SNMP version, community and security settings.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentException"><paramref name="dnsName"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="dnsName"/> or <paramref name="details"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> isn't from 0 to 65535.</exception>
    public static ZabbixHostInterfaceRequest Snmp(string dnsName, int port, ZabbixHostInterfaceDetailRequest details) => At(ZabbixInterfaceType.Snmp, dnsName, port) with { Details = Complete(details) };

    /// <summary>Makes an IPMI interface at <paramref name="address"/>, on port 623.</summary>
    /// <param name="address">The address of the host's management controller.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    public static ZabbixHostInterfaceRequest Ipmi(IPAddress address) => Ipmi(address, IpmiPort);

    /// <summary>Makes an IPMI interface at <paramref name="address"/> and <paramref name="port"/>.</summary>
    /// <param name="address">The address of the host's management controller.</param>
    /// <param name="port">The controller's port.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> isn't from 0 to 65535.</exception>
    public static ZabbixHostInterfaceRequest Ipmi(IPAddress address, int port) => At(ZabbixInterfaceType.Ipmi, address, port);

    /// <summary>Makes an IPMI interface at the controller <paramref name="dnsName"/> names, on port 623.</summary>
    /// <param name="dnsName">The DNS name of the host's management controller.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentException"><paramref name="dnsName"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="dnsName"/> is <see langword="null"/>.</exception>
    public static ZabbixHostInterfaceRequest Ipmi(string dnsName) => Ipmi(dnsName, IpmiPort);

    /// <summary>Makes an IPMI interface at the controller <paramref name="dnsName"/> names, and <paramref name="port"/>.</summary>
    /// <param name="dnsName">The DNS name of the host's management controller.</param>
    /// <param name="port">The controller's port.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentException"><paramref name="dnsName"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="dnsName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> isn't from 0 to 65535.</exception>
    public static ZabbixHostInterfaceRequest Ipmi(string dnsName, int port) => At(ZabbixInterfaceType.Ipmi, dnsName, port);

    /// <summary>Makes a JMX interface at <paramref name="address"/>, on port 12345.</summary>
    /// <param name="address">The address of the Java application's JMX endpoint.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    public static ZabbixHostInterfaceRequest Jmx(IPAddress address) => Jmx(address, JmxPort);

    /// <summary>Makes a JMX interface at <paramref name="address"/> and <paramref name="port"/>.</summary>
    /// <param name="address">The address of the Java application's JMX endpoint.</param>
    /// <param name="port">The JMX port.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="address"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> isn't from 0 to 65535.</exception>
    public static ZabbixHostInterfaceRequest Jmx(IPAddress address, int port) => At(ZabbixInterfaceType.Jmx, address, port);

    /// <summary>Makes a JMX interface at the host <paramref name="dnsName"/> names, on port 12345.</summary>
    /// <param name="dnsName">The DNS name of the Java application's host.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentException"><paramref name="dnsName"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="dnsName"/> is <see langword="null"/>.</exception>
    public static ZabbixHostInterfaceRequest Jmx(string dnsName) => Jmx(dnsName, JmxPort);

    /// <summary>Makes a JMX interface at the host <paramref name="dnsName"/> names, and <paramref name="port"/>.</summary>
    /// <param name="dnsName">The DNS name of the Java application's host.</param>
    /// <param name="port">The JMX port.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="ArgumentException"><paramref name="dnsName"/> is empty or white space.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="dnsName"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="port"/> isn't from 0 to 65535.</exception>
    public static ZabbixHostInterfaceRequest Jmx(string dnsName, int port) => At(ZabbixInterfaceType.Jmx, dnsName, port);

    /// <summary>
    /// A host's interfaces with each that leaves <see cref="ZabbixHostInterfaceRequest.Main"/> unset decided: the first of
    /// its type is the main one, unless another of that type is marked main, and the rest aren't. A list with nothing to
    /// decide is kept as it is, and an interface marked either way keeps its mark.
    /// </summary>
    /// <param name="interfaces">The interfaces a host's create or update holds.</param>
    /// <returns>The interfaces, each of a type marked main or not.</returns>
    internal static IReadOnlyList<ZabbixHostInterfaceRequest>? MarkMain(IReadOnlyList<ZabbixHostInterfaceRequest>? interfaces)
    {
        if (interfaces is null || !interfaces.Any(candidate => candidate is { Main: null, Type: not null })) return interfaces;
        var main = new HashSet<ZabbixInterfaceType>();
        foreach (ZabbixHostInterfaceRequest candidate in interfaces)
            if (candidate is { Main: ZabbixInterfacePriority.Primary, Type: { } type }) main.Add(type);
        var marked = new ZabbixHostInterfaceRequest[interfaces.Count];
        for (int i = 0; i < marked.Length; i++)
        {
            marked[i] = interfaces[i] is { Main: null, Type: { } type } unset
                ? unset with { Main = main.Add(type) ? ZabbixInterfacePriority.Primary : ZabbixInterfacePriority.Secondary }
                : interfaces[i];
        }
        return Array.AsReadOnly(marked);
    }

    private static ZabbixHostInterfaceRequest At(ZabbixInterfaceType type, IPAddress address, int port)
    {
        ArgumentNullException.ThrowIfNull(address);
        return new() { Type = type, UseIp = ZabbixHostInterfaceUseIp.Ip, Ip = address.ToString(), Dns = string.Empty, Port = Port(port) };
    }

    private static ZabbixHostInterfaceRequest At(ZabbixInterfaceType type, string dnsName, int port)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dnsName);
        return new() { Type = type, UseIp = ZabbixHostInterfaceUseIp.Dns, Ip = string.Empty, Dns = dnsName, Port = Port(port) };
    }

    // The port as the API takes it, which also takes a user macro.
    private static string Port(int port)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, IPEndPoint.MaxPort);
        return port.ToString(CultureInfo.InvariantCulture);
    }

    private static ZabbixHostInterfaceDetailRequest V2c(string community)
    {
        ArgumentException.ThrowIfNullOrEmpty(community);
        return new() { Version = ZabbixSnmpVersion.V2c, Community = community };
    }

    // Zabbix requires an SNMP interface's version, and an SNMPv1 or SNMPv2c interface's community.
    private static ZabbixHostInterfaceDetailRequest Complete(ZabbixHostInterfaceDetailRequest details)
    {
        ArgumentNullException.ThrowIfNull(details);
        ZabbixSnmpVersion version = details.Version ?? ZabbixSnmpVersion.V2c;
        return details with { Version = version, Community = version == ZabbixSnmpVersion.V3 ? details.Community : details.Community ?? SnmpCommunity };
    }
}
