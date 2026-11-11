// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Represents a <c>-Name</c> as the shell writes it, mated to what Zabbix can search. PowerShell's
/// own <see cref="WildcardPattern"/> is the meaning (Learn, "Supporting Wildcard Characters in Cmdlet Parameters":
/// <c>*</c>, <c>?</c>, backtick escapes, case-insensitive), with one deliberate difference: <c>[</c> and <c>]</c> are
/// literal, because Zabbix names routinely carry them (item keys, <c>Proxy [name]</c> triggers) and a character set
/// would make such a name unmatchable without escaping. Zabbix's <c>search</c> with <c>searchWildcardsEnabled</c>
/// (<c>zbx_db_search</c>, <c>ui/include/db.inc.php</c>: <c>UPPER(field) LIKE UPPER(pattern) ESCAPE '!'</c>, <c>*</c> →
/// <c>%</c>, <c>%</c>/<c>_</c>/<c>!</c> escaped) knows only <c>*</c> and is case-insensitive, so <see cref="Server"/> is
/// that pattern — exact and case-insensitive for a name with no wildcard — and a <c>?</c> or an escaped <c>`*</c>,
/// which it cannot say, widens to <c>*</c> there and is narrowed by <see cref="IsMatch"/> here (<see cref="Precise"/>
/// is then <see langword="false"/>).
/// </summary>
internal sealed class ZabbixNamePattern
{
    private readonly WildcardPattern _match;

    private ZabbixNamePattern(string server, bool precise, bool wildcard, string longestLiteral, WildcardPattern match)
    {
        Server = server;
        Precise = precise;
        HasWildcard = wildcard;
        LongestLiteral = longestLiteral;
        _match = match;
    }

    /// <summary>The pattern for Zabbix's <c>search</c> with <c>searchWildcardsEnabled</c>: literal text and <c>*</c>.</summary>
    public string Server { get; }

    /// <summary>Whether <see cref="Server"/> alone selects exactly what the written pattern means.</summary>
    public bool Precise { get; }

    /// <summary>Whether the written name holds an unescaped <c>*</c> or <c>?</c>: a pattern, not one name.</summary>
    public bool HasWildcard { get; }

    /// <summary>The longest run of literal text: a substring every match contains (for a <c>.get</c> without <c>searchWildcardsEnabled</c>).</summary>
    public string LongestLiteral { get; }

    /// <summary>Whether <paramref name="name"/> matches, as PowerShell would match it (brackets literal).</summary>
    /// <param name="name">A name read off an object.</param>
    public bool IsMatch(string? name) => name is not null && _match.IsMatch(name);

    /// <summary>Parses a written name.</summary>
    /// <param name="written">The name as the shell wrote it.</param>
    public static ZabbixNamePattern Parse(string written)
    {
        ArgumentNullException.ThrowIfNull(written);
        var server = new StringBuilder(written.Length);
        var match = new StringBuilder(written.Length + 4);
        var literal = new StringBuilder();
        string longest = string.Empty;
        bool precise = true;
        bool wildcard = false;

        void Wild(bool exactOnServer)
        {
            if (literal.Length > longest.Length) longest = literal.ToString();
            literal.Clear();
            if (server.Length == 0 || server[^1] != '*') server.Append('*');
            precise &= exactOnServer;
        }

        for (int i = 0; i < written.Length; i++)
        {
            char c = written[i];
            if (c == '`' && i + 1 < written.Length)
            {
                char escaped = written[++i];
                match.Append('`').Append(escaped);
                if (escaped == '*')
                {
                    Wild(exactOnServer: false);
                    continue;
                }
                server.Append(escaped);
                literal.Append(escaped);
                continue;
            }
            switch (c)
            {
                case '*':
                    match.Append('*');
                    Wild(exactOnServer: true);
                    wildcard = true;
                    break;
                case '?':
                    match.Append('?');
                    Wild(exactOnServer: false);
                    wildcard = true;
                    break;
                default:
                    // Brackets are literal (the type's summary), so WildcardPattern gets them escaped.
                    if (c is '[' or ']') match.Append('`');
                    match.Append(c);
                    server.Append(c);
                    literal.Append(c);
                    break;
            }
        }
        if (literal.Length > longest.Length) longest = literal.ToString();
        return new ZabbixNamePattern(server.ToString(), precise, wildcard, longest, new WildcardPattern(match.ToString(), WildcardOptions.IgnoreCase | WildcardOptions.CultureInvariant));
    }
}
