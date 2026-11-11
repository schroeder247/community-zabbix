// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Security;
using Microsoft.CodeAnalysis.CSharp;

namespace Community.Zabbix.Generator;

/// <summary>
/// How a value from the description is written into C#: a string as a literal (<see cref="SymbolDisplay.FormatLiteral(string, bool)"/>,
/// so a quote, a backslash or a control character cannot end it), a name as an identifier (<c>@</c> before a keyword),
/// a sentence as XML documentation (escaped unless it is the description's own trusted markup) or as a plain comment.
/// The one place every emitter writes description text through.
/// </summary>
internal static class CodeText
{
    /// <summary><paramref name="value"/> as a quoted C# string literal.</summary>
    /// <param name="value">The text.</param>
    public static string Literal(string value) => SymbolDisplay.FormatLiteral(value, quote: true);

    /// <summary><paramref name="name"/> as a C# identifier: <c>@</c> before a keyword (the description's names are
    /// checked to be identifiers, <see cref="DescriptionValidator"/>).</summary>
    /// <param name="name">The name.</param>
    public static string Identifier(string name)
        => SyntaxFacts.GetKeywordKind(name) != SyntaxKind.None ? "@" + name : name;

    /// <summary>
    /// A sentence for an XML doc comment: on one line; the description's own <c>&lt;c&gt;</c>, <c>&lt;b&gt;</c> and
    /// <c>&lt;see cref="…"/&gt;</c> kept, anything else escaped; <paramref name="fallback"/> when there is none.
    /// </summary>
    /// <param name="summary">The description's sentence.</param>
    /// <param name="fallback">The sentence to write when there is none.</param>
    public static string Doc(string? summary, string fallback)
    {
        string text = (string.IsNullOrWhiteSpace(summary) ? fallback : summary!).Replace('\r', ' ').Replace('\n', ' ').Trim();
        return IsTrustedXml(text) ? text : SecurityElement.Escape(text) ?? fallback;
    }

    /// <summary>A sentence for a <c>//</c> comment: on one line, its markup reduced to text.</summary>
    /// <param name="text">The sentence, possibly with doc markup.</param>
    public static string Plain(string text)
    {
        string plain = text.Replace('\r', ' ').Replace('\n', ' ').Replace("<c>", string.Empty).Replace("</c>", string.Empty)
            .Replace("<b>", string.Empty).Replace("</b>", string.Empty);
        int see;
        while ((see = plain.IndexOf("<see cref=\"", StringComparison.Ordinal)) >= 0)
        {
            int end = plain.IndexOf("\"/>", see, StringComparison.Ordinal);
            if (end < 0) break;
            string cref = plain.Substring(see + 11, end - see - 11);
            plain = plain.Remove(see, end + 3 - see).Insert(see, cref.Substring(cref.LastIndexOf('.') + 1));
        }
        return plain.Trim();
    }

    private static bool IsTrustedXml(string text)
    {
        string stripped = text.Replace("<c>", string.Empty).Replace("</c>", string.Empty).Replace("<b>", string.Empty).Replace("</b>", string.Empty);
        int see;
        while ((see = stripped.IndexOf("<see cref=\"", StringComparison.Ordinal)) >= 0)
        {
            int end = stripped.IndexOf("\"/>", see, StringComparison.Ordinal);
            if (end < 0) return false;
            stripped = stripped.Remove(see, end + 3 - see);
        }
        return stripped.IndexOf('<') < 0 && stripped.IndexOf('>') < 0 && stripped.IndexOf('&') < 0;
    }
}
