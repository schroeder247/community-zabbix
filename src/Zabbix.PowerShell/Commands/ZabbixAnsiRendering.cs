// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// Provides plain-ANSI rendering for the module's live console view (<c>Show-ZbxWatch</c>): a table drawn with
/// <see cref="PSStyle"/>, not a TUI framework.
/// </summary>
/// <remarks>
/// Terminal.Gui is not used: its current release is net10.0-only and depends on several
/// <c>Microsoft.Extensions.*</c> packages, and a second copy of any <c>Microsoft.Extensions.*</c> assembly already
/// loaded at a different version throws <c>FileLoadException</c> on import (README, "Assembly isolation"). The view
/// needs no window management (no overlapping panes, no keyboard focus tree), and <see cref="PSStyle"/> is
/// already part of <c>System.Management.Automation</c>, so plain ANSI needs no isolation and no extra assemblies.
/// Revisit once the module has its own <c>AssemblyLoadContext</c> and a command genuinely needs a TUI.
/// </remarks>
internal static class ZabbixAnsiRendering
{
    /// <summary>A left-aligned, space-padded table with a dim header row.</summary>
    public static string Table(IReadOnlyList<string> headers, IReadOnlyList<string?[]> rows)
    {
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(rows);
        var widths = new int[headers.Count];
        for (int column = 0; column < headers.Count; column++)
        {
            int max = headers[column].Length;
            foreach (string?[] row in rows) max = Math.Max(max, row[column]?.Length ?? 0);
            widths[column] = max;
        }

        PSStyle style = PSStyle.Instance;
        var text = new StringBuilder();
        text.Append(style.Foreground.BrightBlack);
        for (int column = 0; column < headers.Count; column++) text.Append(headers[column].PadRight(widths[column] + 2, ' '));
        text.Append(style.Reset).Append('\n');
        foreach (string?[] row in rows)
        {
            for (int column = 0; column < headers.Count; column++) text.Append((row[column] ?? string.Empty).PadRight(widths[column] + 2, ' '));
            text.Append('\n');
        }
        return text.ToString();
    }
}
