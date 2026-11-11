// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix;

/// <summary>
/// The exception that is thrown when a host, a group or another object, by its name or id, or an item key, does not exist on
/// the server, or when an item has never collected a value. Absence is answered one way across the tree: the first read of a
/// node whose host, group or item does not exist throws this (a <see cref="KeyNotFoundException"/>, so a <c>catch</c> on the
/// BCL type still sees it), and every <c>Get…Async</c> that finds nothing throws it where its <c>Find…Async</c> returns
/// <see langword="null"/>. A caller that would rather test first calls <see cref="ZabbixNode.ExistsAsync"/> (Tester-Doer).
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

    /// <summary>The server has no <paramref name="objectType"/> with the id <paramref name="id"/>.</summary>
    internal ZabbixNotFoundException(string objectType, ulong id)
        : base(string.Create(CultureInfo.InvariantCulture, $"The Zabbix server has no {objectType} with the id {id}."))
    {
        ObjectType = objectType;
        Id = id;
    }

    private ZabbixNotFoundException(string message, string objectType, string name)
        : base(message)
    {
        ObjectType = objectType;
        Name = name;
    }

    /// <summary>An item key the host does not have.</summary>
    /// <param name="host">The host, as it was asked for: its technical name, or its id.</param>
    /// <param name="key">The key that was asked for.</param>
    internal static ZabbixNotFoundException NoItem(string host, ZabbixItemKey key)
        => new($"The Zabbix host {host} has no item with a key named '{key.Name}'.", "item", key.ToString());

    /// <summary>An item that has never collected a value.</summary>
    /// <param name="host">The host, as it was asked for: its technical name, or its id.</param>
    /// <param name="key">The item's key.</param>
    internal static ZabbixNotFoundException NoValue(string host, ZabbixItemKey key)
        => new($"The item '{key}' on the Zabbix host {host} has never collected a value.", "item", key.ToString());

    /// <summary>
    /// The API object that is absent: <c>host</c>, <c>hostgroup</c>, the object of an API client's node, or <c>item</c>
    /// (then <see cref="Name"/> is the key).
    /// </summary>
    public string? ObjectType { get; }

    /// <summary>The name that was asked for, when the object was asked for by its name.</summary>
    public string? Name { get; }

    /// <summary>The id that was asked for, when the object was asked for by its id.</summary>
    public ulong? Id { get; }

    string IZabbixErrorTyped.ErrorType => "not_found";
}
