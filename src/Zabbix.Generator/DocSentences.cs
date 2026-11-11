// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text;
using System.Text.RegularExpressions;

namespace Community.Zabbix.Generator;

/// <summary>
/// The sentence a member is documented with when the description has none of its own: the description carries a
/// sentence written for it where the overlay has one, and otherwise only the extractor's mechanical fallback
/// (<c>Passwd check rules. Wire field passwd_check_rules.</c>), a define (<c>&lt;c&gt;HOST_ENCRYPTION_PSK&lt;/c&gt;.</c>) or a
/// family (<c>&lt;c&gt;PASSWD_CHECK_*&lt;/c&gt; (&lt;c&gt;defines.inc.php&lt;/c&gt;).</c>). Those read as a sentence made
/// of the member's own C# words with the wire or define beside it: <c>The password check rules
/// (&lt;c&gt;passwd_check_rules&lt;/c&gt;).</c> <see cref="IsFallback(string?)"/> tells the two apart, for the ratchet
/// <c>GeneratedApiTests</c> keeps on how many members still lack a sentence of their own.
/// </summary>
internal static class DocSentences
{
    private static readonly Regex s_mechanicalField = new(@"^([^.]*\. )?Wire field (<c>)?[^ <]+(</c>)?\.$", RegexOptions.CultureInvariant);

    private static readonly Regex s_defineOnly = new(@"^<c>[A-Z0-9_]+</c>\.$", RegexOptions.CultureInvariant);

    private static readonly Regex s_familyOnly = new(@"^<c>[A-Z0-9_:]+_\*</c> \(<c>defines\.inc\.php</c>\)\.$", RegexOptions.CultureInvariant);

    /// <summary>Whether <paramref name="summary"/> is absent or one of the extractor's mechanical fallbacks.</summary>
    /// <param name="summary">The description's sentence.</param>
    public static bool IsFallback(string? summary)
        => string.IsNullOrWhiteSpace(summary) || s_mechanicalField.IsMatch(summary!) || s_defineOnly.IsMatch(summary!) || s_familyOnly.IsMatch(summary!);

    /// <summary>A field's documentation: its own sentence, or <c>The {words} (&lt;c&gt;{wire}&lt;/c&gt;).</c></summary>
    /// <param name="summary">The description's sentence.</param>
    /// <param name="member">The C# member name.</param>
    /// <param name="wire">The wire name.</param>
    public static string Field(string? summary, string member, string wire)
    {
        string generated = "The " + Words(member) + " (<c>" + wire + "</c>).";
        if (IsFallback(summary)) return generated;
        // a sentence of its own that still carries the mechanical "Wire field x." (a default appended to it): the phrase goes
        string rest = s_wirePhrase.Replace(summary!, string.Empty).Trim();
        return rest.Length == 0 ? generated : rest.StartsWith("Left out", StringComparison.Ordinal) ? generated + " " + rest : rest;
    }

    private static readonly Regex s_wirePhrase = new(@"Wire field (<c>)?[^ <]+(</c>)?( on [^.]*)?\.\s*", RegexOptions.CultureInvariant);

    /// <summary>An enum member's documentation: its own sentence, or <c>{Words} (&lt;c&gt;{DEFINE}&lt;/c&gt;).</c></summary>
    /// <param name="summary">The description's sentence.</param>
    /// <param name="member">The C# member name.</param>
    /// <param name="define">The define, when the source names one.</param>
    /// <param name="value">The wire value.</param>
    public static string Member(string? summary, string member, string? define, long value)
    {
        if (!IsFallback(summary)) return summary!;
        string words = Words(member);
        string sentence = char.ToUpperInvariant(words[0]) + words.Substring(1);
        return sentence + " (" + (define is not null ? "<c>" + define + "</c>, " : string.Empty) + value.ToString(System.Globalization.CultureInfo.InvariantCulture) + " on the wire).";
    }

    /// <summary>An enum's documentation: its own sentence, or <c>The {words} values: &lt;c&gt;{FAMILY}_*&lt;/c&gt; in &lt;c&gt;defines.inc.php&lt;/c&gt;.</c></summary>
    /// <param name="summary">The description's sentence.</param>
    /// <param name="name">The enum's C# name, without its <c>Zabbix</c> prefix.</param>
    /// <param name="family">The define family.</param>
    /// <param name="flags">Whether the values combine.</param>
    public static string Enum(string? summary, string name, string family, bool flags)
        => IsFallback(summary)
            ? (flags ? "A set of " : "The ") + Words(name) + (flags ? " bits" : " values") + ": <c>" + family + "_*</c> in <c>defines.inc.php</c>."
            : summary!;

    /// <summary><c>PasswordCheckRules</c> → <c>password check rules</c>, <c>HostId</c> → <c>host ID</c>, <c>TlsPsk</c> → <c>TLS PSK</c>.</summary>
    /// <param name="pascal">A C# name.</param>
    public static string Words(string pascal)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        foreach (char c in pascal.TrimStart('@'))
        {
            if ((char.IsUpper(c) || (char.IsDigit(c) && word.Length > 0 && !char.IsDigit(word[word.Length - 1]))) && word.Length > 0)
            {
                words.Add(word.ToString());
                word.Clear();
            }
            word.Append(c);
        }
        if (word.Length > 0) words.Add(word.ToString());
        return string.Join(" ", words.Select(Render));
    }

    private static readonly HashSet<string> s_acronyms = new(StringComparer.Ordinal)
    {
        "Id", "Ids", "Tls", "Psk", "Ip", "Dns", "Url", "Uri", "Api", "Http", "Https", "Snmp", "Ssl", "Ldap", "Saml", "Mfa", "Jmx", "Ipmi", "Oid", "Lld", "Sla", "Sli", "Gui", "Smtp", "Json", "Xml", "Ssh",
    };

    private static string Render(string word)
        => s_acronyms.Contains(word) ? (word == "Ids" ? "IDs" : word.ToUpperInvariant()) : ToLowerAscii(word);

    private static string ToLowerAscii(string text) => new([.. text.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c)]);
}
