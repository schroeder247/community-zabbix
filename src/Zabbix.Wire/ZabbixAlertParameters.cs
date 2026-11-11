// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix;

/// <summary>
/// Represents a test alert's <c>parameters</c>, in the one of two shapes its media type reads. A script takes its
/// parameters as an ordered list of values, a webhook as an object of name to value (the frontend's <c>CControllerMediatypeTestSend</c>:
/// <c>array_column(…, 'value')</c> for <c>MEDIA_TYPE_EXEC</c>, <c>array_column(…, 'value', 'name')</c> for
/// <c>MEDIA_TYPE_WEBHOOK</c>). Two factories, so the wrong shape for a type cannot be written.
/// </summary>
public sealed class ZabbixAlertParameters
{
    private readonly IReadOnlyList<string>? _values;
    private readonly IReadOnlyList<KeyValuePair<string, string>>? _named;

    private ZabbixAlertParameters(IReadOnlyList<string>? values, IReadOnlyList<KeyValuePair<string, string>>? named)
    {
        _values = values;
        _named = named;
    }

    /// <summary>A script media type's parameters, in order.</summary>
    /// <param name="values">The values.</param>
    /// <returns>The parameters of a script media type.</returns>
    public static ZabbixAlertParameters Script(IEnumerable<string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new([.. values], null);
    }

    /// <summary>A webhook media type's parameters, by name.</summary>
    /// <param name="parameters">Name to value.</param>
    /// <returns>The parameters of a webhook media type.</returns>
    public static ZabbixAlertParameters Webhook(IEnumerable<KeyValuePair<string, string>> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        return new(null, [.. parameters]);
    }

    /// <summary>Whether these are a webhook's (by name) rather than a script's (in order).</summary>
    public bool IsWebhook => _named is not null;

    internal void WriteTo(Utf8JsonWriter writer)
    {
        if (_named is not null)
        {
            writer.WriteStartObject("parameters");
            foreach ((string name, string value) in _named) writer.WriteString(name, value);
            writer.WriteEndObject();
            return;
        }
        writer.WriteStartArray("parameters");
        foreach (string value in _values ?? []) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }
}
