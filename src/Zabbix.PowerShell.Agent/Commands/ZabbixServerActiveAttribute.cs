// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Management.Automation;
using System.Net;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// An active agent's destinations (<c>Start-ZbxActiveAgent -Server</c>), as Zabbix's <c>ServerActive</c> text: one string
/// is that text (<c>'a;b'</c> is one HA cluster, tried in order; <c>'a,b'</c> is two independent destinations, each sent
/// every value), and an array of nodes (<c>-Server a, b</c>) is one HA cluster, as every other <c>-Server</c> of the modules
/// reads a list.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixServerActiveAttribute : ArgumentTransformationAttribute
{
    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        object? input = inputData is PSObject wrapped ? wrapped.BaseObject : inputData;
        switch (input)
        {
            case null:
                return input;
            case string text:
                return Checked(text);
            case DnsEndPoint node:
                return ZabbixEndPoint.Format(node);
            case IEnumerable many:
                var nodes = new List<string>();
                foreach (object? item in many)
                {
                    object? element = item is PSObject inner ? inner.BaseObject : item;
                    if (element is DnsEndPoint given) nodes.Add(ZabbixEndPoint.Format(given));
                    else if (element is string entry && ZabbixEndPoint.TryParse(entry, ZabbixWireClient.DefaultServerPort, out DnsEndPoint? parsed)) nodes.Add(ZabbixEndPoint.Format(parsed));
                    else throw new ArgumentTransformationMetadataException($"'{element}' is not a server node: a list names the nodes of one HA cluster (host or host:port).");
                }
                return Checked(string.Join(';', nodes));
            default:
                throw new ArgumentTransformationMetadataException($"'{input}' is not a ServerActive value: give 'host', 'host:port', 'a;b' (failover) or 'a,b' (independent destinations).");
        }
    }

    private static string Checked(string text)
    {
        try
        {
            _ = ZabbixEndPoint.ParseServerActive(text);
            return text;
        }
        catch (FormatException refused)
        {
            throw new ArgumentTransformationMetadataException($"'{text}' is not a ServerActive value: {refused.Message}", refused);
        }
    }
}
