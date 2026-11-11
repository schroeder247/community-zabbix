// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>Binds <c>-Connection</c>: a connection object as is, or the name of one in the runspace's context.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixConnectionTransformationAttribute : ArgumentTransformationAttribute
{
    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        object? input = inputData is PSObject wrapped ? wrapped.BaseObject : inputData;
        return input switch
        {
            null or ZabbixConnection => input,
            string name when ZabbixContext.TryGetCurrent(out ZabbixContext? context) && context.Servers.TryGetValue(name, out ZabbixConnection? connection) => connection,
            string name => throw new ArgumentTransformationMetadataException(
                $"no connection is named '{name}'; connected: {string.Join(", ", ZabbixContext.TryGetCurrent(out ZabbixContext? context) ? context.Servers.Keys : [])}"),
            _ => throw new ArgumentTransformationMetadataException($"'{input}' is not a connection or a connection name"),
        };
    }
}
