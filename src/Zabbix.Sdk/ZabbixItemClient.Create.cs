// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

// item.create from a typed key: the item's key is the key's text and its value type follows T, as the key that sends,
// reads and answers the item holds it (ADR 0013, section 3).
public partial class ZabbixItemClient
{
    /// <summary>
    /// <c>item.create</c> for <paramref name="key"/> on a host (writes): the item's key is the key's text, its name too, and
    /// its value type follows <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The type of the item's values.</typeparam>
    /// <param name="hostId">The host or template that owns the item, by ID.</param>
    /// <param name="key">The item's typed key.</param>
    /// <param name="type">The kind of check that collects the item's values: <see cref="ZabbixItemType.Trapper"/> for one an application sends.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id of the item the call created.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is the default value.</exception>
    /// <exception cref="ZabbixApiException">The server answered <c>item.create</c> with an error.</exception>
    /// <remarks>
    /// <para>The value type a key's type gives the item:</para>
    /// <list type="table">
    /// <listheader><term><c>T</c></term><description>Value type</description></listheader>
    /// <item><term><see cref="long"/>, <see cref="int"/>, <see cref="short"/>, <see cref="sbyte"/>, <see cref="Int128"/>, <see cref="nint"/>, <see cref="ulong"/>, <see cref="uint"/>, <see cref="ushort"/>, <see cref="byte"/>, <see cref="UInt128"/></term><description><see cref="ZabbixItemValueType.Unsigned"/>: a whole number, as a counter is.</description></item>
    /// <item><term><see cref="bool"/>, <see cref="DateTimeOffset"/></term><description><see cref="ZabbixItemValueType.Unsigned"/>: 1 and 0, and seconds since the epoch, as Zabbix's own items keep them.</description></item>
    /// <item><term><see cref="float"/>, <see cref="double"/>, <see cref="decimal"/>, <see cref="Half"/>, <see cref="TimeSpan"/></term><description><see cref="ZabbixItemValueType.Float"/>; a <see cref="TimeSpan"/> is its seconds.</description></item>
    /// <item><term><see cref="string"/>, and any other type</term><description><see cref="ZabbixItemValueType.Text"/>: the value's text. For a character or a log item, pass the value type.</description></item>
    /// </list>
    /// <para>
    /// Zabbix refuses a negative value for an unsigned item when the value arrives, not when the item is created. A key
    /// whose values can be negative takes the overload with the value type, and passes <see cref="ZabbixItemValueType.Float"/>:
    /// <see cref="CreateAsync{T}(ulong, ZabbixItemKey{T}, ZabbixItemType, ZabbixItemValueType, CancellationToken)"/>.
    /// </para>
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// static readonly ZabbixItemKey&lt;ulong&gt; Pending = new("orders.pending");
    ///
    /// ulong itemId = await zabbix.Items.CreateAsync(hostId, Pending, ZabbixItemType.Trapper);
    /// </code>
    /// </example>
    public virtual Task<ulong> CreateAsync<T>(ulong hostId, ZabbixItemKey<T> key, ZabbixItemType type, CancellationToken cancellationToken = default)
        where T : ISpanParsable<T>
        => CreateAsync(Typed(hostId, key, type, ValueTypeFor<T>()), cancellationToken);

    /// <summary>
    /// <c>item.create</c> for <paramref name="key"/> on a host, with the fields <paramref name="configure"/> sets (writes):
    /// the item's key is the key's text, its value type follows <typeparamref name="T"/> as <see cref="CreateAsync{T}(ulong, ZabbixItemKey{T}, ZabbixItemType, CancellationToken)"/>
    /// says, and its name is the key's text unless <paramref name="configure"/> names it. An integer key makes an unsigned
    /// item, which refuses a negative value when it arrives: a key whose values can be negative passes
    /// <see cref="ZabbixItemValueType.Float"/> to the overload with the value type.
    /// </summary>
    /// <typeparam name="T">The type of the item's values.</typeparam>
    /// <param name="hostId">The host or template that owns the item, by ID.</param>
    /// <param name="key">The item's typed key.</param>
    /// <param name="type">The kind of check that collects the item's values.</param>
    /// <param name="configure">Sets the item's other fields: <c>i =&gt; i.Name = "Pending orders"</c>.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id of the item the call created.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is the default value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ZabbixApiException">The server answered <c>item.create</c> with an error.</exception>
    /// <example>
    /// <code language="csharp">
    /// await zabbix.Items.CreateAsync(hostId, Pending, ZabbixItemType.Trapper, i =&gt; i.Name = "Pending orders");
    /// </code>
    /// </example>
    public virtual Task<ulong> CreateAsync<T>(ulong hostId, ZabbixItemKey<T> key, ZabbixItemType type, Action<ZabbixItemCreateRequest> configure, CancellationToken cancellationToken = default)
        where T : ISpanParsable<T>
    {
        ArgumentNullException.ThrowIfNull(configure);
        ZabbixItemCreateRequest request = Typed(hostId, key, type, ValueTypeFor<T>());
        configure(request);
        return CreateAsync(request, cancellationToken);
    }

    /// <summary>
    /// <c>item.create</c> for <paramref name="key"/> on a host, of the value type <paramref name="valueType"/> (writes): for
    /// a key whose values can be negative, which an unsigned item refuses when they arrive
    /// (<see cref="ZabbixItemValueType.Float"/>), and for a character or a log item of text.
    /// </summary>
    /// <typeparam name="T">The type of the item's values.</typeparam>
    /// <param name="hostId">The host or template that owns the item, by ID.</param>
    /// <param name="key">The item's typed key.</param>
    /// <param name="type">The kind of check that collects the item's values.</param>
    /// <param name="valueType">The type of the values the item stores.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id of the item the call created.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is the default value.</exception>
    /// <exception cref="ZabbixApiException">The server answered <c>item.create</c> with an error.</exception>
    /// <example>
    /// <code language="csharp">
    /// static readonly ZabbixItemKey&lt;long&gt; Balance = new("account.balance");
    ///
    /// await zabbix.Items.CreateAsync(hostId, Balance, ZabbixItemType.Trapper, ZabbixItemValueType.Float);
    /// </code>
    /// </example>
    public virtual Task<ulong> CreateAsync<T>(ulong hostId, ZabbixItemKey<T> key, ZabbixItemType type, ZabbixItemValueType valueType, CancellationToken cancellationToken = default)
        where T : ISpanParsable<T>
        => CreateAsync(Typed(hostId, key, type, valueType), cancellationToken);

    /// <summary>
    /// <c>item.create</c> for <paramref name="key"/> on a host, of the value type <paramref name="valueType"/>, with the
    /// fields <paramref name="configure"/> sets (writes); its name is the key's text unless <paramref name="configure"/>
    /// names it.
    /// </summary>
    /// <typeparam name="T">The type of the item's values.</typeparam>
    /// <param name="hostId">The host or template that owns the item, by ID.</param>
    /// <param name="key">The item's typed key.</param>
    /// <param name="type">The kind of check that collects the item's values.</param>
    /// <param name="valueType">The type of the values the item stores.</param>
    /// <param name="configure">Sets the item's other fields.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id of the item the call created.</returns>
    /// <exception cref="ArgumentException"><paramref name="key"/> is the default value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="configure"/> is <see langword="null"/>.</exception>
    /// <exception cref="ZabbixApiException">The server answered <c>item.create</c> with an error.</exception>
    public virtual Task<ulong> CreateAsync<T>(ulong hostId, ZabbixItemKey<T> key, ZabbixItemType type, ZabbixItemValueType valueType, Action<ZabbixItemCreateRequest> configure, CancellationToken cancellationToken = default)
        where T : ISpanParsable<T>
    {
        ArgumentNullException.ThrowIfNull(configure);
        ZabbixItemCreateRequest request = Typed(hostId, key, type, valueType);
        configure(request);
        return CreateAsync(request, cancellationToken);
    }

    /// <summary>
    /// The value type an item of <typeparamref name="T"/> stores: unsigned for a whole number, a flag and an instant, float
    /// for a fraction and a length of time, text for the rest. A signed integer is a counter's type, and its item is
    /// unsigned too: Zabbix refuses a negative value when it arrives.
    /// </summary>
    /// <typeparam name="T">The type of a key's values.</typeparam>
    /// <returns>The value type.</returns>
    internal static ZabbixItemValueType ValueTypeFor<T>()
    {
        Type type = typeof(T);
        if (type == typeof(long) || type == typeof(int) || type == typeof(short) || type == typeof(sbyte) || type == typeof(Int128) || type == typeof(nint)
            || type == typeof(ulong) || type == typeof(uint) || type == typeof(ushort) || type == typeof(byte) || type == typeof(UInt128)
            || type == typeof(bool) || type == typeof(DateTimeOffset))
            return ZabbixItemValueType.Unsigned;
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal) || type == typeof(Half) || type == typeof(TimeSpan))
            return ZabbixItemValueType.Float;
        return ZabbixItemValueType.Text;
    }

    // The request a typed key makes: its text is the item's key and, until a lambda names the item, its name.
    private static ZabbixItemCreateRequest Typed<T>(ulong hostId, ZabbixItemKey<T> key, ZabbixItemType type, ZabbixItemValueType valueType)
        where T : ISpanParsable<T>
    {
        if (key.Key == default) throw new ArgumentException("The key is the default value, which names no item: declare it with new ZabbixItemKey<T>(\"name\").", nameof(key));
        return new ZabbixItemCreateRequest(hostId, key.ToString(), type, key.Key, valueType);
    }
}
