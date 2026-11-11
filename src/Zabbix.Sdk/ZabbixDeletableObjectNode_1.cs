// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents one object of an API client that the API can delete, by its id: <c>zabbix.Items[id].DeleteAsync()</c>. No
/// call is made until it's asked.
/// </summary>
/// <typeparam name="T">The record the API reads the object as.</typeparam>
public class ZabbixDeletableObjectNode<T> : ZabbixObjectNode<T>
    where T : class
{
    private readonly Func<ulong, CancellationToken, Task>? _delete;

    /// <summary>Initializes a node for mocking; every member of a node built this way is to be overridden.</summary>
    protected ZabbixDeletableObjectNode()
    {
    }

    internal ZabbixDeletableObjectNode(ulong id, string objectType, Func<ulong, CancellationToken, Task<T?>> find, Func<ulong, CancellationToken, Task> delete)
        : base(id, objectType, find)
    {
        _delete = delete;
    }

    /// <summary>Deletes the object.</summary>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>A task that completes when the server has deleted it.</returns>
    public virtual Task DeleteAsync(CancellationToken cancellationToken = default)
        => (_delete ?? throw new InvalidOperationException("The node was built for mocking, and this member is not overridden."))(Id, cancellationToken);
}
