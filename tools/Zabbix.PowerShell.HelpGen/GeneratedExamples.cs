// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.HelpGen;

/// <summary>
/// The generator's examples as the live examples run reads them: one JSON array of every example a generated command's
/// doc comment carries, each with its command, title and code, and whether <c>Get-Help</c> shows it (a help script
/// replaces a command's generated examples, which the run still uses to set up the object the shown ones work on).
/// <c>tools/Test-ZbxHelpExamples.ps1</c> runs every shown one against a live server.
/// </summary>
internal static class GeneratedExamples
{
    private static readonly JsonSerializerOptions s_options = new()
    {
        WriteIndented = true,
        // The code is PowerShell: keep its quotes, pipes and angle brackets readable in the file.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The JSON text for <paramref name="generated"/>, in command order and each command's example order.</summary>
    /// <param name="generated">The generated commands, as their doc comments give them (before any help script).</param>
    /// <param name="scripted">The commands a help script documents, whose generated examples <c>Get-Help</c> does not show.</param>
    public static string Build(IEnumerable<CmdletDoc> generated, IReadOnlySet<string> scripted)
    {
        ArgumentNullException.ThrowIfNull(generated);
        ArgumentNullException.ThrowIfNull(scripted);
        List<Entry> entries =
        [
            .. generated.OrderBy(cmdlet => cmdlet.Name, StringComparer.Ordinal)
                .SelectMany(cmdlet => cmdlet.Examples.Select(example => new Entry(cmdlet.Name, example.Title, example.Code, !scripted.Contains(cmdlet.Name)))),
        ];
        return JsonSerializer.Serialize(entries, s_options) + "\n";
    }

    // Property names as the PowerShell reader spells them (ConvertFrom-Json is case-insensitive either way).
    private sealed record Entry(string Cmdlet, string Title, string Code, bool Shown);
}
