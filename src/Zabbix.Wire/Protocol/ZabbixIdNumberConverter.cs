// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix.Protocol;

/// <summary>
/// An id as the framed protocol writes one: a JSON number (<c>zbx_json_adduint64</c>), where the API writes the decimal
/// string <see cref="ulong"/>'s own converter writes. A protocol context registers it in its options, which take
/// precedence over the type's converter. Reads either form. Owned because the platform has no converter for a value type
/// of the SDK's own.
/// </summary>
internal sealed class ZabbixIdNumberConverter : JsonConverter<ulong>
{
    /// <inheritdoc/>
    public override ulong Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.Number when reader.TryGetUInt64(out ulong id) => id,
        JsonTokenType.String when ulong.TryParse(reader.GetString(), null, out ulong id) => id,
        _ => throw new JsonException($"A {reader.TokenType} is not a Zabbix id: an id is a decimal number such as 10084."),
    };

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ulong value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteNumberValue(value);
    }
}
