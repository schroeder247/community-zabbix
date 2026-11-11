// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix;

/// <summary>
/// The exception that is thrown when a host or group name, or an item key, does not exist on the server. Absence is
/// answered one way across the tree: the first read of a node whose host, group or item does not exist throws this (a <see cref="KeyNotFoundException"/>, so a <c>catch</c> on the BCL
/// type still sees it). A caller that would rather test first calls <see cref="ZabbixNode.ExistsAsync"/> (Tester-Doer).
/// A host that exists and has no items is not absent: it answers with nothing.
/// </summary>
public sealed class ZabbixNotFoundException : KeyNotFoundException, IZabbixErrorTyped
{
    /// <summary>An absence with no detail.</summary>
    public ZabbixNotFoundException()
    {
    }

    /// <summary>An absence with a message.</summary>
    /// <param name="message">The message.</param>
    public ZabbixNotFoundException(string message) : base(message)
    {
    }

    /// <summary>An absence caused by another failure.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ZabbixNotFoundException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>The server has no <paramref name="objectType"/> named <paramref name="name"/>.</summary>
    /// <param name="objectType">The API object, in the API's word (<c>host</c>, <c>hostgroup</c>).</param>
    /// <param name="name">The name that was asked for.</param>
    public ZabbixNotFoundException(string objectType, string name)
        : base($"The Zabbix server has no {objectType} named '{name}'.")
    {
        ObjectType = objectType;
        Name = name;
    }

    /// <summary>An item key the host does not have.</summary>
    internal ZabbixNotFoundException(string host, ZabbixItemKey key)
        : base($"The Zabbix host '{host}' has no item with a key named '{key.Name}'.")
    {
        ObjectType = "item";
        Name = key.ToString();
    }

    /// <summary>The API object that is absent: <c>host</c>, <c>hostgroup</c> or <c>item</c> (then <see cref="Name"/> is the key).</summary>
    public string? ObjectType { get; }

    /// <summary>The name that was asked for.</summary>
    public string? Name { get; }

    string IZabbixErrorTyped.ErrorType => "not_found";
}
