// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// A map color (<c>font_color</c>, <c>line_color</c>, a link's <c>color</c>...): a <see cref="ZabbixNamedValues.MapColors"/>
/// name becomes its hex; six hex digits pass through as written; anything else is refused at binding, so the server
/// never sees a color the frontend would refuse (the frontend's own color rule, <c>CApiInputValidator</c>'s
/// <c>API_COLOR</c>: <c>/^[0-9a-f]{6}$/i</c>; the map API itself only checks the length).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixMapColorAttribute : ArgumentTransformationAttribute
{
    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        object? input = inputData is PSObject wrapped ? wrapped.BaseObject : inputData;
        if (input is not string text) return input;
        string trimmed = text.Trim();
        if (ZabbixNamedValues.MapColors.TryGetValue(trimmed, out string? hex)) return hex;
        if (trimmed.Length == 6 && trimmed.All(char.IsAsciiHexDigit)) return trimmed;
        throw new ArgumentTransformationMetadataException(string.Create(CultureInfo.InvariantCulture,
            $"'{text}' is not a color: give six hex digits (RRGGBB) or one of {string.Join(", ", ZabbixNamedValues.Names(ZabbixNamedValues.MapColors))}."));
    }
}
