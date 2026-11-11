// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// A <c>version</c> tag as a <see cref="Version"/>: read as Zabbix reads it (<c>zbx_get_component_version</c>, so
/// <c>8.0.0rc1</c> is 8.0.0, which <see cref="Version.Parse(string)"/> refuses), and written as the protocol writes it,
/// <c>major.minor.patch</c>. A tag that isn't a version reads as <see langword="null"/>: a reply's version only informs.
/// </summary>
internal sealed class ZabbixVersionTextConverter : JsonConverter<Version>
{
    /// <inheritdoc/>
    public override Version? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && ZabbixComponentVersion.TryParse(reader.GetString(), CultureInfo.InvariantCulture, out ZabbixComponentVersion version)
            ? version.ToVersion()
            : null;

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, Version value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStringValue(Text(value));
    }

    /// <summary>A version as the protocol writes it: <c>major.minor.patch</c>, the patch 0 when <paramref name="version"/> has none.</summary>
    /// <param name="version">The version.</param>
    /// <returns>The text.</returns>
    internal static string Text(Version version) => ZabbixComponentVersion.FromVersion(version).ToString();
}
