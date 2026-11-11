// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Normalizes an LLD macro's name as the shell writes it, the counterpart of <see cref="ZabbixUserMacroNameAttribute"/>:
/// <c>-Macro ifname</c>, <c>-Macro IFNAME</c>, <c>-Macro #IFNAME</c> and <c>-Macro {#ifname}</c> all become
/// <c>{#IFNAME}</c>.
/// Zabbix LLD macro names are <c>A-Z0-9_.</c>, upper case only (<c>API_LLD_MACRO</c>, <c>CDiscoveryRule.php:1187,1200</c>), so the
/// name is upper-cased. A value in braces that is not an LLD macro (<c>{$USER}</c>, an expression) passes through untouched.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixLldMacroNameAttribute : ArgumentTransformationAttribute
{
    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        object? input = inputData is PSObject wrapped ? wrapped.BaseObject : inputData;
        return input is string text ? Normalize(text) : input;
    }

    /// <summary><paramref name="macro"/> in <c>{#NAME}</c> form.</summary>
    /// <param name="macro">The name as written.</param>
    public static string Normalize(string macro)
    {
        ArgumentNullException.ThrowIfNull(macro);
        string trimmed = macro.Trim();
        if (trimmed.Length == 0) return trimmed;
        if (trimmed.StartsWith('{'))
            return trimmed.StartsWith("{#", StringComparison.Ordinal) && trimmed.EndsWith('}') ? "{#" + trimmed[2..^1].ToUpperInvariant() + "}" : trimmed;
        string bare = trimmed.StartsWith('#') ? trimmed[1..] : trimmed;
        return "{#" + bare.ToUpperInvariant() + "}";
    }
}
