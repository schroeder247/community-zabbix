// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Frozen;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>An enumerated field: the API writes the number as text (<c>"status":"1"</c>) and accepts a number back.</summary>
/// <remarks>
/// A <see cref="JsonConverter{T}"/> of its own, because neither platform converter fits on .NET 10:
/// <see cref="JsonNumberEnumConverter{TEnum}"/> writes the number but refuses <c>"3"</c> with a
/// <see cref="JsonException"/> even under <see cref="JsonNumberHandling.AllowReadingFromString"/>, and
/// <see cref="JsonStringEnumConverter{TEnum}"/> (<c>allowIntegerValues: true</c>) reads <c>"3"</c> and <c>3</c> but writes
/// the member's name (<c>"Wednesday"</c>), which no Zabbix field accepts. The API reads text and wants a number back.
/// <para>A name is read only when it is one member's name. <see cref="Enum.TryParse{TEnum}(string?, bool, out TEnum)"/> alone
/// reads a comma list as the OR of its members (<c>"Disabled, NotSupported"</c> reads as ZabbixItemStatus 3 on an
/// enum that is not <c>[Flags]</c>), and checking <see cref="Enum.IsDefined{TEnum}(TEnum)"/> after it does not help when
/// the OR is itself a member (1|3 is 3), so the name is looked up among <see cref="Enum.GetNames{TEnum}"/>.</para>
/// </remarks>
/// <typeparam name="TEnum">The enum.</typeparam>
internal sealed class ZabbixEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    private static readonly FrozenDictionary<string, TEnum> s_names =
        Enum.GetNames<TEnum>().ToFrozenDictionary(name => name, Enum.Parse<TEnum>, StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc/>
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.TryGetInt64(out long read) ? FromNumber(read) : throw new JsonException($"The number is not a {typeof(TEnum).Name}.");
        if (reader.TokenType != JsonTokenType.String) throw new JsonException($"A {reader.TokenType} is not a {typeof(TEnum).Name}.");
        string? text = reader.GetString();
        if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number)) return FromNumber(number);
        return text is not null && s_names.TryGetValue(text, out TEnum named) ? named : throw new JsonException($"'{text}' is not a {typeof(TEnum).Name}.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue(ToNumber(value));
    }

    // The value's bits as its underlying type, without boxing (Enum.ToObject and Convert.ToInt64 box every value). A
    // number the underlying type cannot hold is not this enum's: refused rather than truncated into another member.
    private static readonly TypeCode s_underlying = Type.GetTypeCode(Enum.GetUnderlyingType(typeof(TEnum)));

    private static TEnum FromNumber(long number)
    {
        try
        {
            return s_underlying switch
            {
                TypeCode.SByte => Unsafe.BitCast<sbyte, TEnum>(checked((sbyte)number)),
                TypeCode.Byte => Unsafe.BitCast<byte, TEnum>(checked((byte)number)),
                TypeCode.Int16 => Unsafe.BitCast<short, TEnum>(checked((short)number)),
                TypeCode.UInt16 => Unsafe.BitCast<ushort, TEnum>(checked((ushort)number)),
                TypeCode.Int32 => Unsafe.BitCast<int, TEnum>(checked((int)number)),
                TypeCode.UInt32 => Unsafe.BitCast<uint, TEnum>(checked((uint)number)),
                TypeCode.Int64 => Unsafe.BitCast<long, TEnum>(number),
                _ => Unsafe.BitCast<ulong, TEnum>(checked((ulong)number)),
            };
        }
        catch (OverflowException overflow)
        {
            throw new JsonException($"{number} is out of the range of {typeof(TEnum).Name}.", overflow);
        }
    }

    private static long ToNumber(TEnum value) => s_underlying switch
    {
        TypeCode.SByte => Unsafe.BitCast<TEnum, sbyte>(value),
        TypeCode.Byte => Unsafe.BitCast<TEnum, byte>(value),
        TypeCode.Int16 => Unsafe.BitCast<TEnum, short>(value),
        TypeCode.UInt16 => Unsafe.BitCast<TEnum, ushort>(value),
        TypeCode.Int32 => Unsafe.BitCast<TEnum, int>(value),
        TypeCode.UInt32 => Unsafe.BitCast<TEnum, uint>(value),
        TypeCode.Int64 => Unsafe.BitCast<TEnum, long>(value),
        _ => unchecked((long)Unsafe.BitCast<TEnum, ulong>(value)),
    };
}
