// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Management.Automation.Language;

namespace Community.Zabbix.PowerShell;

/// <summary>Completion's bounds and quoting.</summary>
internal static class ZabbixCompletion
{
    /// <summary>How long a server's names stay cached.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(1);

    /// <summary>How long completion waits for the server.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(3);

    /// <summary>The most names one completion asks the server for (a list longer than this is not read at a prompt).</summary>
    public const int Limit = 100;

    /// <summary>A completion text that PowerShell parses back as the same single argument.</summary>
    /// <param name="value">The value.</param>
    public static string Quote(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length > 0 && value.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.')
            ? value
            : string.Create(CultureInfo.InvariantCulture, $"'{CodeGeneration.EscapeSingleQuotedStringContent(value)}'");
    }
}
