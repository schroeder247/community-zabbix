// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Management.Automation;
using System.Net;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// A <c>-Server</c>, <c>-Trapper</c> or <c>-Agent</c> parameter: text as Zabbix spells a node (<c>web-01</c>,
/// <c>web-01:10050</c>, <c>10.0.0.1</c>, <c>fe80::1</c>, <c>[::1]:10050</c>) becomes a <see cref="DnsEndPoint"/>, on the
/// given port where the text names none. A list reads each entry, and an endpoint passes through.
/// </summary>
/// <param name="defaultPort">The port when the text names none: the trapper's, unless the parameter says otherwise.</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixEndPointAttribute(int defaultPort = ZabbixWireClient.DefaultServerPort) : ArgumentTransformationAttribute
{
    /// <summary>Gets the port when the text names none.</summary>
    public int DefaultPort { get; } = defaultPort;

    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        object? input = inputData is PSObject wrapped ? wrapped.BaseObject : inputData;
        switch (input)
        {
            case string text:
                return Read(text);
            case IEnumerable many:
                var read = new List<object?>();
                foreach (object? item in many)
                {
                    object? element = item is PSObject inner ? inner.BaseObject : item;
                    read.Add(element is string entry ? Read(entry) : element);
                }
                return read.ToArray();
            default:
                return input;
        }
    }

    private DnsEndPoint Read(string text)
        => ZabbixEndPoint.TryParse(text, DefaultPort, out DnsEndPoint? endPoint)
            ? endPoint
            : throw new ArgumentTransformationMetadataException($"'{text}' is not a server node: give host, host:port, an IPv6 address, or [ipv6]:port.");
}
