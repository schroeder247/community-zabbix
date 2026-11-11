// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// An object the API writes as <c>[]</c> when it is empty (PHP's <c>json_encode</c> of an empty array): <c>[]</c> reads as
/// <see langword="null"/>. A non-empty array where an object belongs is not that, and is a <see cref="JsonException"/>.
/// Owned because the platform has no converter that reads PHP's empty array as an absent object.
/// </summary>
/// <typeparam name="T">The object.</typeparam>
internal sealed class ZabbixObjectOrEmptyArrayConverter<T> : JsonConverter<T> where T : class
{
    /// <inheritdoc/>
    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.StartArray)
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray)
                throw new JsonException($"A non-empty array is not a {typeof(T).Name}: only [] stands for an empty object.");
            return null;
        }
        ArgumentNullException.ThrowIfNull(options);
        return (T?)JsonSerializer.Deserialize(ref reader, options.GetTypeInfo(typeof(T)));
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        JsonSerializer.Serialize(writer, value, options.GetTypeInfo(typeof(T)));
    }
}
