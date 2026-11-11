// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0

namespace Community.Zabbix;

/// <summary>
/// An item key and the type of its values. Declare one once and pass it wherever the item is sent, read, or answered, as
/// <c>Meter.CreateCounter&lt;long&gt;("app.requests")</c> pairs a name with a type. The compiler then holds every use to that
/// type, and nothing needs parsing.
/// </summary>
/// <typeparam name="T">
/// The value's type: <see cref="long"/> or <see cref="ulong"/> for a numeric (unsigned) item, <see cref="double"/> for a numeric
/// (float) item, <see cref="string"/> for a character, text, or log item. <see cref="bool"/> reads 1 and 0,
/// <see cref="TimeSpan"/> reads seconds, and <see cref="DateTimeOffset"/> reads seconds since the epoch.
/// </typeparam>
/// <example>
/// <code language="csharp">
/// static readonly ZabbixItemKey&lt;long&gt; Requests = new("app.requests");
///
/// await wire.SendAsync("web-01", Requests, 42);
/// long last = await zabbix.Hosts["web-01"].GetLastAsync(Requests);
/// </code>
/// </example>
public readonly record struct ZabbixItemKey<T>
    where T : ISpanParsable<T>
{
    /// <summary>An item with this key.</summary>
    /// <param name="key">The key, with any parameters: <c>app.requests</c>, <c>vfs.fs.size[/,free]</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="key"/> is not an item key.</exception>
    public ZabbixItemKey([ZabbixItemKeyData] string key)
        : this(ZabbixItemKey.Parse(key))
    {
    }

    /// <summary>An item with this key.</summary>
    /// <param name="key">The parsed item key.</param>
    public ZabbixItemKey(ZabbixItemKey key) => Key = key;

    /// <summary>The item's key.</summary>
    public ZabbixItemKey Key { get; }

    /// <summary>The same item with these parameters: <c>new ZabbixItemKey&lt;long&gt;("app.disk.free").With("/")</c> is <c>app.disk.free[/]</c>.</summary>
    /// <param name="parameters">The key's parameters.</param>
    /// <returns>The item with these parameters in place of any it has.</returns>
    /// <exception cref="ArgumentException">A parameter can't be written in a key.</exception>
    public ZabbixItemKey<T> With([ZabbixItemKeyData] params ReadOnlySpan<string> parameters) => new(ZabbixItemKey.Create(Key.Name, parameters));

    /// <summary>Reads a value of this item from the text Zabbix carries.</summary>
    /// <param name="value">The text Zabbix carries.</param>
    /// <returns>The value.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is <see langword="null"/>.</exception>
    /// <exception cref="FormatException"><paramref name="value"/> isn't a <typeparamref name="T"/>: the item holds another type on the server.</exception>
    public T Parse(string value) => ZabbixItemValues.Parse<T>(value);

    /// <summary>Writes a value of this item as the text Zabbix carries.</summary>
    /// <param name="value">The value to write.</param>
    /// <returns>The text.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="value"/> is NaN or infinite, which Zabbix doesn't store.</exception>
    public string Format(T value) => ZabbixItemValues.Format(value);

    /// <summary>The key.</summary>
    /// <returns>The key's text.</returns>
    public override string ToString() => Key.ToString();
}
