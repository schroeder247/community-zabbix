// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// A <c>.get</c> <c>sortorder</c> word as the BCL's <see cref="ListSortDirection"/>: the API writes <c>ASC</c> and
/// <c>DESC</c> (<c>ZBX_SORT_UP</c>, <c>ZBX_SORT_DOWN</c>) and reads them without regard to case. The platform's
/// <see cref="JsonStringEnumConverter{TEnum}"/> would write the member names, which the API refuses.
/// </summary>
internal sealed class ZabbixSortDirectionConverter : JsonConverter<ListSortDirection>
{
    /// <inheritdoc/>
    public override ListSortDirection Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && reader.GetString() is { } word
            ? word.Equals("ASC", StringComparison.OrdinalIgnoreCase) ? ListSortDirection.Ascending
            : word.Equals("DESC", StringComparison.OrdinalIgnoreCase) ? ListSortDirection.Descending
            : throw new JsonException($"The sort order {word} is neither ASC nor DESC.")
            : throw new JsonException($"A {reader.TokenType} is not a sort order.");

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ListSortDirection value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value switch
        {
            ListSortDirection.Ascending => "ASC",
            ListSortDirection.Descending => "DESC",
            _ => throw new JsonException($"{value} is not a sort direction."),
        });
    }
}
