// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// A Zabbix float: a JSON number, or the decimal text PHP writes for a database float (<c>"12.5000"</c>). Never NaN or
/// infinite: the server refuses both before storing a value (<c>zbx_validate_value_dbl</c>, <c>src/libs/zbxnum/num.c</c>),
/// so a reply that carries one (<c>"NaN"</c>, <c>"Infinity"</c>, a number past <see cref="double.MaxValue"/> such as
/// <c>1e400</c>) is not the API's and is a <see cref="JsonException"/>, not a value. The context's
/// <c>AllowReadingFromString</c> alone reads all three, so the platform's number handling cannot be used as it is.
/// </summary>
internal sealed class ZabbixFiniteDoubleConverter : JsonConverter<double>
{
    /// <inheritdoc/>
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        double value = reader.TokenType switch
        {
            JsonTokenType.Number when reader.TryGetDouble(out double number) => number,
            JsonTokenType.String when double.TryParse(reader.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) => parsed,
            _ => throw new JsonException($"A {reader.TokenType} is not a Zabbix float."),
        };
        return double.IsFinite(value) ? value : throw new JsonException($"{value.ToString(CultureInfo.InvariantCulture)} is not a Zabbix float: the server stores finite values only.");
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!double.IsFinite(value)) throw new JsonException($"{value.ToString(CultureInfo.InvariantCulture)} is not a Zabbix float: the server refuses what is not finite.");
        writer.WriteNumberValue(value);
    }
}
