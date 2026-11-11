// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text;

namespace Community.Zabbix.Generator;

/// <summary>
/// The typed side of a <c>.get</c>'s field grammar. <c>filter</c>, <c>search</c>, <c>output</c>, the <c>select</c>
/// options and <c>sortfield</c> take wire field names, and the description already knows each object's fields, their C#
/// names and their types, so it emits: <c>Zabbix{Record}Fields</c>, every field of each record an output, a select or a
/// sort names, as a <c>ZabbixField&lt;ZabbixRecord&gt;</c> under its C# name; <c>Zabbix{Object}GetFilter</c>, one typed
/// list per field <c>filter</c> accepts; and <c>Zabbix{Object}GetSearch</c>, one text per field <c>search</c> accepts.
/// The filter and the search are <c>ZabbixGetConditions</c>, whose indexer sets a field they do not type, by its wire
/// name.
/// </summary>
internal sealed partial class ZabbixApiEmitter
{
    // The members a filter or search has as a dictionary: a field of the same name takes Value after it.
    private static readonly HashSet<string> s_dictionaryMembers = new(StringComparer.Ordinal)
    {
        "Count", "Keys", "Values", "Item", "ContainsKey", "TryGetValue", "GetEnumerator", "Entries",
    };

    // A member's value as a local: its name in camel case, escaped (@status), so no keyword or member is hidden.
    private static string Local(string member)
    {
        string bare = member.TrimStart('@');
        return "@" + char.ToLowerInvariant(bare[0]) + bare[1..];
    }

    // The read record a get's parameters return, through the object's client.
    private TypeDescription? ModelOf(TypeDescription get)
        => api.Services.FirstOrDefault(s => s.Object == get.Object && s.Model is not null) is { Model: { } model } && _types.TryGetValue(model, out TypeDescription? read) ? read : null;

    // One field of the read record a filter or search names by wire: the record's own field, else the one field of that
    // wire among the records it holds (a host's interfaces carry ip and dns, which host.get filters by).
    private (TypeDescription Owner, FieldDescription Field)? FieldOf(TypeDescription model, string wire)
    {
        FieldDescription? own = model.Fields.FirstOrDefault(f => f.Wire == wire);
        if (own is not null) return (model, own);
        (TypeDescription Owner, FieldDescription Field)[] nested = [.. model.Fields
            .Select(f => NestedRecord(f.Kind))
            .OfType<TypeDescription>()
            .Distinct()
            .SelectMany(t => t.Fields.Where(f => f.Wire == wire).Select(f => (t, f)))];
        return nested.Length == 1 ? nested[0] : null;
    }

    private TypeDescription? NestedRecord(string kind)
    {
        string bare = kind.StartsWith("list:", StringComparison.Ordinal) ? kind[5..] : kind;
        return bare.StartsWith("object:", StringComparison.Ordinal) && _types.TryGetValue(bare[7..], out TypeDescription? nested) ? nested : null;
    }

    // How a filter sends one field's values: the element type a caller writes and the conversion to ZabbixFieldValue.
    // A kind with no scalar wire form (a list, a record, JSON) is not a filter's to type.
    private (string Element, string Value)? FilterShape(string kind, string member)
    {
        const string Entries = "global::Community.Zabbix.ZabbixFieldEntries";
        string local = Local(member);
        return kind switch
        {
            "string" => ("string", Entries + ".Of(" + local + ")"),
            "id" => ("ulong", Entries + ".Of(" + local + ", static value => value.ToString(global::System.Globalization.CultureInfo.InvariantCulture))"),
            "clock" => ("global::System.DateTimeOffset", Entries + ".Of(" + local + ", static value => value.ToUnixTimeSeconds())"),
            "timeofday" => ("global::System.TimeOnly", Entries + ".Of(" + local + ", static value => value.Ticks / global::System.TimeSpan.TicksPerSecond)"),
            "int" or "long" => (kind, Entries + ".Of(" + local + ", static value => (long)value)"),
            "ulong" => ("ulong", Entries + ".Of(" + local + ", static value => value.ToString(global::System.Globalization.CultureInfo.InvariantCulture))"),
            "double" => ("double", Entries + ".Of(" + local + ", static value => value.ToString(\"R\", global::System.Globalization.CultureInfo.InvariantCulture))"),
            "bool" or "apibool" => ("bool", Entries + ".Of(" + local + ", static value => value ? 1L : 0L)"),
            // an interval or a port as the record types it, sent as Zabbix spells it; the hosts a trapper allows, as text
            ZabbixTypedFields.Interval => ("global::Community.Zabbix.ZabbixInterval", Entries + ".Of(" + local + ", static value => value.ToString())"),
            ZabbixTypedFields.Port => ("global::Community.Zabbix.ZabbixPort", Entries + ".Of(" + local + ", static value => value.ToString())"),
            ZabbixTypedFields.Duration => ("global::Community.Zabbix.ZabbixDuration", Entries + ".Of(" + local + ", static value => value.ToString())"),
            ZabbixTypedFields.Peers => ("string", Entries + ".Of(" + local + ")"),
            _ when kind.StartsWith("enum:", StringComparison.Ordinal) && _enums.ContainsKey(kind[5..])
                => ("global::Community.Zabbix." + Prefixed(kind[5..]), Entries + ".Of(" + local + ", static value => (long)value)"),
            _ => null,
        };
    }

    // The files of the typed field grammar: the fields of every record an output, a select or a sort names, in the order
    // the description first names each, and a filter and a search per get whose object has a read record.
    private IEnumerable<GeneratedSource> EmitFieldGrammar()
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        foreach (FieldDescription field in api.Types.SelectMany(t => t.Fields))
        {
            string target = field.Kind.StartsWith(ZabbixTypedFields.Output, StringComparison.Ordinal) ? field.Kind[ZabbixTypedFields.Output.Length..]
                : field.Kind.StartsWith(ZabbixTypedFields.SortFields, StringComparison.Ordinal) ? field.Kind[ZabbixTypedFields.SortFields.Length..]
                : string.Empty;
            if (target.StartsWith("object:", StringComparison.Ordinal) && _types.TryGetValue(target[7..], out TypeDescription? record) && named.Add(record.Name))
                yield return new(Prefixed(record.Name) + "Fields.g.cs", EmitFieldNames(record));
        }
        foreach (TypeDescription get in api.Types.Where(t => t.Role == "get" && t.GetParameters && t.Name.EndsWith("GetParameters", StringComparison.Ordinal)))
        {
            if (ModelOf(get) is not { } model) continue;
            string stem = Prefixed(get.Name[..^"Parameters".Length]);
            if (EmitFieldValues(get, model, stem + "Filter", search: false) is { } filter) yield return new(stem + "Filter.g.cs", filter);
            if (EmitFieldValues(get, model, stem + "Search", search: true) is { } search) yield return new(stem + "Search.g.cs", search);
        }
    }

    // The type of a get's filter or search: its typed conditions, or the fields by wire name where it has none.
    private string ConditionsType(TypeDescription get, bool search)
    {
        const string Untyped = "global::Community.Zabbix.ZabbixGetFields";
        if (!get.GetParameters || !get.Name.EndsWith("GetParameters", StringComparison.Ordinal) || ModelOf(get) is not { } model) return Untyped;
        string name = Prefixed(get.Name[..^"Parameters".Length]) + (search ? "Search" : "Filter");
        return ConditionMembers(get, model, name, search).Count > 0 ? "global::Community.Zabbix." + name : Untyped;
    }

    // A record's fields, each a ZabbixField<TRecord> under its member's name; a clock folded with its nanoseconds
    // (ZabbixClockPairs) has a field for each of its two wire fields, so an output or a sort can name both.
    private string EmitFieldNames(TypeDescription model)
    {
        string record = Prefixed(model.Name);
        string name = record + "Fields";
        string type = "global::Community.Zabbix.ZabbixField<global::Community.Zabbix." + record + ">";
        string first = Member(model.Fields.Length > 0 ? model.Fields[0].Name : "Id");
        var code = new StringBuilder(Header(api.Range,
            "The fields of <see cref=\"" + record + "\"/>, under their C# names, for a get's <c>output</c>, a <c>select</c> option that returns the record, and its sort: <c>Output = [" + name + "." + first + "]</c>."));
        code.Append("public static class ").Append(name).Append("\n{\n");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (FieldDescription field in model.Fields)
        {
            string member = Member(field.Name);
            if (member == name || !seen.Add(member)) continue;
            string cref = record + "." + member.TrimStart('@');
            code.Append("    /// <summary>Represents <c>").Append(Doc(field.Wire, field.Wire)).Append("</c>, the field <see cref=\"").Append(cref).Append("\"/> holds.</summary>\n");
            code.Append("    public static readonly ").Append(type).Append(' ').Append(member).Append(" = new(").Append(Lit(field.Wire)).Append(");\n\n");
            string nanoseconds = member.TrimStart('@') + "Nanoseconds";
            if (field.Nanoseconds is null || model.Fields.Any(f => Member(f.Name) == nanoseconds) || !seen.Add(nanoseconds)) continue;
            code.Append("    /// <summary>Represents <c>").Append(Doc(field.Nanoseconds, field.Nanoseconds)).Append("</c>, the nanoseconds <see cref=\"").Append(cref).Append("\"/> holds beside its seconds.</summary>\n");
            code.Append("    public static readonly ").Append(type).Append(' ').Append(nanoseconds).Append(" = new(").Append(Lit(field.Nanoseconds)).Append(");\n\n");
        }
        while (code.Length > 1 && code[^1] == '\n' && code[^2] == '\n') code.Length--;
        return code.Append("}\n").ToString();
    }

    // What a filter or search names: each field the get takes and the read record types, with how to send its value.
    private List<(string Member, string Wire, string Type, string Value, FieldDescription Field, string Owner)> ConditionMembers(TypeDescription get, TypeDescription model, string name, bool search)
    {
        IEnumerable<string> wires = (search ? get.Searchable : get.Filterable) is { Length: > 0 } listed
            ? listed
            : model.Fields.Select(f => f.Wire);
        var members = new List<(string Member, string Wire, string Type, string Value, FieldDescription Field, string Owner)>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { name };
        foreach (string wire in wires)
        {
            // a secret is never a condition: the reply does not carry it, and a filter on it would print and log it
            if (FieldOf(model, wire) is not ({ } owner, { } field) || field.Secret) continue;
            string member = Member(field.Name);
            if (s_dictionaryMembers.Contains(member)) member += "Value";
            if (search)
            {
                // a search is a substring of the field's text, the text of an interval, a port and a trapper's hosts too
                if (field.Kind is not ("string" or ZabbixTypedFields.Interval or ZabbixTypedFields.Duration or ZabbixTypedFields.Port or ZabbixTypedFields.Peers) || !seen.Add(member)) continue;
                members.Add((member, wire, "string", "global::Community.Zabbix.ZabbixFieldValue.FromString(" + Local(member) + ")", field, Prefixed(owner.Name) + "." + Member(field.Name).TrimStart('@')));
            }
            else if (FilterShape(field.Kind, member) is { } shape && seen.Add(member))
            {
                members.Add((member, wire, "global::System.Collections.Generic.IReadOnlyList<" + shape.Element + ">", shape.Value, field, Prefixed(owner.Name) + "." + Member(field.Name).TrimStart('@')));
            }
        }
        return members;
    }

    // A filter (a list of values per field, sent as an array) or a search (one text per field): a member per field,
    // over the base that adds the fields set by wire name, the dictionary the parameters send, equality and the text form.
    private string? EmitFieldValues(TypeDescription get, TypeDescription model, string name, bool search)
    {
        string record = Prefixed(model.Name);
        string parameters = Prefixed(get.Name);
        string method = get.Method ?? get.Name;
        List<(string Member, string Wire, string Type, string Value, FieldDescription Field, string Owner)> members = ConditionMembers(get, model, name, search);
        if (members.Count == 0) return null;
        string summary = search
            ? "The <c>search</c> of <c>" + method + "</c> by field: each member set is one pattern its field must contain (<see cref=\"" + parameters + ".StartSearch\"/>, <see cref=\"" + parameters + ".SearchWildcardsEnabled\"/> and <see cref=\"" + parameters + ".SearchByAny\"/> say how)."
            : "The <c>filter</c> of <c>" + method + "</c> by field, typed as <see cref=\"" + record + "\"/> types it: each member set is one condition, met when the field holds one of its values.";
        const string Pair = "global::System.Collections.Generic.KeyValuePair<string, global::Community.Zabbix.ZabbixFieldValue>";
        var code = new StringBuilder(Header(api.Range, summary));
        code.Append(Suppress("Naming", "CA1710:Identifiers should have correct suffix", "The API's own word for the parameter it fills (filter, search); the dictionary is the shape the parameter is sent in, not what the type is.")).Append('\n');
        code.Append("public sealed record ").Append(name).Append(" : global::Community.Zabbix.ZabbixGetConditions\n{\n");
        code.Append("    /// <summary>Initializes conditions with nothing set.</summary>\n    public ").Append(name).Append("()\n    {\n    }\n\n");
        code.Append("    /// <summary>Initializes conditions with fields set by their wire names.</summary>\n    /// <param name=\"fields\">The wire names and values; none when <see langword=\"null\"/>.</param>\n");
        code.Append("    public ").Append(name).Append("(global::System.Collections.Generic.IEnumerable<").Append(Pair).Append(">? fields)\n        : base(fields)\n    {\n    }\n\n");
        foreach ((string member, string wire, string type, _, FieldDescription field, string owner) in members)
        {
            code.Append("    /// <summary>").Append(search ? "Text that " : "Values one of which ").Append("<see cref=\"").Append(owner).Append("\"/> ")
                .Append(search ? "contains" : "holds").Append(" (<c>").Append(Doc(wire, wire)).Append("</c>).</summary>\n");
            if (field.Deprecated is not null) code.Append("    [global::System.Obsolete(\"Deprecated since Zabbix ").Append(field.Deprecated).Append(" (API_DEPRECATED).\")]\n");
            if (type == "string") code.Append(UrlSuppression(member, "    "));
            code.Append("    public ").Append(type).Append("? ").Append(member).Append(" { get; set; }\n\n");
        }
        code.Append("    /// <summary>Whether <paramref name=\"other\"/> sets the same fields to the same values.</summary>\n");
        code.Append("    /// <param name=\"other\">The ").Append(search ? "search" : "filter").Append(" to compare with this one; <see langword=\"null\"/> is never equal.</param>\n");
        code.Append("    /// <returns><see langword=\"true\"/> when both send the same fields with the same values; otherwise <see langword=\"false\"/>.</returns>\n");
        code.Append("    public bool Equals(").Append(name).Append("? other) => base.Equals(other);\n\n");
        code.Append("    /// <inheritdoc/>\n    public override int GetHashCode() => base.GetHashCode();\n\n");
        code.Append("    /// <inheritdoc/>\n    protected override bool PrintMembers(global::System.Text.StringBuilder builder) => base.PrintMembers(builder);\n\n");
        bool obsolete = members.Exists(m => m.Field.Deprecated is not null);
        code.Append("    /// <inheritdoc/>\n    protected override global::System.Collections.Generic.IEnumerable<").Append(Pair).Append("> MemberConditions()\n    {\n");
        if (obsolete) code.Append("#pragma warning disable CS0618 // the dictionary sends the deprecated fields too\n");
        foreach ((string member, string wire, _, string value, _, _) in members)
        {
            string local = Local(member);
            code.Append("        if (").Append(member).Append(" is { } ").Append(local).Append(") yield return new(").Append(Lit(wire)).Append(", ").Append(value).Append(");\n");
        }
        if (obsolete) code.Append("#pragma warning restore CS0618\n");
        code.Append("    }\n");
        return code.Append("}\n").ToString();
    }
}
