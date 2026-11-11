// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>The values of one <see cref="ZabbixWireClient.SendAsync(Action{ZabbixSendBatch}, CancellationToken)"/> call.</summary>
public sealed class ZabbixSendBatch
{
    private readonly List<ZabbixValue> _values = [];
    private readonly TimeProvider _time;

    internal ZabbixSendBatch(TimeProvider time) => _time = time;

    /// <summary>How many values the batch holds.</summary>
    public int Count => _values.Count;

    internal IReadOnlyCollection<ZabbixValue> Values => _values;

    /// <summary>Adds a value, collected now.</summary>
    /// <typeparam name="T">The item's value type.</typeparam>
    /// <param name="hostName">The technical name of the host the value belongs to.</param>
    /// <param name="key">The typed key of the item the value belongs to.</param>
    /// <param name="value">The value to send.</param>
    /// <returns>The batch.</returns>
    public ZabbixSendBatch Add<T>(string hostName, ZabbixItemKey<T> key, T value)
        where T : ISpanParsable<T>
        => Add(hostName, key, value, _time.GetUtcNow());

    /// <summary>Adds a value collected at <paramref name="time"/>.</summary>
    /// <typeparam name="T">The item's value type.</typeparam>
    /// <param name="hostName">The technical name of the host the value belongs to.</param>
    /// <param name="key">The typed key of the item the value belongs to.</param>
    /// <param name="value">The value to send.</param>
    /// <param name="time">When the value was collected.</param>
    /// <returns>The batch.</returns>
    public ZabbixSendBatch Add<T>(string hostName, ZabbixItemKey<T> key, T value, DateTimeOffset time)
        where T : ISpanParsable<T>
    {
        _values.Add(ZabbixValue.From(hostName, key, value, time));
        return this;
    }
}
