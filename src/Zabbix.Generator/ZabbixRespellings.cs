// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Community.Zabbix.Generator;

/// <summary>
/// Respells every name the generator emits by <c>schema/words.json</c>'s <c>respell</c> section, once the words have named
/// it (<see cref="ZabbixNames"/>), the overlay's own names included: a name keeps no abbreviation, and a two-letter acronym
/// is written in capitals (<c>EvalType</c> → <c>EvaluationType</c>, <c>Ip</c> → <c>IP</c>, <c>Username</c> →
/// <c>UserName</c>, Microsoft's general naming and capitalization conventions). The fields go first, by the names their
/// records and kinds have before the records do; then the records, the enums and their members, each object's client and
/// property, and every kind, model and <c>cref</c> that names a respelled record. A respelled name another field or type
/// already has keeps the old one, which a conformance test then reports. A respelled field keeps the name it had as
/// <see cref="FieldDescription.PreviousName"/>, and an object whose client is respelled keeps its command noun as
/// <see cref="ServiceDescription.CmdletNoun"/>, so the modules keep their commands.
/// </summary>
internal static class ZabbixRespellings
{
    private static readonly Spellings s_spellings = Spellings.Load();

    // A kind that names a record or enum: enum:X, object:X, list:object:X, idmap:list:object:X …
    private static readonly Regex s_namedKind = new(@"((?:enum|object):)(\w+)", RegexOptions.CultureInvariant);

    private static readonly Regex s_cref = new(@"(cref=""Zabbix)(\w+)", RegexOptions.CultureInvariant);

    /// <summary>The capitalised parts no generated name keeps: the keys of the section's <c>parts</c>.</summary>
    public static IEnumerable<string> RetiredParts => s_spellings.Parts.Keys.OrderBy(part => part, StringComparer.Ordinal);

    /// <summary>
    /// The field names the section's <c>fields</c> retires, each with what it is retired for: a field of a kind
    /// (<c>Flags</c>, <c>enum:DiscoveryKinds</c>), whose type says more than its wire name, or a field of one record
    /// (<c>Host</c>, <c>HistoryPushRequest</c>).
    /// </summary>
    public static IEnumerable<(string Field, string Of)> RetiredFieldNames
        => s_spellings.Fields.Keys
            .Select(key => key.IndexOf(' ') is int space and > 0
                ? (key.Substring(0, space), key.Substring(space + 1))
                : (key.Substring(key.IndexOf('.') + 1), key.Substring(0, key.IndexOf('.'))))
            .OrderBy(entry => entry.Item2 + " " + entry.Item1, StringComparer.Ordinal);

    /// <summary>
    /// <paramref name="name"/> respelled: as <c>names</c> names it whole, else each of its capitalised parts as <c>parts</c>
    /// spells it (<c>OkAckColor</c> → <c>OkAcknowledgedColor</c>, <c>ZabbixHaNodeClient</c> → <c>ZabbixHANodeClient</c>).
    /// </summary>
    /// <param name="name">A record, enum, member, client or property name.</param>
    public static string Name(string name)
    {
        if (name is null) throw new ArgumentNullException(nameof(name));
        if (s_spellings.Names.TryGetValue(name, out string? whole)) return whole;
        return string.Concat(Parts(name).Select(part => s_spellings.Parts.TryGetValue(part, out string? spelled) ? spelled : part));
    }

    /// <summary><paramref name="api"/> with every name the generator emits respelled.</summary>
    /// <param name="api">The description, named by its words (<see cref="ZabbixNames.Rename(ApiDescription)"/>).</param>
    internal static ApiDescription Respell(ApiDescription api)
    {
        if (api is null) throw new ArgumentNullException(nameof(api));
        TypeDescription[] types = [.. api.Types.Select(RespellFields)];
        var taken = new HashSet<string>(types.Select(t => t.Name).Concat(api.Enums.Select(e => e.Name)), StringComparer.Ordinal);
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in types.Select(t => t.Name).Concat(api.Enums.Select(e => e.Name)))
        {
            string spelled = Name(name);
            if (spelled != name && !renamed.ContainsKey(name) && taken.Add(spelled)) renamed[name] = spelled;
        }
        string Type(string name) => renamed.TryGetValue(name, out string? spelled) ? spelled : name;
        string Kind(string kind) => s_namedKind.Replace(kind, m => m.Groups[1].Value + Type(m.Groups[2].Value));
        // a sentence's <see cref="ZabbixX"/> names the record by its C# name
        string? Text(string? text) => text is null ? null : s_cref.Replace(text, m => m.Groups[1].Value + Type(m.Groups[2].Value));
        return api with
        {
            Enums = [.. api.Enums.Select(e => e with { Name = Type(e.Name), Summary = Text(e.Summary), Members = [.. Members(e.Members).Select(m => m with { Summary = Text(m.Summary) })] })],
            Types = [.. types.Select(t => t with
            {
                Name = Type(t.Name),
                Summary = Text(t.Summary),
                Fields = [.. t.Fields.Select(f => f with { Kind = Kind(f.Kind), Summary = Text(f.Summary) })],
            })],
            Services = [.. api.Services.Select(s => RespellService(s, Type, Kind, Text))],
            Cmdlets = api.CmdletShape.Renamed(Type),
        };
    }

    // An object's client and property respelled (ZabbixHaNodeClient → ZabbixHANodeClient, HaNodes → HANodes), its model and
    // its methods' kinds following the records; the command noun it had is kept, so Get-ZbxHaNode stays Get-ZbxHaNode.
    private static ServiceDescription RespellService(ServiceDescription service, Func<string, string> type, Func<string, string> kind, Func<string?, string?> text)
    {
        string client = Name(service.Client);
        return service with
        {
            Client = client,
            Property = Name(service.Property),
            Summary = text(service.Summary),
            Model = service.Model is null ? null : type(service.Model),
            Methods = [.. service.Methods.Select(m => m with { Parameters = kind(m.Parameters), Result = kind(m.Result) })],
            CmdletNoun = service.CmdletNoun ?? (client != service.Client ? Noun(service.Client) : null),
        };
    }

    // ZabbixHaNodeClient → HaNode: the noun Get-Zbx<Noun> is named with.
    private static string Noun(string client)
    {
        string name = client.StartsWith("Zabbix", StringComparison.Ordinal) ? client.Substring(6) : client;
        return name.EndsWith("Client", StringComparison.Ordinal) ? name.Substring(0, name.Length - 6) : name;
    }

    // A record's fields respelled: by the field's own entry (its name and kind, or its record and name), else its whole
    // name, else its parts; a respelling another field of the record has takes the name 'taken' gives it, or keeps its own.
    private static TypeDescription RespellFields(TypeDescription type)
    {
        // each field's name as it would be respelled; a field already decided holds its final name here
        string[] spelled = [.. type.Fields.Select(field => FieldName(type, field))];
        if (spelled.SequenceEqual(type.Fields.Select(field => field.Name), StringComparer.Ordinal)) return type;
        var fields = new List<FieldDescription>(type.Fields.Length);
        for (int i = 0; i < type.Fields.Length; i++)
        {
            FieldDescription field = type.Fields[i];
            string name = spelled[i];
            if (Clashes(spelled, i, name))
                name = s_spellings.Taken.TryGetValue(field.Name, out string? other) && !Clashes(spelled, i, other) ? other : field.Name;
            spelled[i] = name;
            fields.Add(name == field.Name ? field : field with { Name = name, PreviousName = field.PreviousName ?? field.Name });
        }
        return type with { Fields = [.. fields] };
    }

    // Whether another field of the record has, or is to be respelled as, name.
    private static bool Clashes(string[] spelled, int index, string name)
    {
        for (int other = 0; other < spelled.Length; other++)
        {
            if (other != index && spelled[other] == name) return true;
        }
        return false;
    }

    private static string FieldName(TypeDescription type, FieldDescription field)
    {
        string bare = field.Kind.StartsWith("list:", StringComparison.Ordinal) ? field.Kind.Substring(5) : field.Kind;
        if (s_spellings.Fields.TryGetValue(field.Name + " " + bare, out string? typed)) return typed;
        if (s_spellings.Fields.TryGetValue(type.Name + "." + field.Name, out string? owned)) return owned;
        return Name(field.Name);
    }

    // An enum's members respelled; a respelling another member already has keeps the old name.
    private static IEnumerable<EnumMember> Members(IEnumerable<EnumMember> members)
    {
        EnumMember[] all = [.. members];
        var taken = new HashSet<string>(all.Select(m => m.Name), StringComparer.Ordinal);
        foreach (EnumMember member in all)
        {
            string spelled = Name(member.Name);
            if (spelled != member.Name && taken.Add(spelled))
            {
                taken.Remove(member.Name);
                yield return member with { Name = spelled };
            }
            else
            {
                yield return member;
            }
        }
    }

    // A name's capitalised parts: OkAckColor → Ok, Ack, Color; Snmpv3AuthProtocol → Snmpv3, Auth, Protocol.
    private static IEnumerable<string> Parts(string name)
    {
        for (int start = 0; start < name.Length;)
        {
            int end = start + 1;
            while (end < name.Length && !char.IsUpper(name[end])) end++;
            yield return name.Substring(start, end - start);
            start = end;
        }
    }

    /// <summary><c>schema/words.json</c>'s <c>respell</c> section, embedded in this assembly at build.</summary>
    private sealed class Spellings(ImmutableDictionary<string, string> parts, ImmutableDictionary<string, string> names, ImmutableDictionary<string, string> fields, ImmutableDictionary<string, string> taken)
    {
        public ImmutableDictionary<string, string> Parts { get; } = parts;

        public ImmutableDictionary<string, string> Names { get; } = names;

        public ImmutableDictionary<string, string> Fields { get; } = fields;

        public ImmutableDictionary<string, string> Taken { get; } = taken;

        public static Spellings Load()
        {
            using Stream stream = typeof(ZabbixRespellings).Assembly.GetManifestResourceStream("Zabbix.Generator.words.json")
                ?? throw new InvalidOperationException("schema/words.json is not embedded in Zabbix.Generator");
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement respell = document.RootElement.GetProperty("respell");
            return new(Map(respell, "parts"), Map(respell, "names"), Map(respell, "fields"), Map(respell, "taken"));
        }

        private static ImmutableDictionary<string, string> Map(JsonElement section, string name)
            => section.GetProperty(name).EnumerateObject().ToImmutableDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
    }
}
