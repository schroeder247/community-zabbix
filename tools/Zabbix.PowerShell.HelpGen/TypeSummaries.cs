// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Xml.Linq;

namespace Community.Zabbix.PowerShell.HelpGen;

/// <summary>
/// What a type a command takes or writes is, from the compiler's XML documentation files beside the module: the source
/// parser reads only the module's own sources, while the record a <c>Get-</c> writes is documented in
/// <c>Community.Zabbix.Api.xml</c>, so the help's INPUTS and OUTPUTS read it here. Keyed by the type's simple name, as
/// the help displays it (<c>ZabbixHost</c>, <c>ZabbixReference</c>); the first file that documents a name wins.
/// </summary>
internal static class TypeSummaries
{
    /// <summary>The types' one-line summaries, by simple name.</summary>
    /// <param name="files">The XML documentation files (<c>&lt;assembly&gt;.xml</c>).</param>
    public static Dictionary<string, string> Load(IEnumerable<string> files)
    {
        var summaries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string file in files.Order(StringComparer.Ordinal))
        {
            XDocument document;
            try { document = XDocument.Load(file); }
            catch (System.Xml.XmlException) { continue; }
            if (document.Root?.Name != "doc") continue;
            foreach (XElement member in document.Descendants("member"))
            {
                string? id = member.Attribute("name")?.Value;
                if (id is null || !id.StartsWith("T:", StringComparison.Ordinal)) continue;
                string name = id[(id.LastIndexOf('.') + 1)..];
                int arity = name.IndexOf('`', StringComparison.Ordinal);
                if (arity >= 0) name = name[..arity];
                if (summaries.ContainsKey(name) || member.Element("summary") is not { } summary) continue;
                string text = CommandSourceParser.PlainText(string.Concat(summary.Nodes()));
                if (text.Length > 0) summaries[name] = FirstSentence(text);
            }
        }
        return summaries;
    }

    /// <summary>The summary of the type <paramref name="displayType"/> names (<c>ZabbixReference&lt;ZabbixHost&gt;[]</c> → <c>ZabbixReference</c>'s).</summary>
    /// <param name="summaries">The loaded summaries.</param>
    /// <param name="displayType">The type as the help displays it.</param>
    public static string? Of(IReadOnlyDictionary<string, string> summaries, string displayType)
    {
        string name = displayType;
        int generic = name.IndexOfAny(['<', '[']);
        if (generic >= 0) name = name[..generic];
        return summaries.TryGetValue(name, out string? summary) ? summary : BuiltIn(name);
    }

    // The engine's and the runtime's own types a command takes or writes, which no file here documents.
    private static string? BuiltIn(string name) => name switch
    {
        "string" => "Text.",
        "long" or "int" => "A number.",
        "bool" => "True or false.",
        "object" or "PSObject" => "Each object as the API answers it.",
        "Hashtable" => "A hashtable.",
        "ScriptBlock" => "A script block.",
        "Version" => "A version number.",
        "SwitchParameter" => "A switch.",
        _ => null,
    };

    // The first sentence, without its parentheticals: a type's summary is written for the code's readers, and what it
    // puts in parentheses is theirs (an upstream citation, a design reason, a measured case).
    private static string FirstSentence(string text)
    {
        string plain = text;
        for (string previous = string.Empty; previous != plain;)
        {
            previous = plain;
            plain = System.Text.RegularExpressions.Regex.Replace(plain, @"\s*\([^()]*\)", string.Empty);
        }
        int end = plain.IndexOf(". ", StringComparison.Ordinal);
        string sentence = (end > 0 ? plain[..(end + 1)] : plain).Trim();
        return sentence.EndsWith('.') ? sentence : sentence + ".";
    }
}
