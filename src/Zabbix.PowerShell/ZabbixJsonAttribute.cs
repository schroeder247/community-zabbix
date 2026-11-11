// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// A field whose JSON shape Zabbix does not fix (<c>-Config</c> of a module, <c>-Items</c> of a host interface), every
/// one a <see cref="JsonElement"/>: a hashtable, an <c>[ordered]</c> dictionary, a <c>[pscustomobject]</c> or an array
/// of them is written as the JSON object or array it holds, a string is read as JSON text, and a
/// <see cref="JsonElement"/> passes through. PowerShell has no conversion to <see cref="JsonElement"/> of its own, so
/// without this the parameter cannot be bound from a shell. The walk is the module's one shell-to-JSON walk
/// (<see cref="ZabbixJsonShape.FromShell"/>), never PowerShell's own JSON.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixJsonAttribute : ArgumentTransformationAttribute
{
    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        if (inputData is null) return null;
        try
        {
            return ZabbixJsonShape.FromShell(inputData);
        }
        catch (JsonException notJson)
        {
            throw new ArgumentTransformationMetadataException($"The text is not JSON: {notJson.Message} Give a hashtable, a [pscustomobject], an array of them, or JSON text.", notJson);
        }
        catch (ArgumentException refused)
        {
            throw new ArgumentTransformationMetadataException(refused.Message, refused);
        }
    }
}
