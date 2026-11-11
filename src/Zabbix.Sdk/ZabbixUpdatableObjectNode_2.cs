// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>
/// Represents one object of an API client that the API can change and delete, by its id:
/// <c>zabbix.Items[id].UpdateAsync(i =&gt; i.Status = ZabbixItemStatus.Disabled)</c>. No call is made until it's asked.
/// </summary>
/// <typeparam name="T">The record the API reads the object as.</typeparam>
/// <typeparam name="TUpdate">The request that changes the object.</typeparam>
public class ZabbixUpdatableObjectNode<T, TUpdate> : ZabbixDeletableObjectNode<T>
    where T : class
    where TUpdate : class
{
    private readonly Func<ulong, TUpdate>? _request;
    private readonly Func<TUpdate, CancellationToken, Task>? _update;

    /// <summary>Initializes a node for mocking; every member of a node built this way is to be overridden.</summary>
    protected ZabbixUpdatableObjectNode()
    {
    }

    internal ZabbixUpdatableObjectNode(ulong id, string objectType, Func<ulong, CancellationToken, Task<T?>> find, Func<ulong, CancellationToken, Task> delete,
        Func<ulong, TUpdate> request, Func<TUpdate, CancellationToken, Task> update)
        : base(id, objectType, find, delete)
    {
        _request = request;
        _update = update;
    }

    /// <summary>
    /// Changes the object: the request names it by <see cref="ZabbixObjectNode{T}.Id"/>, and <paramref name="configure"/> sets
    /// the fields to change. What it leaves unset isn't sent, so the server keeps it.
    /// </summary>
    /// <param name="configure">Sets the fields to change: <c>i =&gt; i.Status = ZabbixItemStatus.Disabled</c>.</param>
    /// <param name="cancellationToken">A token that cancels the call.</param>
    /// <returns>A task that completes when the server has changed the object.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ZabbixApiException">The server refused the change, or has no object with this id that the caller may change.</exception>
    public virtual Task UpdateAsync(Action<TUpdate> configure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configure);
        if (_request is null || _update is null) throw new InvalidOperationException("The node was built for mocking, and this member is not overridden.");
        TUpdate request = _request(Id);
        configure(request);
        return _update(request, cancellationToken);
    }
}
