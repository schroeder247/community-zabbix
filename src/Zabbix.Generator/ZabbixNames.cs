// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Community.Zabbix.Generator;

/// <summary>
/// Provides a field's C# name from its words. The extractor names a field by capitalising each
/// underscore-separated part of its wire name (<c>tools/ZabbixApiExtract</c>, <c>merge.py</c> <c>pascal</c>), so a
/// lower-case compound reads as one word: <c>sortorder</c> → <c>Sortorder</c>, <c>opmessage_grp</c> →
/// <c>OpmessageGrp</c>, <c>hk_trends</c> → <c>HkTrends</c>. This splits each part into the fewest words
/// <c>schema/words.json</c> knows (embedded in this assembly) and renders them: <c>SortOrder</c>,
/// <c>OperationMessageGroup</c>, <c>HousekeepingTrends</c>. It applies only to a name that is exactly that mechanical
/// capitalisation — a name the overlay chose (<c>VisibleName</c>, <c>EvalType</c>) is the reviewed one and wins here, and
/// <see cref="ZabbixRespellings"/> respells every name afterwards (<c>EvalType</c> is <c>EvaluationType</c>) —
/// and once, in <see cref="ApiDescription.Read(string, List{EmitProblem})"/>, so the records, the cmdlets, the views and the converters all see the
/// same name. The name it replaced is kept as <see cref="FieldDescription.PreviousName"/>, which the cmdlet emitter
/// keeps as a parameter alias.
/// </summary>
internal static class ZabbixNames
{
    // The extractor's own exceptions to its id/ids rule (merge.py _NOT_ID): words that end in "id" without being ids.
    private static readonly ImmutableHashSet<string> s_notId = ["uuid", "valid", "void", "paid", "grid", "id", "ids"];

    private static readonly Vocabulary s_words = Vocabulary.Load();

    /// <summary>
    /// The extractor's name for <paramref name="wire"/> (<c>merge.py</c> <c>pascal</c>): each part capitalised, a
    /// trailing <c>id</c>/<c>ids</c> split off. <c>tls_psk_identity</c> → <c>TlsPskIdentity</c>,
    /// <c>hostids</c> → <c>HostIds</c>, <c>sortorder</c> → <c>Sortorder</c>.
    /// </summary>
    /// <param name="wire">The wire name.</param>
    public static string Mechanical(string wire) => Join(Parts(wire).Select(MechanicalWord));

    /// <summary>
    /// The name of <paramref name="wire"/> by its words: <c>sortorder</c> → <c>SortOrder</c>, <c>opmessage_grp</c> →
    /// <c>OperationMessageGroup</c>; a part no known words make up keeps <see cref="Mechanical"/>'s capitalisation.
    /// </summary>
    /// <param name="wire">The wire name.</param>
    public static string Friendly(string wire) => Join(Parts(wire).Select(FriendlyWord));

    /// <summary>
    /// The wire parts <see cref="Friendly"/> leaves as one capitalised word because no known words make them up (and
    /// that are not themselves a known word): the dictionary's gaps, for review.
    /// </summary>
    /// <param name="wire">The wire name.</param>
    public static IEnumerable<string> Unsplit(string wire)
        => Parts(wire).Select(Lower).OfType<(string Core, string Digits)>()
            .Where(p => !s_words.Names.ContainsKey(p.Core + p.Digits) && !s_words.Names.ContainsKey(p.Core) && s_words.Split(p.Core) is null)
            .Select(p => p.Core);

    /// <summary>
    /// <paramref name="type"/> with every field whose name is <see cref="Mechanical"/> renamed to <see cref="Friendly"/>,
    /// the old name kept as <see cref="FieldDescription.PreviousName"/>; a friendly name another field of the record
    /// already has keeps the old one (two members cannot share a name).
    /// </summary>
    /// <param name="type">The record as the description states it.</param>
    internal static TypeDescription Rename(TypeDescription type)
    {
        var taken = new HashSet<string>(type.Fields.Select(f => f.Name), StringComparer.Ordinal);
        var fields = new List<FieldDescription>(type.Fields.Length);
        foreach (FieldDescription field in type.Fields)
        {
            string friendly = Friendly(field.Wire);
            if (field.Name == Mechanical(field.Wire) && friendly != field.Name && taken.Add(friendly))
            {
                taken.Remove(field.Name);
                fields.Add(field with { Name = friendly, PreviousName = field.Name });
            }
            else
            {
                fields.Add(field);
            }
        }
        return type with { Fields = [.. fields] };
    }

    /// <summary>
    /// The name of a record or enum by its words: each capitalised part of <paramref name="name"/> as <see cref="Friendly"/>
    /// names a wire part, a word the part before it already ends in said once. <c>ActionOperationOpgroupRequest</c> →
    /// <c>ActionOperationGroupRequest</c>, <c>MapSelement</c> → <c>MapElement</c>, <c>DsvcStatus</c> →
    /// <c>DiscoveredServiceStatus</c>; a part no known words make up (<c>Reg</c>, <c>Sli</c>) is kept as written.
    /// </summary>
    /// <param name="name">The record or enum name as the description states it.</param>
    public static string TypeName(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        var words = new List<string>();
        foreach (string part in TypeParts(name))
        {
            string? named = NamedTypePart(part);
            if (named is null)
            {
                words.Add(part);
                continue;
            }
            List<string> said = [.. TypeParts(named)];
            if (said.Count > 1 && words.Count > 0 && words[^1] == said[0]) said.RemoveAt(0);
            words.AddRange(said);
        }
        return string.Concat(words);
    }

    /// <summary>
    /// <paramref name="name"/> with a part that repeats the part before it said once: the extractor names a nested record
    /// by its parent and its field, so a host's <c>hostDiscovery</c> is <c>HostDiscovery</c>, not
    /// <c>HostHostDiscovery</c>.
    /// </summary>
    /// <param name="name">A record or enum name.</param>
    public static string Once(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        var words = new List<string>();
        foreach (string part in TypeParts(name))
        {
            if (words.Count == 0 || words[^1] != part) words.Add(part);
        }
        return string.Concat(words);
    }

    /// <summary>The capitalised parts of a record or enum name no known words make up: the dictionary's gaps, for review.</summary>
    /// <param name="name">The record or enum name.</param>
    public static IEnumerable<string> UnsplitType(string name)
        => name is null ? throw new ArgumentNullException(nameof(name)) : UnsplitTypeParts(name);

    private static IEnumerable<string> UnsplitTypeParts(string name)
    {
        foreach (string part in TypeParts(name))
        {
            string lower = LowerAscii(part);
            if (NamedTypePart(part) is not null || s_words.Names.ContainsKey(lower)) continue;
            // a part with digits inside (Bool2dec, Snmpv2c) is a gap unless a name covers it whole
            if (Lower(lower) is not (string core, _)) yield return lower;
            else if (!s_words.IsWord(lower) && !s_words.IsWord(core) && s_words.Split(core) is null) yield return core;
        }
    }

    /// <summary>
    /// <paramref name="api"/> with every record and enum named by <see cref="TypeName"/>, and every kind, model, parameter
    /// and result that names one following it; a name another record or enum already has keeps the old one (two types
    /// cannot share a name).
    /// </summary>
    /// <param name="api">The description, its fields already named (<see cref="Rename(TypeDescription)"/>).</param>
    internal static ApiDescription Rename(ApiDescription api)
    {
        var taken = new HashSet<string>(api.Types.Select(t => t.Name).Concat(api.Enums.Select(e => e.Name)), StringComparer.Ordinal);
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        bool membersRenamed = api.Enums.Any(e => e.Members.Any(m => TypeName(m.Name) != m.Name));
        foreach (string name in api.Types.Select(t => t.Name).Concat(api.Enums.Select(e => e.Name)))
        {
            string named = TypeName(name);
            if (named != name && !renamed.ContainsKey(name) && taken.Add(named)) renamed[name] = named;
        }
        // a repeated part said once only where no record or enum already has the shorter name (MapElementElementRequest
        // keeps its name beside MapElementRequest)
        foreach (string name in api.Types.Select(t => t.Name).Concat(api.Enums.Select(e => e.Name)))
        {
            string current = renamed.TryGetValue(name, out string? already) ? already : name;
            string once = Once(current);
            if (once != current && taken.Add(once))
            {
                taken.Remove(current);
                renamed[name] = once;
            }
        }
        if (renamed.Count == 0 && !membersRenamed) return api;
        string Name(string name) => renamed.TryGetValue(name, out string? named) ? named : name;
        string Kind(string kind) => s_namedKind.Replace(kind, m => m.Groups[1].Value + Name(m.Groups[2].Value));
        // a sentence's <see cref="ZabbixX"/> names the record by its C# name
        string? Text(string? text) => text is null ? null : s_cref.Replace(text, m => m.Groups[1].Value + Name(m.Groups[2].Value));
        return api with
        {
            Enums = [.. api.Enums.Select(e => e with { Name = Name(e.Name), Summary = Text(e.Summary), Members = [.. Members(e.Members).Select(m => m with { Summary = Text(m.Summary) })] })],
            Types = [.. api.Types.Select(t => t with
            {
                Name = Name(t.Name),
                Summary = Text(t.Summary),
                Fields = [.. t.Fields.Select(f => f with { Kind = Kind(f.Kind), Summary = Text(f.Summary) })],
            })],
            Services = [.. api.Services.Select(s => s with
            {
                Summary = Text(s.Summary),
                Model = s.Model is null ? null : Name(s.Model),
                Methods = [.. s.Methods.Select(m => m with { Parameters = Kind(m.Parameters), Result = Kind(m.Result) })],
            })],
            Cmdlets = api.CmdletShape.Renamed(Name),
        };
    }

    /// <summary>
    /// An enum's members named by their words as a record is (<see cref="TypeName"/>): the extractor capitalises what
    /// follows the family's prefix, so <c>ITEM_TYPE_HTTPAGENT</c> is <c>HttpAgent</c>, not <c>Httpagent</c>, and
    /// <c>EVENT_OBJECT_AUTOREGHOST</c> is <c>AutoregistrationHost</c>. A name another member already has keeps the old one.
    /// </summary>
    /// <param name="members">The members as the description states them.</param>
    internal static IEnumerable<EnumMember> Members(IEnumerable<EnumMember> members)
    {
        EnumMember[] all = [.. members];
        var taken = new HashSet<string>(all.Select(m => m.Name), StringComparer.Ordinal);
        foreach (EnumMember member in all)
        {
            string named = TypeName(member.Name);
            if (named != member.Name && taken.Add(named))
            {
                taken.Remove(member.Name);
                yield return member with { Name = named };
            }
            else
            {
                yield return member;
            }
        }
    }

    // A kind that names a record or enum: enum:X, object:X, list:object:X, map:enum:X …
    private static readonly Regex s_namedKind = new(@"((?:enum|object):)(\w+)", RegexOptions.CultureInvariant);

    private static readonly Regex s_cref = new(@"(cref=""Zabbix)(\w+)", RegexOptions.CultureInvariant);

    // A record or enum name's capitalised parts: ActionOperationOpgroupRequest → Action, Operation, Opgroup, Request.
    private static IEnumerable<string> TypeParts(string name)
    {
        for (int start = 0; start < name.Length;)
        {
            int end = start + 1;
            while (end < name.Length && !char.IsUpper(name[end])) end++;
            yield return name[start..end];
            start = end;
        }
    }

    // A capitalised part named by its words when that changes it (Opgroup → OperationGroup, Valuemap → ValueMap), else null.
    private static string? NamedTypePart(string part)
    {
        string lower = LowerAscii(part);
        if (s_words.Names.TryGetValue(lower, out string? whole)) return whole == part ? null : whole;
        if (Lower(lower) is not (string core, string digits)) return null;
        string? named = s_words.Names.TryGetValue(core, out string? named2) ? named2 + digits
            : s_words.Split(core) is { } words ? string.Concat(words.Select(s_words.Render)) + digits
            : null;
        return named is null || named == part ? null : named;
    }

    // ASCII lower case (a name is ASCII; string.ToLowerInvariant is CA1308's to question).
    private static string LowerAscii(string text) => new([.. text.Select(c => c is >= 'A' and <= 'Z' ? (char)(c + 32) : c)]);

    // merge.py's re.split(r'[_\W]+', text): underscores and anything not a letter or a digit separate the parts.
    private static IEnumerable<string> Parts(string wire) => Regex.Split(wire, @"[_\W]+").Where(p => p.Length > 0);

    private static string Join(IEnumerable<string> words)
    {
        string name = string.Concat(words);
        return name.Length == 0 ? "_" : char.IsDigit(name[0]) ? "_" + name : name;
    }

    private static string MechanicalWord(string part)
    {
        if (!part.Any(char.IsUpper) && !s_notId.Contains(part))
        {
            if (part.EndsWith("ids", StringComparison.Ordinal) && part.Length > 3) return Capital(part[..^3]) + "Ids";
            if (part.EndsWith("id", StringComparison.Ordinal) && part.Length > 2) return Capital(part[..^2]) + "Id";
        }
        return Capital(part);
    }

    private static string FriendlyWord(string part)
    {
        // A camelCase part (selectHosts, parentTemplateids) is the API's own spelling: as the extractor had it.
        if (Lower(part) is not (string core, string digits)) return MechanicalWord(part);
        if (s_words.Names.TryGetValue(part, out string? whole)) return whole;
        if (s_words.Names.TryGetValue(core, out string? named)) return named + digits;
        return s_words.Split(core) is { } words ? string.Concat(words.Select(s_words.Render)) + digits : MechanicalWord(part);
    }

    // A lower-case part as its letters and the digits after them (selementid1 → selementid, 1); null for any other part.
    private static (string Core, string Digits)? Lower(string part)
    {
        int letters = 0;
        while (letters < part.Length && part[letters] is >= 'a' and <= 'z') letters++;
        int end = letters;
        while (end < part.Length && char.IsDigit(part[end])) end++;
        return letters > 0 && end == part.Length ? (part[..letters], part[letters..]) : null;
    }

    private static string Capital(string word) => word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];

    /// <summary><c>schema/words.json</c>, embedded in this assembly at build.</summary>
    private sealed class Vocabulary(ImmutableHashSet<string> words, ImmutableDictionary<string, string> expansions, ImmutableDictionary<string, string> names)
    {
        public ImmutableDictionary<string, string> Names { get; } = names;

        public static Vocabulary Load()
        {
            using Stream stream = typeof(ZabbixNames).Assembly.GetManifestResourceStream("Zabbix.Generator.words.json")
                ?? throw new InvalidOperationException("schema/words.json is not embedded in Zabbix.Generator");
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            return new(
                [.. root.GetProperty("words").EnumerateArray().Select(w => w.GetString()!), "id", "ids"],
                root.GetProperty("expansions").EnumerateObject().ToImmutableDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal),
                root.GetProperty("names").EnumerateObject().ToImmutableDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal));
        }

        public string Render(string word) => expansions.TryGetValue(word, out string? rendered) ? rendered : Capital(word);

        public bool IsWord(string word) => words.Contains(word) || expansions.ContainsKey(word) || Names.ContainsKey(word);

        /// <summary>The fewest known words <paramref name="text"/> is made of, or <see langword="null"/>.</summary>
        public string[]? Split(string text)
        {
            var best = new List<string>?[text.Length + 1];
            best[0] = [];
            for (int end = 1; end <= text.Length; end++)
            {
                for (int start = 0; start < end; start++)
                {
                    if (best[start] is not { } before) continue;
                    string word = text[start..end];
                    if (!words.Contains(word) && !expansions.ContainsKey(word)) continue;
                    if (best[end] is null || before.Count + 1 < best[end]!.Count) best[end] = [.. before, word];
                }
            }
            return best[text.Length]?.ToArray();
        }
    }
}
