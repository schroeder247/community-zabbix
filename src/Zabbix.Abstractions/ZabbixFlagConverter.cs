// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>A flag the API stores as an integer: read in any form, written as <c>"0"</c>/<c>"1"</c> (an object field is validated as an integer, never as a JSON boolean). Owned because the platform writes a boolean as JSON <c>true</c>/<c>false</c>, which the API refuses there.</summary>
internal sealed class ZabbixFlagConverter : JsonConverter<bool?>
{
    /// <inheritdoc/>
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => ZabbixBoolConverter.ReadBool(ref reader);

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value.Value ? "1" : "0");
    }
}
