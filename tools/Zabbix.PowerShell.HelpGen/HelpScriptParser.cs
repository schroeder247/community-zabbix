// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.RegularExpressions;

namespace Community.Zabbix.PowerShell.HelpGen;

/// <summary>
/// Reads a cmdlet's help script, <c>Examples/&lt;Verb-Noun&gt;.ps1</c>: PowerShell's own comment-based help block
/// (<c>&lt;# .SYNOPSIS … .DESCRIPTION … #&gt;</c>) ahead of the first example, whose two sections replace the cmdlet
/// class's paragraphs, and one example per <c>#region &lt;title&gt;</c> … <c>#endregion</c> block, the block's leading
/// <c>#</c> lines being the remarks and the rest the code. The file is an ordinary PowerShell script, so the same text
/// that becomes the help is what <c>tools/Test-ZbxHelpExamples.ps1</c> runs against a live server: an example that
/// stops working fails there, not in a reader's console.
/// </summary>
internal static partial class HelpScriptParser
{
    [GeneratedRegex(@"^\s*#region\s+(?<title>\S.*?)\s*$")]
    private static partial Regex RegionStart();

    [GeneratedRegex(@"^\s*#endregion\b")]
    private static partial Regex RegionEnd();

    [GeneratedRegex(@"^\s*\.(?<keyword>[A-Za-z]+)\s*$")]
    private static partial Regex Keyword();

    /// <summary>The help in <paramref name="text"/>; <paramref name="errors"/> collects every malformed part (a region with
    /// no code, never closed or nested; a help block section other than <c>.SYNOPSIS</c> and <c>.DESCRIPTION</c>) so
    /// the build can refuse it.</summary>
    public static HelpScript Parse(string text, List<string> errors)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(errors);
        string[] lines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        (string? synopsis, string? description) = HelpBlock(lines, errors);
        return new HelpScript(synopsis, description, Examples(lines, errors));
    }

    // The <# ... #> block ahead of the first #region: .SYNOPSIS and .DESCRIPTION, each one paragraph (its lines joined).
    // Examples are regions, never .EXAMPLE, so the help and the live run read the same code.
    private static (string? Synopsis, string? Description) HelpBlock(string[] lines, List<string> errors)
    {
        int start = Array.FindIndex(lines, line => line.TrimStart().StartsWith("<#", StringComparison.Ordinal) || RegionStart().IsMatch(line));
        if (start < 0 || RegionStart().IsMatch(lines[start])) return (null, null);
        int end = Array.FindIndex(lines, start, line => line.TrimEnd().EndsWith("#>", StringComparison.Ordinal));
        if (end < 0)
        {
            errors.Add($"line {start + 1}: the help block is never closed");
            return (null, null);
        }

        var sections = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        List<string>? current = null;
        for (int i = start; i <= end; i++)
        {
            string line = lines[i].Trim();
            if (i == start) line = line[2..].TrimStart();
            if (i == end) line = line[..^2].TrimEnd();
            if (Keyword().Match(line) is { Success: true } keyword)
            {
                string name = keyword.Groups["keyword"].Value.ToUpperInvariant();
                if (name is not ("SYNOPSIS" or "DESCRIPTION"))
                {
                    errors.Add($"line {i + 1}: .{name} is not read; the help block takes .SYNOPSIS and .DESCRIPTION, and an example is a #region");
                    current = null;
                    continue;
                }
                if (sections.ContainsKey(name)) errors.Add($"line {i + 1}: .{name} is given twice");
                sections[name] = current = [];
                continue;
            }
            if (line.Length > 0)
            {
                if (current is null) errors.Add($"line {i + 1}: text outside a .SYNOPSIS or .DESCRIPTION section");
                else current.Add(line);
            }
        }
        return (Paragraph(sections, "SYNOPSIS", errors), Paragraph(sections, "DESCRIPTION", errors));
    }

    private static string? Paragraph(Dictionary<string, List<string>> sections, string name, List<string> errors)
    {
        if (!sections.TryGetValue(name, out List<string>? lines)) return null;
        if (lines.Count == 0) errors.Add($".{name} is empty");
        return lines.Count == 0 ? null : string.Join(' ', lines);
    }

    private static List<ExampleDoc> Examples(string[] lines, List<string> errors)
    {
        List<ExampleDoc> examples = [];
        string? title = null;
        List<string> body = [];
        int lineNumber = 0;
        foreach (string raw in lines)
        {
            lineNumber++;
            string line = raw.TrimEnd();
            if (RegionStart().Match(line) is { Success: true } start)
            {
                if (title is not null) errors.Add($"line {lineNumber}: #region inside the region '{title}'");
                title = start.Groups["title"].Value;
                body.Clear();
            }
            else if (RegionEnd().IsMatch(line))
            {
                if (title is null) { errors.Add($"line {lineNumber}: #endregion without a #region"); continue; }
                ExampleDoc? example = Build(title, body);
                if (example is null) errors.Add($"line {lineNumber}: the region '{title}' has no code");
                else examples.Add(example);
                title = null;
            }
            else if (title is not null)
            {
                body.Add(line);
            }
        }
        if (title is not null) errors.Add($"the region '{title}' is never closed");
        return examples;
    }

    private static ExampleDoc? Build(string title, List<string> body)
    {
        // The leading comment lines are the remarks: a comment wrapped over several lines is one paragraph, and a bare
        // '#' line starts the next one.
        int first = 0;
        List<string> paragraphs = [];
        var paragraph = new List<string>();
        while (first < body.Count && (body[first].TrimStart().StartsWith('#') || body[first].Trim().Length == 0))
        {
            string trimmed = body[first].Trim();
            string text = trimmed.TrimStart('#').Trim();
            if (text.Length > 0) paragraph.Add(text);
            else if (trimmed.Length > 0 && paragraph.Count > 0) { paragraphs.Add(string.Join(' ', paragraph)); paragraph.Clear(); }
            first++;
        }
        if (paragraph.Count > 0) paragraphs.Add(string.Join(' ', paragraph));
        List<string> code = [.. body.Skip(first)];
        while (code.Count > 0 && code[^1].Trim().Length == 0) code.RemoveAt(code.Count - 1);
        if (code.Count == 0) return null;
        return new ExampleDoc(title, string.Join('\n', code), string.Join('\n', paragraphs));
    }
}
