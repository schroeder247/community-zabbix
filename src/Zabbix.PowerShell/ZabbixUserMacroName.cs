// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Normalizes a user macro's name as the shell writes it: <c>-Macro NAME</c> and <c>-Macro $NAME</c> become
/// <c>{$NAME}</c>, the only form <c>usermacro.create</c> accepts (it answers <c>incorrect syntax near "NAME"</c>
/// otherwise). The name part is upper-cased
/// (Zabbix macro names are <c>A-Z0-9_.</c>), a context after the first <c>:</c> is kept as written
/// (<c>NAME:"ctx"</c> → <c>{$NAME:"ctx"}</c>), and a value already in braces (<c>{$NAME}</c>,
/// <c>{$NAME:"ctx"}</c>) passes through untouched.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixUserMacroNameAttribute : ArgumentTransformationAttribute
{
    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        object? input = inputData is PSObject wrapped ? wrapped.BaseObject : inputData;
        return input switch
        {
            string text => Normalize(text),
            // Get-ZbxUserMacro -Name takes several: each element is a name as written.
            object?[] several when several.All(e => (e is PSObject p ? p.BaseObject : e) is string) =>
                several.Select(e => Normalize((string)(e is PSObject p ? p.BaseObject : e)!)).ToArray(),
            _ => input,
        };
    }

    /// <summary><paramref name="macro"/> in <c>{$NAME}</c> form.</summary>
    /// <param name="macro">The name as written.</param>
    public static string Normalize(string macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        string trimmed = macro.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('{')) return trimmed;
        string bare = trimmed.StartsWith('$') ? trimmed[1..] : trimmed;
        int colon = bare.IndexOf(':', StringComparison.Ordinal);
        string name = colon < 0 ? bare : bare[..colon];
        string context = colon < 0 ? string.Empty : bare[colon..];
        return "{$" + name.ToUpperInvariant() + context + "}";
    }
}
