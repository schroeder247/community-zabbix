// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// Binds a tag given as the shell has it — a <c>{tag, value}</c> hashtable (<c>New-ZbxTag</c>), a read tag record, a
/// <c>[pscustomobject]</c> or hashtable with <c>Name</c>/<c>Value</c>, or the request record itself — to
/// <see cref="ZabbixTagRequest"/>. PowerShell binds no hashtable key by property name, so the builder's hashtable
/// needs a parameter that takes it by value, and a conversion the shell gets wrong is a transformation attribute.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixTagPairAttribute : ArgumentTransformationAttribute
{
    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        object? input = inputData is PSObject { BaseObject: not PSCustomObject } wrapped ? wrapped.BaseObject : inputData;
        if (input is null) return null;
        IEnumerable<object?> items = input is System.Collections.IEnumerable many and not string and not System.Collections.IDictionary ? many.Cast<object?>() : [input];
        return items.Select(Pair).ToArray();
    }

    private static ZabbixTagRequest Pair(object? item)
    {
        object? value = item is PSObject { BaseObject: not PSCustomObject } wrapped ? wrapped.BaseObject : item;
        switch (value)
        {
            case ZabbixTagRequest request:
                return request;
            case System.Collections.IDictionary table:
                string? Key(string name) => table.Keys.Cast<object>().FirstOrDefault(k => string.Equals(k.ToString(), name, StringComparison.OrdinalIgnoreCase)) is { } key ? table[key]?.ToString() : null;
                return Build(Key("tag") ?? Key("name"), Key("value"), item);
            case not null:
                PSObject shell = PSObject.AsPSObject(item!);
                string? Property(string name) => shell.Properties[name]?.Value?.ToString();
                return Build(Property("Tag") ?? Property("Name"), Property("Value"), item);
            default:
                throw new ArgumentTransformationMetadataException("a tag is not null: give a {tag, value} (or {Name, Value}) object or hashtable");
        }
    }

    private static ZabbixTagRequest Build(string? tag, string? value, object? item)
        => string.IsNullOrWhiteSpace(tag)
            ? throw new ArgumentTransformationMetadataException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"'{item}' is not a tag: give an object or hashtable with a tag (or Name) and a value"))
            : new ZabbixTagRequest { Tag = tag, Value = value ?? string.Empty };
}
