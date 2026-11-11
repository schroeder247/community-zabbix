// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;

namespace Community.Zabbix;

/// <summary>
/// Represents a sample: one value of one item on one host at one instant. It is the record on every edge of both
/// planes (what a history read yields, what a watcher yields, what an item on an agent produces, what a proxy stores,
/// what the sender frames), converted to another schema only at the edge that needs it.
/// </summary>
/// <param name="Host">The monitored host's technical name — the sender wire's <c>host</c>.</param>
/// <param name="Key">The item key.</param>
/// <param name="Value">The value in its canonical text form; the wire carries every value as text. When
/// <see cref="IsSupported"/> is <see langword="false"/>, the reason the item is not supported.</param>
/// <param name="Time">When the value was observed.</param>
public sealed record ZabbixValue(string Host, ZabbixItemKey Key, string Value, DateTimeOffset Time)
{
    /// <summary>When the value was observed, to 100 ns: the wire's <c>clock</c> and <c>ns</c>.</summary>
    public DateTimeOffset Time { get => Clock.ToDateTimeOffset(); init => Clock = ZabbixClock.FromDateTimeOffset(value); }

    // The exact instant, to the nanosecond, for the SDK's own paths: history's key is (itemid, clock, ns).
    internal ZabbixClock Clock { get; init; } = ZabbixClock.FromDateTimeOffset(Time);

    internal ZabbixValue(string host, ZabbixItemKey key, string value, ZabbixClock clock)
        : this(host, key, value, default(DateTimeOffset))
    {
        Clock = clock;
    }

    internal ZabbixValue(string host, string key, string value, ZabbixClock clock)
        : this(host, ZabbixItemKey.Parse(key), value, clock)
    {
    }

    internal ZabbixValue(string host, string key, long value, ZabbixClock clock)
        : this(host, key, value.ToString(CultureInfo.InvariantCulture), clock)
    {
    }

    internal ZabbixValue(string host, string key, double value, ZabbixClock clock)
        : this(host, key, Finite(value).ToString("R", CultureInfo.InvariantCulture), clock)
    {
    }

    /// <summary>A sample whose key is given as text.</summary>
    /// <param name="host">The host.</param>
    /// <param name="key">The item key, parsed.</param>
    /// <param name="value">The value it carries.</param>
    /// <param name="time">When the value was observed.</param>
    /// <exception cref="FormatException"><paramref name="key"/> is not an item key.</exception>
    public ZabbixValue(string host, string key, string value, DateTimeOffset time)
        : this(host, ZabbixItemKey.Parse(key), value, time)
    {
    }

    /// <summary>A numeric sample, written as Zabbix parses it whatever the current culture (<c>42</c>, never <c>42,0</c>).</summary>
    /// <param name="host">The monitored host's technical name.</param>
    /// <param name="key">The item key, parsed.</param>
    /// <param name="value">The number to send.</param>
    /// <param name="time">When the value was observed.</param>
    /// <exception cref="FormatException"><paramref name="key"/> is not an item key.</exception>
    public ZabbixValue(string host, string key, long value, DateTimeOffset time)
        : this(host, key, value.ToString(CultureInfo.InvariantCulture), time)
    {
    }

    /// <summary>
    /// A floating-point sample, written as Zabbix parses it whatever the current culture (<c>0.25</c>, never <c>0,25</c>),
    /// to the shortest text that reads back as the same <see cref="double"/>.
    /// </summary>
    /// <param name="host">The monitored host's technical name.</param>
    /// <param name="key">The item key, parsed.</param>
    /// <param name="value">The number to send.</param>
    /// <param name="time">When the value was observed.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is not finite: Zabbix stores no NaN or infinity.</exception>
    /// <exception cref="FormatException"><paramref name="key"/> is not an item key.</exception>
    public ZabbixValue(string host, string key, double value, DateTimeOffset time)
        : this(host, key, Finite(value).ToString("R", CultureInfo.InvariantCulture), time)
    {
    }

    /// <summary>A value of a typed item.</summary>
    /// <typeparam name="T">The item's value type.</typeparam>
    /// <param name="host">The host the value belongs to, as Zabbix knows it.</param>
    /// <param name="item">The item the value belongs to.</param>
    /// <param name="value">The value it carries.</param>
    /// <param name="time">When the value was collected.</param>
    /// <returns>The value, as the send path carries it.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is NaN or infinite.</exception>
    public static ZabbixValue From<T>(string host, ZabbixItemKey<T> item, T value, DateTimeOffset time)
        where T : ISpanParsable<T>
        => new(host, item.Key, item.Format(value), time);

    /// <summary>Reads this value as a <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">The type the item holds.</typeparam>
    /// <returns>The value.</returns>
    /// <exception cref="FormatException">The value isn't a <typeparamref name="T"/>.</exception>
    public T As<T>()
        where T : ISpanParsable<T>
        => ZabbixItemValues.Parse<T>(Value);

    private static double Finite(double value)
        => double.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "Zabbix stores no NaN or infinity.");

    /// <summary>
    /// Whether the item was supported when the value was taken. <see langword="false"/> is Zabbix's item <c>state</c> 1
    /// (<c>ITEM_STATE_NOTSUPPORTED</c>): <see cref="Value"/> is then the reason, as an active agent reports it and a proxy
    /// forwards it, and the server marks the item not supported with that error instead of storing a value
    /// (<c>proxy.c</c> <c>process_history_data_value</c>). The state travels with the value, not beside it.
    /// </summary>
    public bool IsSupported { get; init; } = true;
}
