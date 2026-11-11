// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Globalization;

namespace Community.Zabbix;

/// <summary>
/// Represents an agent's item key access rules, Zabbix's <c>AllowKey</c> and <c>DenyKey</c>: which keys the agent
/// answers. A key a rule denies is answered as not supported ("This agent does not answer the key."), as a key nobody answers is.
/// </summary>
/// <remarks>
/// <para>Each rule is one line of Zabbix's agent configuration, <c>AllowKey=&lt;pattern&gt;</c> or
/// <c>DenyKey=&lt;pattern&gt;</c>. A pattern is one item key in which <c>*</c> matches any characters: in the name
/// (<c>vfs.file.*</c>) or in a parameter (<c>vfs.file.contents[*passwd*]</c>). The rules are tried in order and the first
/// that matches decides; a key no rule matches is allowed.</para>
/// <para>The matching is Zabbix's own (<c>zbx_check_request_access_rules</c>, <c>src/libs/zbxsysinfo/sysinfo.c</c>):
/// <c>key</c> matches the key without brackets only, <c>key[]</c> the key with empty brackets only, and a final
/// <c>*</c> parameter matches the parameters that remain, none included, so <c>key[a,*]</c> matches <c>key[a]</c> and
/// <c>key[a,b,c]</c>, and <c>key[*]</c> every parameter list but not the bare key; the pattern <c>*</c> alone matches
/// every key. As Zabbix's agents do, the rules end with an implicit
/// <c>DenyKey=system.run[*]</c> unless they name <c>system.run[*]</c> themselves, rules after a match-all <c>*</c> and
/// trailing <c>AllowKey</c> rules (which change nothing) are dropped, and a rule equal to an earlier one is ignored.</para>
/// </remarks>
public sealed class ZabbixKeyRules
{
    private const string AllowPrefix = "AllowKey=";
    private const string DenyPrefix = "DenyKey=";

    private readonly ImmutableArray<Rule> _rules;

    private ZabbixKeyRules(ImmutableArray<Rule> rules) => _rules = rules;

    /// <summary>The rules with none configured: every key is allowed but <c>system.run</c>, as in Zabbix's agents.</summary>
    public static ZabbixKeyRules Default { get; } = Parse([]);

    /// <summary>The effective rules, in the order they are tried, as configuration lines (<c>DenyKey=system.run[*]</c>).</summary>
    public IReadOnlyList<string> Rules => [.. _rules.Select(rule => rule.ToString())];

    /// <summary>Reads rules from Zabbix's configuration lines, in order.</summary>
    /// <param name="rules">Each <c>AllowKey=&lt;pattern&gt;</c> or <c>DenyKey=&lt;pattern&gt;</c>.</param>
    /// <returns>The rules.</returns>
    /// <exception cref="FormatException">
    /// A line is neither form, its pattern is not one item key, or the rules allow every key (Zabbix's agent refuses to
    /// start on "Item key access rules are configured to match all keys").
    /// </exception>
    public static ZabbixKeyRules Parse(IEnumerable<string> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var parsed = new List<Rule>();
        foreach (string line in rules)
        {
            Rule rule = Rule.Parse(line);
            if (!parsed.Any(earlier => earlier.SamePattern(rule))) parsed.Add(rule);   // zbx_add_key_access_rule
        }
        return new ZabbixKeyRules(Finalize(parsed));
    }

    /// <summary>Whether the rules allow <paramref name="key"/>.</summary>
    /// <param name="key">The key asked for, its parameters included.</param>
    /// <returns><see langword="true"/> unless the first rule that matches denies it.</returns>
    public bool Allows(ZabbixItemKey key)
    {
        IReadOnlyList<string> parameters = key.Parameters;                              // none without brackets, one empty in key[]
        int count = parameters.Count;
        // A key followed by empty brackets is not the same as one with none.
        bool emptyArguments = count == 1 && parameters[0].Length == 0;
        foreach (Rule rule in _rules)
        {
            if (rule.Matches(key.Name, parameters, count, emptyArguments)) return rule.Allow;
        }
        return true;
    }

    /// <inheritdoc/>
    public override string ToString() => string.Join(Environment.NewLine, Rules);

    // zbx_finalize_key_access_rules_configuration: drop what can never match or changes nothing, then end with the
    // default system.run deny unless the rules already decide system.run[*].
    private static ImmutableArray<Rule> Finalize(List<Rule> rules)
    {
        Rule systemRun = Rule.Parse(DenyPrefix + "system.run[*]");
        int systemRunIndex = rules.FindIndex(rule => rule.SamePattern(systemRun));
        int count = rules.Count - (systemRunIndex >= 0 ? 1 : 0);
        if (count != 0)
        {
            int matchAll = rules.FindIndex(rule => rule.IsMatchAll);
            if (matchAll >= 0)
            {
                if (systemRunIndex < 0 || matchAll < systemRunIndex) systemRunIndex = matchAll;
                rules.RemoveRange(matchAll + 1, rules.Count - matchAll - 1);
            }
            for (int i = rules.Count - 1; i >= 0 && rules[i].Allow; i--)
            {
                if (!rules[i].IsSystemRun) rules.RemoveAt(i);   // AllowKey=* is the default; system.run's are not redundant
            }
            if (rules.Count == 0)
            {
                throw new FormatException("The item key access rules are configured to match all keys, which indicates a configuration problem; remove the rules if that is the purpose.");
            }
        }
        if (systemRunIndex < 0) rules.Add(systemRun);
        return [.. rules];
    }

    /// <summary>One rule: allow or deny, the name pattern, and the parameter patterns (none when the pattern has no brackets).</summary>
    private sealed class Rule
    {
        private readonly string _text;
        private readonly string _name;
        private readonly ImmutableArray<string> _parameters;
        private readonly bool _emptyArguments;

        private Rule(string text, bool allow, string name, ImmutableArray<string> parameters, bool emptyArguments)
        {
            _text = text;
            Allow = allow;
            _name = name;
            _parameters = parameters;
            _emptyArguments = emptyArguments;
        }

        public bool Allow { get; }

        // Zabbix counts '*[]' as match-all too: it looks at the elements only.
        public bool IsMatchAll => _name == "*" && _parameters.IsEmpty;

        public bool IsSystemRun => _name == "system.run";

        public static Rule Parse(string line)
        {
            ArgumentNullException.ThrowIfNull(line);
            bool allow = line.StartsWith(AllowPrefix, StringComparison.Ordinal);
            if (!allow && !line.StartsWith(DenyPrefix, StringComparison.Ordinal))
                throw new FormatException($"An item key access rule is 'AllowKey=<pattern>' or 'DenyKey=<pattern>': '{line}'.");
            string pattern = line[(allow ? AllowPrefix : DenyPrefix).Length..];
            int nameLength = 0;
            while (nameLength < pattern.Length && (ZabbixItemKey.IsNameChar(pattern[nameLength]) || pattern[nameLength] == '*')) nameLength++;
            if (nameLength == 0) throw Invalid(line, "the key is empty");
            string name = Minimize(pattern[..nameLength]);
            if (nameLength == pattern.Length) return new Rule(line, allow, name, [], emptyArguments: false);
            if (pattern[nameLength] != '[' || pattern[^1] != ']') throw Invalid(line, "the key is followed by text other than a bracketed parameter list");
            if (pattern.Length - nameLength == 2) return new Rule(line, allow, name, [], emptyArguments: true);
            // The parameters as Zabbix's zbx_get_param reads them; the key grammar accepts '*' inside any parameter.
            if (!ZabbixItemKey.TryParse("k" + pattern[nameLength..], out ZabbixItemKey parsed)) throw Invalid(line, "the parameters do not parse");
            List<string> parameters = [.. parsed.Parameters.Select(Minimize)];
            while (parameters.Count > 1 && parameters[^1] == "*" && parameters[^2] == "*") parameters.RemoveAt(parameters.Count - 1);
            return new Rule(line, allow, name, [.. parameters], emptyArguments: false);
        }

        public bool SamePattern(Rule other)
            => _emptyArguments == other._emptyArguments && _name == other._name && _parameters.SequenceEqual(other._parameters, StringComparer.Ordinal);

        // zbx_check_request_access_rules, one rule: the name, the parameter count, then each parameter.
        public bool Matches(string name, IReadOnlyList<string> parameters, int count, bool emptyArguments)
        {
            if (IsMatchAll) return true;
            int elements = _parameters.Length + 1;
            if (elements > 1)
            {
                if (_parameters[^1] == "*")
                {
                    if (elements == 2 && count == 0) return false;          // rule key[*], request key
                }
                else
                {
                    if (count < elements - 1) return false;                  // too few parameters
                    if (count > elements - 1 && !emptyArguments) return false;   // too many parameters
                }
            }
            if (!Wildcard(name, _name)) return false;
            if (_emptyArguments) return emptyArguments;                     // the rule expects key[]
            if (emptyArguments && elements == 1) return false;
            if (count == 0 && elements == 1) return true;
            for (int j = 1; j < elements; j++)
            {
                string pattern = _parameters[j - 1];
                if (j == elements - 1)
                {
                    if (pattern == "*") return true;
                    return count >= j && Wildcard(parameters[j - 1], pattern);
                }
                if (count < j || !Wildcard(parameters[j - 1], pattern)) return false;
            }
            return false;
        }

        public override string ToString() => _text;

        private static FormatException Invalid(string line, string reason)
            => new(string.Create(CultureInfo.InvariantCulture, $"The item key access rule '{line}' is not one item key: {reason}."));

        // zbx_wildcard_minimize: "**" is "*".
        private static string Minimize(string pattern)
        {
            while (pattern.Contains("**", StringComparison.Ordinal)) pattern = pattern.Replace("**", "*", StringComparison.Ordinal);
            return pattern;
        }

        // zbx_wildcard_match: '*' matches any run of characters, everything else itself.
        private static bool Wildcard(string value, string pattern)
        {
            int v = 0, p = 0, star = -1, mark = 0;
            while (v < value.Length)
            {
                if (p < pattern.Length && pattern[p] == '*')
                {
                    star = p++;
                    mark = v;
                }
                else if (p < pattern.Length && pattern[p] == value[v])
                {
                    p++;
                    v++;
                }
                else if (star >= 0)
                {
                    p = star + 1;
                    v = ++mark;
                }
                else
                {
                    return false;
                }
            }
            while (p < pattern.Length && pattern[p] == '*') p++;
            return p == pattern.Length;
        }
    }
}
