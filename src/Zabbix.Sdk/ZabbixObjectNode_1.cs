// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents one object of an API client, by its id: <c>zabbix.Items[id]</c>. No call is made until it's asked.
/// </summary>
/// <typeparam name="T">The record the API reads the object as.</typeparam>
public class ZabbixObjectNode<T>
    where T : class
{
    private readonly string? _objectType;
    private readonly Func<ulong, CancellationToken, Task<T?>>? _find;

    /// <summary>Initializes a node for mocking; every member of a node built this way is to be overridden.</summary>
    protected ZabbixObjectNode()
    {
    }

    internal ZabbixObjectNode(ulong id, string objectType, Func<ulong, CancellationToken, Task<T?>> find)
    {
        Id = id;
        _objectType = objectType;
        _find = find;
    }

    /// <summary>Gets the object's id.</summary>
    public virtual ulong Id { get; }

    /// <summary>Reads the object.</summary>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>The object, as the API reads it.</returns>
    /// <exception cref="ZabbixNotFoundException">The server has no object with this id that the caller may read.</exception>
    public virtual async Task<T> GetAsync(CancellationToken cancellationToken = default)
        => await FindAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new ZabbixNotFoundException(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"The Zabbix server has no {_objectType} with the id {Id}."));

    /// <summary>Reads the object, if there is one.</summary>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>The object, or <see langword="null"/> when the server has none with this id that the caller may read.</returns>
    public virtual Task<T?> FindAsync(CancellationToken cancellationToken = default)
        => (_find ?? throw new InvalidOperationException("The node was built for mocking, and this member is not overridden."))(Id, cancellationToken);
}
