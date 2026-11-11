// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// An option whose <see langword="null"/> is a request of its own, apart from leaving it out: <c>history.get</c>'s
/// <c>maxValueSize</c> left out cuts a binary or JSON value at 64 KiB, <see langword="null"/> asks for the whole value
/// (<c>CHistory.php</c> 8.0:121, 238). A <see cref="Nullable{T}"/> cannot say both, since the API's records leave a
/// <see langword="null"/> member out. <see langword="default"/> is <see cref="Absent"/> (not sent); <see cref="Null"/> sends
/// <c>null</c>; a value converts implicitly and is sent as itself.
/// </summary>
/// <typeparam name="T">The option's value.</typeparam>
public readonly struct ZabbixOptional<T> : IEquatable<ZabbixOptional<T>> where T : struct
{
    private readonly T _value;
    private readonly State _state;

    private ZabbixOptional(State state, T value)
    {
        this._state = state;
        this._value = value;
    }

    private enum State : byte
    {
        Absent,
        Null,
        Value,
    }

    /// <summary>Not sent: the server's default applies.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "The option's named states and its named conversion, as Nullable<T> and ImmutableArray<T>.Empty are; the type argument is always inferred from the target.")]
    public static ZabbixOptional<T> Absent => default;

    /// <summary>Sent as <c>null</c>.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "The option's named states and its named conversion, as Nullable<T> and ImmutableArray<T>.Empty are; the type argument is always inferred from the target.")]
    public static ZabbixOptional<T> Null => new(State.Null, default);

    /// <summary>Whether the option is sent at all (as <c>null</c> or a value).</summary>
    public bool IsSent => _state != State.Absent;

    /// <summary>Whether the option is sent as <c>null</c>.</summary>
    public bool IsNull => _state == State.Null;

    /// <summary>The value, when one is sent.</summary>
    /// <param name="value">The value; <see langword="default"/> when there is none.</param>
    /// <returns>Whether a value is sent.</returns>
    public bool TryGetValue([MaybeNullWhen(false)] out T value)
    {
        value = this._value;
        return _state == State.Value;
    }

    /// <summary>A value, or <see cref="Absent"/> for <see langword="null"/> (use <see cref="Null"/> to send <c>null</c>).
    /// A plain value converts too (<c>MaxValueSize = 1024</c>).</summary>
    /// <param name="value">The value.</param>
    /// <returns>The present value.</returns>
    public static implicit operator ZabbixOptional<T>(T? value) => FromNullable(value);

    /// <summary>A value, or <see cref="Absent"/> for <see langword="null"/> (the named alternative of the implicit conversion).</summary>
    /// <param name="value">The value.</param>
    /// <returns>The option.</returns>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "The option's named states and its named conversion, as Nullable<T> and ImmutableArray<T>.Empty are; the type argument is always inferred from the target.")]
    public static ZabbixOptional<T> FromNullable(T? value) => value is { } v ? new(State.Value, v) : default;

    /// <inheritdoc/>
    public bool Equals(ZabbixOptional<T> other) => _state == other._state && EqualityComparer<T>.Default.Equals(_value, other._value);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is ZabbixOptional<T> other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(_state, _value);

    /// <summary>Equality.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns><see langword="true"/> when both are absent, or both are present with equal values.</returns>
    public static bool operator ==(ZabbixOptional<T> left, ZabbixOptional<T> right) => left.Equals(right);

    /// <summary>Inequality.</summary>
    /// <param name="left">One.</param>
    /// <param name="right">The other.</param>
    /// <returns><see langword="true"/> when one is absent and the other is not, or their values differ.</returns>
    public static bool operator !=(ZabbixOptional<T> left, ZabbixOptional<T> right) => !left.Equals(right);

    /// <inheritdoc/>
    public override string ToString() => _state switch
    {
        State.Value => Convert.ToString(_value, CultureInfo.InvariantCulture) ?? string.Empty,
        State.Null => "null",
        _ => string.Empty,
    };
}

/// <summary>
/// Writes a <see cref="ZabbixOptional{T}"/> as <c>null</c> or its value; the member is left out when it is
/// <see cref="ZabbixOptional{T}.Absent"/> (the generated member carries <c>JsonIgnoreCondition.WhenWritingDefault</c>).
/// Reads <c>null</c> as <see cref="ZabbixOptional{T}.Null"/>. Owned because the platform's nullable has no third state
/// for "absent".
/// </summary>
/// <typeparam name="T">The option's value.</typeparam>
internal sealed class ZabbixOptionalConverter<T> : JsonConverter<ZabbixOptional<T>> where T : struct
{
    /// <inheritdoc/>
    public override bool HandleNull => true;

    /// <inheritdoc/>
    public override ZabbixOptional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return ZabbixOptional<T>.Null;
        ArgumentNullException.ThrowIfNull(options);
        return JsonSerializer.Deserialize(ref reader, options.GetTypeInfo(typeof(T))) is T value
            ? value
            : throw new JsonException($"The value is not a {typeof(T).Name}.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ZabbixOptional<T> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);
        if (value.TryGetValue(out T set)) JsonSerializer.Serialize(writer, set, options.GetTypeInfo(typeof(T)));
        else writer.WriteNullValue();
    }
}
