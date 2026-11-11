// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// The document formats <c>configuration.export</c>/<c>import</c>/<c>importcompare</c> take (<c>format</c>; <c>raw</c>
/// is export-only and not offered). An enum, not a <c>[ValidateSet]</c> string: PowerShell binds <c>Json</c>,
/// <c>json</c> and <c>JSON</c> to the same member, while the API accepts only the lowercase wire value
/// (a case-insensitive <c>ValidateSet</c> passes <c>Json</c> through verbatim, and the server refuses it).
/// </summary>
public enum ZabbixConfigurationFormat
{
    /// <summary><c>json</c>.</summary>
    Json,

    /// <summary><c>xml</c>.</summary>
    Xml,

    /// <summary><c>yaml</c>.</summary>
    Yaml,
}

/// <summary>The wire value of a <see cref="ZabbixConfigurationFormat"/>.</summary>
internal static class ZabbixConfigurationFormatWire
{
    public static string ToWire(this ZabbixConfigurationFormat format) => format switch
    {
        ZabbixConfigurationFormat.Json => "json",
        ZabbixConfigurationFormat.Xml => "xml",
        ZabbixConfigurationFormat.Yaml => "yaml",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "not a configuration format"),
    };

    /// <summary>The format a document file's extension names (<c>.json</c>, <c>.xml</c>, <c>.yaml</c> or <c>.yml</c>, any case); <see langword="null"/> for any other.</summary>
    /// <param name="path">The file.</param>
    public static ZabbixConfigurationFormat? OfExtension(string path) => System.IO.Path.GetExtension(path).ToUpperInvariant() switch
    {
        ".JSON" => ZabbixConfigurationFormat.Json,
        ".XML" => ZabbixConfigurationFormat.Xml,
        ".YAML" or ".YML" => ZabbixConfigurationFormat.Yaml,
        _ => null,
    };

    /// <summary>
    /// The format of the document file at <paramref name="path"/>: <c>-Format</c> when given, else the one its extension
    /// names, else <see langword="null"/> (the command's own fallback). <c>-Format</c> and an extension that names another
    /// format are refused rather than either winning silently: <c>-Path x.yaml -Format Json</c> would write JSON into a
    /// file every reader takes for YAML.
    /// </summary>
    /// <param name="given">The command's <c>-Format</c>, when bound.</param>
    /// <param name="path">The file.</param>
    /// <param name="format">The format, when one is named.</param>
    /// <returns>The refusal, or <see langword="null"/>.</returns>
    public static ErrorRecord? Resolve(ZabbixConfigurationFormat? given, string path, out ZabbixConfigurationFormat? format)
    {
        ZabbixConfigurationFormat? named = OfExtension(path);
        format = given ?? named;
        if (given is not { } written || named is not { } extension || written == extension) return null;
        return new ErrorRecord(
            new PSArgumentException($"-Format {written} disagrees with the extension of '{path}', which names {extension}: leave -Format out to use {extension}, or name the file .{written.ToWire()}."),
            "ZabbixConfigurationFormatMismatch", ErrorCategory.InvalidArgument, path);
    }
}
