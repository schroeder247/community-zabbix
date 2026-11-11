// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace Community.Zabbix.Generator;

/// <summary>
/// Emits the API's source files from one description. Used twice: by <see cref="ZabbixApiGenerator"/> at compile time for the
/// records, enums, capability table and clients, and by the build step (<c>Zabbix.Generator.Context</c>) for the JSON
/// context, which System.Text.Json's own generator must see as real source.
/// </summary>
/// <param name="api">The description.</param>
internal sealed partial class ZabbixApiEmitter(ApiDescription api)
{
    private readonly Dictionary<string, TypeDescription> _types = api.Types.ToDictionary(t => t.Name, StringComparer.Ordinal);
    private readonly Dictionary<string, EnumDescription> _enums = api.Enums.ToDictionary(e => e.Name, StringComparer.Ordinal);
    private readonly Dictionary<string, string> _jsonRoots = new(StringComparer.Ordinal);
    private readonly List<EmitProblem> _problems = [];
    private readonly List<string> _sendableBags = [];

    private static readonly HashSet<string> s_reservedMembers = new(StringComparer.Ordinal)
    {
        "Equals", "GetHashCode", "ToString", "GetType", "EqualityContract", "PrintMembers", "MemberwiseClone", "ReferenceEquals",
        "AdditionalFields", "FilterableFields", "SearchableFields", "SortableFields", "ModelledWires",
    };

    // The eleven options every queryable .get shares carry the interface's names, whatever the overlay calls them.
    private static readonly Dictionary<string, string> s_commonGetOptions = new(StringComparer.Ordinal)
    {
        ["output"] = "Output",
        ["filter"] = "Filter",
        ["search"] = "Search",
        ["searchByAny"] = "SearchByAny",
        ["startSearch"] = "StartSearch",
        ["excludeSearch"] = "ExcludeSearch",
        ["searchWildcardsEnabled"] = "SearchWildcardsEnabled",
        ["limit"] = "Limit",
        ["sortfield"] = "SortField",
        ["sortorder"] = "SortOrder",
        ["countOutput"] = "CountOutput",
    };

    private const string SortDirections = "global::System.Collections.Generic.IReadOnlyList<global::System.ComponentModel.ListSortDirection>";

    /// <summary>The generated files of one part, and what was refused.</summary>
    /// <param name="part">Which assembly is being built.</param>
    /// <param name="cancellationToken">Stops between files.</param>
    public EmitResult Emit(ZabbixApiPart part, CancellationToken cancellationToken = default)
    {
        _problems.Clear();
        _jsonRoots.Clear();
        _sendableBags.Clear();
        var files = new List<GeneratedSource>();
        if (part == ZabbixApiPart.Models)
        {
            foreach (EnumDescription e in api.Enums)
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(new(Prefixed(e.Name) + ".g.cs", EmitEnum(e)));
            }
            foreach (TypeDescription t in api.Types)
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(new(Prefixed(t.Name) + ".g.cs", EmitRecord(t)));
            }
            files.AddRange(EmitFieldGrammar());
            files.Add(new("ZabbixApiMethods.g.cs", EmitCapabilities()));
            files.Add(new("ZabbixApiSuppressions.g.cs", EmitSuppressions()));
        }
        else
        {
            foreach (ServiceDescription service in api.Services)
            {
                cancellationToken.ThrowIfCancellationRequested();
                files.Add(new(service.Client + ".g.cs", EmitClient(service)));
            }
            files.Add(new("ZabbixApi.g.cs", EmitAggregate(api.Services)));
            files.Add(new("ZabbixClient.Clients.g.cs", EmitClientProperties(api.Services)));
        }
        return new([.. files], [.. _problems]);
    }

    /// <summary>The JSON context source (<c>ZabbixApiJson.g.cs</c>): every record and every list the clients send or
    /// receive, as one <c>JsonSerializerContext</c>; with what the clients' emission refused.</summary>
    /// <param name="cancellationToken">Stops between files.</param>
    public EmitResult ContextSource(CancellationToken cancellationToken = default)
    {
        EmitResult clients = Emit(ZabbixApiPart.Clients, cancellationToken);
        return new([new("ZabbixApiJson.g.cs", EmitContext())], clients.Problems);
    }

    // ----- names -------------------------------------------------------------------------------------------

    private static string Prefixed(string name) => name.StartsWith("Zabbix", StringComparison.Ordinal) ? name : "Zabbix" + name;

    private static string Member(string name) => CodeText.Identifier(s_reservedMembers.Contains(name) ? name + "Value" : name);

    // The description's sentences may carry <c>…</c>, <b>…</b> and <see cref="…"/>; anything else is escaped (CodeText).
    private static string Doc(string? summary, string fallback) => CodeText.Doc(summary, fallback);

    private static string Lit(string value) => CodeText.Literal(value);

    private static string Header(string range, string summary) =>
        "// Emitted by Zabbix.Generator from the Zabbix " + range + " API description (schema/zabbix-api.json). Do not edit.\n#nullable enable\nnamespace Community.Zabbix;\n\n/// <summary>" + summary + "</summary>\n";

    private static string Versioning(string? since, string? removed, string? replacement, string indent)
    {
        var code = new StringBuilder();
        if (since is not null) code.Append(indent).Append("[global::Community.Zabbix.ZabbixSupported(").Append(Lit(since)).Append(")]\n");
        if (removed is not null)
        {
            code.Append(indent).Append("[global::Community.Zabbix.ZabbixUnsupported(").Append(Lit(removed));
            if (replacement is not null) code.Append(", ").Append(Lit(replacement));
            code.Append(")]\n");
        }
        return code.ToString();
    }

    /// <summary>A kind as a C# type (never nullable; the caller adds <c>?</c>), or null (and a problem) when the kind is not one the generator knows.</summary>
    private string? ClrType(string kind, string record, string? wire)
    {
        switch (kind)
        {
            case "string":
                return "string";
            case "id":
                return "ulong";
            case "clock":
                return "global::System.DateTimeOffset";
            case "date":
                return "global::System.DateOnly";
            case "timeofday":
                return "global::System.TimeOnly";
            case "int":
                return "int";
            case "long":
                return "long";
            case "ulong":
                return "ulong";
            case "double":
                return "double";
            case "bool" or "apibool":
                return "bool";
            case "json":
                return "global::System.Text.Json.JsonElement";
            case "output":
                return "global::Community.Zabbix.ZabbixOutput";
            case "filter":
                return "global::System.Collections.Generic.IReadOnlyDictionary<string, global::Community.Zabbix.ZabbixFieldValue>";
            default:
                break;
        }
        if (kind.StartsWith("enum:", StringComparison.Ordinal))
        {
            string name = kind[5..];
            if (_enums.ContainsKey(name)) return "global::Community.Zabbix." + Prefixed(name);
            _problems.Add(new(ZabbixDiagnostics.Undeclared, $"'{kind}' names an enum the description does not declare", record, wire));
            return null;
        }
        if (kind.StartsWith("object:", StringComparison.Ordinal))
        {
            string name = kind[7..];
            if (_types.ContainsKey(name)) return "global::Community.Zabbix." + Prefixed(name);
            _problems.Add(new(ZabbixDiagnostics.Undeclared, $"'{kind}' names a record the description does not declare", record, wire));
            return null;
        }
        if (kind.StartsWith("list:", StringComparison.Ordinal))
        {
            string? element = ClrType(kind[5..], record, wire);
            return element is null ? null : "global::System.Collections.Generic.IReadOnlyList<" + element + ">";
        }
        if (kind.StartsWith("map:", StringComparison.Ordinal))
        {
            string? element = ClrType(kind[4..], record, wire);
            return element is null ? null : "global::System.Collections.Generic.IReadOnlyDictionary<string, " + element + ">";
        }
        _problems.Add(new(ZabbixDiagnostics.UnknownKind, $"'{kind}' is not a kind the generator knows", record, wire));
        return null;
    }

    private string Root(string clrType)
    {
        if (!_jsonRoots.TryGetValue(clrType, out string? name))
        {
            var builder = new StringBuilder();
            foreach (string qualified in clrType.Split(['<', '>', ',', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                string word = qualified[(qualified.LastIndexOf('.') + 1)..];
                builder.Append(word switch { "string" => "String", "bool" => "Boolean", "int" => "Int32", "long" => "Int64", "double" => "Double", _ => word });
            }
            name = builder.ToString();
            _jsonRoots[clrType] = name;
        }
        return "global::Community.Zabbix.ZabbixApiJson.Default." + name;
    }

    // ----- enums -------------------------------------------------------------------------------------------

    private string EmitEnum(EnumDescription e)
    {
        string name = Prefixed(e.Name);
        var code = new StringBuilder(Header(api.Range, Doc(DocSentences.Enum(e.Summary, e.Name, e.Family, e.Flags), e.Family)));
        if (e.Flags) code.Append("[global::System.Flags]\n");
        // The generated code is analysed like hand-written code (.editorconfig: generated_code = false), and an
        // .editorconfig severity does not reach a source-generated tree (CA1720 = none in [*.cs] leaves CA1720 errors in
        // this output), so a design rule the Zabbix numbering answers is suppressed here, on the one symbol, with its reason.
        long[] values = [.. e.Members.Select(m => m.Value)];
        if (!e.Flags && !values.Contains(0))
            code.Append(Suppress("Design", "CA1008:Enums should have zero value", "Zabbix's own numbering (" + e.Family + "_*) has no zero; a None = 0 member would be a value the API refuses."));
        if (!e.Flags && LooksLikeFlags(values))
            code.Append(Suppress("Design", "CA1027:Mark enums with FlagsAttribute", "One code per object, not bits: the API validates a single " + e.Family + "_* value (its 'in' list), and values that happen to be powers of two do not combine."));
        code.Append("[global::System.Text.Json.Serialization.JsonConverter(typeof(global::Community.Zabbix.ZabbixEnumConverter<").Append(name).Append(">))]\n");
        code.Append("public enum ").Append(name).Append("\n{\n");
        foreach (EnumMember member in e.Members)
        {
            string text = DocSentences.Member(member.Summary, member.Name, member.Define, member.Value);
            code.Append("    /// <summary>").Append(Doc(text, member.Name)).Append("</summary>\n");
            code.Append(Versioning(member.Since, member.Removed, null, "    "));
            // Float, Unsigned, Single, Int32, String: Zabbix's own word for the value (ITEM_VALUE_TYPE_FLOAT is "Numeric
            // (float)" in its documentation and frontend); an enum member's wire form is its number, so no attribute keeps
            // the Zabbix word if the member is renamed
            if (s_typeWords.Contains(member.Name))
                code.Append("    ").Append(Suppress("Naming", "CA1720:Identifiers should not contain type names", "Zabbix's own name for the value (" + (member.Define ?? e.Family + "_*") + "); the wire value is the number."));
            code.Append("    ").Append(CodeText.Identifier(member.Name)).Append(" = ").Append(member.Value.ToString(CultureInfo.InvariantCulture)).Append(",\n");
        }
        return code.Append("}\n").ToString();
    }

    // The words CA1720 reads as type names (the extractor renames a record member named so; an enum member keeps it).
    private static readonly HashSet<string> s_typeWords = new(StringComparer.Ordinal)
    {
        "Object", "Obj", "String", "Char", "Int", "Integer", "Long", "Short", "Float", "Double", "Decimal", "Single",
        "Signed", "Unsigned", "Guid", "Pointer", "Ptr", "Int16", "Int32", "Int64", "UInt16", "UInt32", "UInt64",
    };

    private static string Suppress(string category, string rule, string justification)
        => "[global::System.Diagnostics.CodeAnalysis.SuppressMessage(\"" + category + "\", \"" + rule + "\", Justification = " + Lit(justification) + ")]\n";

    // CA1027's own test (every enum of this description checked against it): the values are not one
    // contiguous run, and each non-zero value is a power of two or the union of the other values it contains.
    private static bool LooksLikeFlags(long[] values)
    {
        ulong[] sorted = [.. values.Select(v => unchecked((ulong)v)).Distinct().OrderBy(v => v)];
        ulong[] nonZero = [.. sorted.Where(v => v != 0)];
        if (nonZero.Length == 0) return false;
        bool contiguous = true;
        for (int i = 1; i < sorted.Length; i++) contiguous &= sorted[i] - sorted[i - 1] == 1;
        if (contiguous) return false;
        foreach (ulong v in nonZero)
        {
            if ((v & (v - 1)) == 0) continue;
            ulong union = 0;
            foreach (ulong w in nonZero) if (w != v && (w & v) == w) union |= w;
            if (union != v) return false;
        }
        return true;
    }

    /// <summary>
    /// CA1056 on a string member one of whose words is Url, Uri or Urn (the analyzer's own test): suppressed on that
    /// member with its reason, shared by the records and the PowerShell parameters that set them; empty otherwise.
    /// </summary>
    /// <param name="member">The C# member name.</param>
    /// <param name="indent">The member's indentation.</param>
    internal static string UrlSuppression(string member, string indent)
    {
        bool url = false;
        for (int start = 0; start < member.Length;)
        {
            int end = start + 1;
            while (end < member.Length && !char.IsUpper(member[end])) end++;
            url |= member.AsSpan(start, end - start) is "Url" or "Uri" or "Urn";
            start = end;
        }
        if (!url) return string.Empty;
        string why = member switch
        {
            // url_name: the text a menu shows for the link
            _ when member.EndsWith("Name", StringComparison.Ordinal) => "The link's display name, not a URL.",
            // uri_valid_schemes: the settings' comma-separated scheme list
            _ when member.EndsWith("Schemes", StringComparison.Ordinal) => "A comma-separated list of URI schemes, not a URI.",
            _ => "Zabbix URL text (CUrlValidator.php 8.0:60-160): a relative path is a URL and a macro ({$MACRO}, {EVENT.TAGS.x}, {INVENTORY.URL.A}) passes unparsed, so the wire value is not a System.Uri.",
        };
        return indent + Suppress("Design", "CA1056:URI-like properties should not be strings", why);
    }

    // ----- records -----------------------------------------------------------------------------------------

    // Which bag a record carries. The extension-data bag a caller (the PowerShell -Properties hashtable, or any SDK user
    // on a newer Zabbix) can use to send a field this record does not model is scoped to the top-level records a caller
    // builds and sends: *CreateRequest/*UpdateRequest/*MassUpdateRequest (role "request") and *GetParameters (role
    // "get"). Not "nested-request" (a TagFilter, a HostGroupRequest nested in one of those): those are small, closed
    // shapes the parent already covers field-by-field, and giving every nested object its own bag would multiply the
    // surface for no caller need identified so far. A read record's bag is not one of its public properties:
    // IZabbixAdditionalFields, implemented explicitly.
    private sealed record RecordShape(bool SendableBag, bool ReadBag, bool Request)
    {
        public bool HasBag => ReadBag || SendableBag;

        public static RecordShape Of(TypeDescription t)
        {
            bool sendable = t.Role is "request" or "get";
            return new(sendable, t.ExtensionData && !sendable, !t.IsRead && t.Role != "get");
        }
    }

    // What the members a record declares feed the steps after them: the wire names its serialization guard knows, the
    // text form's members and the equality's members.
    private sealed record RecordMembers(List<string> Wires, List<(string Member, string Kind, bool Secret, bool Obsolete, bool Optional)> Printed, List<(string Member, string Type, string Kind, bool Obsolete)> Compared);

    // One record: its declaration, its members, its bag and the guard on it, its text form, its equality and, on a get's
    // parameters, the field sets the API validates.
    private string EmitRecord(TypeDescription t)
    {
        string name = Prefixed(t.Name);
        RecordShape shape = RecordShape.Of(t);
        var code = new StringBuilder(Header(api.Range, Doc(t.Summary, t.Name)));
        EmitRecordDeclaration(code, t, name, shape);
        RecordMembers members = EmitRecordFields(code, t, name, shape);
        if (shape.Request) EmitIdConversion(code, t, name);
        if (shape.HasBag) EmitAdditionalFields(code, t, name, shape);
        if (shape.SendableBag) EmitSerializingGuard(code, name, members.Wires);
        EmitPrintMembers(code, members.Printed, shape.HasBag);
        EmitEquality(code, name, members.Compared, shape.HasBag);
        if (t.Role == "get") EmitFieldSets(code, t);
        while (code.Length > 1 && code[^1] == '\n' && code[^2] == '\n') code.Length--;
        return code.Append("}\n").ToString();
    }

    // A request that is only an id reads as the id: Groups = [groupId], as a caller would write it.
    private static void EmitIdConversion(StringBuilder code, TypeDescription t, string name)
    {
        if (t.Fields.Length != 1 || t.Fields[0] is not { Required: true, Kind: "id" } field) return;
        string member = Member(field.Name);
        code.Append("    /// <summary>Converts an id to the request that names it.</summary>\n    /// <param name=\"id\">The id to name.</param>\n    /// <returns>The request.</returns>\n");
        code.Append("    public static implicit operator ").Append(name).Append("(ulong id) => FromUInt64(id);\n\n");
        code.Append("    /// <summary>The request that names an id.</summary>\n    /// <param name=\"id\">The id to name.</param>\n    /// <returns>The request.</returns>\n");
        code.Append("    public static ").Append(name).Append(" FromUInt64(ulong id) => new() { ").Append(member).Append(" = id };\n\n");
    }

    private static void EmitRecordDeclaration(StringBuilder code, TypeDescription t, string name, RecordShape shape)
    {
        code.Append("public sealed partial record ").Append(name);
        List<string> bases = [];
        if (t.GetParameters) bases.Add("global::Community.Zabbix.IZabbixGetParameters");
        if (shape.ReadBag) bases.Add("global::Community.Zabbix.IZabbixAdditionalFields");
        if (shape.SendableBag) bases.Add("global::System.Text.Json.Serialization.IJsonOnSerializing");
        if (bases.Count > 0) code.Append(" : ").Append(string.Join(", ", bases));
        code.Append("\n{\n");
    }

    // Where a human names the field: its entry in schema/overlay.json, as a JSON pointer (RFC 6901). The description is
    // generated from the overlay, so a name written there survives the next regeneration; one written in zabbix-api.json
    // does not.
    private static string OverlayField(TypeDescription t, FieldDescription field)
        => "schema/overlay.json#/types/" + Pointer(t.DescribedName ?? t.Name) + "/fields/" + Pointer(field.Wire) + "/name";

    private static string Pointer(string token) => token.Replace("~", "~0").Replace("/", "~1");

    // Every field a member, in description order; a field whose name collides or whose kind has no CLR type is reported
    // and left out.
    private RecordMembers EmitRecordFields(StringBuilder code, TypeDescription t, string name, RecordShape shape)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var members = new RecordMembers([], [], []);
        foreach (FieldDescription field in t.Fields)
        {
            string member = Member(t.Role == "get" && s_commonGetOptions.TryGetValue(field.Wire, out string? common) ? common : field.Name);
            if (member == name)
            {
                _problems.Add(new(ZabbixDiagnostics.FieldCollision, $"field '{field.Wire}' would be named {member}, the record's own name; name it at {OverlayField(t, field)}", t.Name, field.Wire));
                continue;
            }
            if (!seen.Add(member))
            {
                _problems.Add(new(ZabbixDiagnostics.FieldCollision, $"field '{field.Wire}' would be named {member}, which another field of {t.Name} already is; name it at {OverlayField(t, field)}", t.Name, field.Wire));
                continue;
            }
            // the get grammar's sortorder is a closed pair of words, the BCL's ListSortDirection
            string? type = t.Role == "get" && field.Wire == "sortorder" ? SortDirections : ClrType(field.Kind, t.Name, field.Wire);
            if (type is null) continue;
            if (t.Role == "get" && field.Kind == "filter" && field.Wire is "filter" or "search")
                EmitConditions(code, t, field, member, ConditionsType(t, field.Wire == "search"), members);
            else
                EmitRecordField(code, t, field, member, type, shape.Request, members);
        }
        return members;
    }

    private static void EmitRecordField(StringBuilder code, TypeDescription t, FieldDescription field, string member, string type, bool request, RecordMembers members)
    {
        if (field.Nanoseconds is not null)
        {
            EmitClockPair(code, field, member, members);
            return;
        }
        // a request's required field, and a .get's own (hostdashboard.get's hostids): the record cannot be built without it
        bool required = (request || t.Role == "get") && field.Required;
        // a flag stays bool? even when required: its converters are JsonConverter<bool?>
        string nullable = field.Kind is "bool" or "apibool" ? "?" : string.Empty;
        // an option whose null is a request of its own (history.get's maxValueSize): absent, null or a value
        bool optional = field.Nullable && !required && field.Kind is "int" or "long" or "double";
        if (optional) type = "global::Community.Zabbix.ZabbixOptional<" + type + ">";
        code.Append("    /// <summary>").Append(Doc(DocSentences.Field(field.Summary, member, field.Wire), field.Wire)).Append("</summary>\n");
        if (field.Json is not null) code.Append("    /// <remarks>Untyped JSON: ").Append(Doc(field.Json, "untyped")).Append(".</remarks>\n");
        code.Append("    [global::System.Text.Json.Serialization.JsonPropertyName(").Append(Lit(field.Wire)).Append(")]\n");
        string? converter = field.Kind switch
        {
            "bool" => "global::Community.Zabbix.ZabbixFlagConverter",
            "apibool" => "global::Community.Zabbix.ZabbixBoolConverter",
            // PHP writes an empty object as [] (json_encode of an empty array): a read-side object reads [] as absent
            _ when t.IsRead && field.Kind.StartsWith("object:", StringComparison.Ordinal) => "global::Community.Zabbix.ZabbixObjectOrEmptyArrayConverter<" + type + ">",
            // PHP answers some text lists as one string on some rows (a user's media sendto: an array for e-mail, a
            // string for SMS, CUser.php 8.0:3540): a read list of text reads either
            "list:string" when t.IsRead => "global::Community.Zabbix.ZabbixTextListConverter",
            // an instant the API writes as Unix seconds, a number or its text
            "clock" => "global::Community.Zabbix.ZabbixUnixTimeConverter",
            // a date the API writes as YYYY-MM-DD (API_DATE)
            "date" => "global::Community.Zabbix.ZabbixDateConverter",
            // a time of day the API writes as seconds after midnight
            "timeofday" => "global::Community.Zabbix.ZabbixTimeOfDayConverter",
            _ => null,
        };
        if (optional)
        {
            converter = "global::Community.Zabbix.ZabbixOptionalConverter<" + type[(type.IndexOf('<') + 1)..^1] + ">";
            code.Append("    [global::System.Text.Json.Serialization.JsonIgnore(Condition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]\n");
        }
        if (converter is not null) code.Append("    [global::System.Text.Json.Serialization.JsonConverter(typeof(").Append(converter).Append("))]\n");
        code.Append(Versioning(field.Since, field.Removed, field.Replacement is null ? null : "use " + field.Replacement, "    "));
        if (field.Deprecated is not null)
            code.Append("    [global::System.Obsolete(\"Deprecated since Zabbix ").Append(field.Deprecated).Append(" (API_DEPRECATED).\")]\n");
        // StringLength, not MaxLength: MaxLengthAttribute(int)'s validation covers both strings and collections, so it
        // checks for ICollection by reflection at runtime (RequiresUnreferencedCode, IL2026 under trimming) even for a
        // string. StringLengthAttribute is the string-only rule and needs no reflection — trim-safe, same check.
        if (field.MaxLength is int length && field.Kind == "string")
            code.Append("    [global::System.ComponentModel.DataAnnotations.StringLength(").Append(length.ToString(CultureInfo.InvariantCulture)).Append(")]\n");
        if (required) code.Append("    [global::System.Text.Json.Serialization.JsonRequired]\n");
        // a secret is classified, so the redactor sees it wherever the record is logged
        if (field.Secret) code.Append("    [global::Community.Zabbix.ZabbixSecret]\n");
        if (type == "string") code.Append(UrlSuppression(member, "    "));
        members.Printed.Add((member, field.Kind, field.Secret, field.Deprecated is not null, optional));
        members.Compared.Add((member, type + (required ? nullable : optional ? string.Empty : "?"), field.Kind, field.Deprecated is not null));
        // a get's parameters are filled in a lambda: GetAsync(q => q.Limit = 10)
        string accessors = t.Role == "get" ? " { get; set; }\n\n" : " { get; init; }\n\n";
        code.Append("    public ").Append(required ? "required " : string.Empty).Append(type).Append(required ? nullable + " " : optional ? " " : "? ").Append(member).Append(accessors);
        members.Wires.Add(field.Wire);
    }

    // A get's filter or search: never null, so q.Filter.Status = [...] needs no new, over the dictionary the wire carries,
    // which is absent when nothing is set.
    private static void EmitConditions(StringBuilder code, TypeDescription t, FieldDescription field, string member, string type, RecordMembers members)
    {
        string wire = member.TrimStart('@') + "Fields";
        const string Dictionary = "global::System.Collections.Generic.IReadOnlyDictionary<string, global::Community.Zabbix.ZabbixFieldValue>";
        code.Append("    /// <summary>").Append(Doc(DocSentences.Field(field.Summary, member, field.Wire), field.Wire)).Append("</summary>\n");
        code.Append("    [global::System.Text.Json.Serialization.JsonIgnore]\n");
        code.Append("    [global::System.Text.Json.Serialization.JsonPropertyName(").Append(Lit(field.Wire)).Append(")]\n");
        code.Append("    [global::System.Diagnostics.CodeAnalysis.AllowNull]\n");
        code.Append("    public ").Append(type).Append(' ').Append(member).Append(" { get => field ??= new(); set; }\n\n");
        if (t.GetParameters) code.Append("    global::Community.Zabbix.ZabbixGetConditions global::Community.Zabbix.IZabbixGetParameters.").Append(member).Append(" => ").Append(member).Append(";\n\n");
        code.Append("    [global::System.Text.Json.Serialization.JsonInclude]\n");
        code.Append("    [global::System.Text.Json.Serialization.JsonPropertyName(").Append(Lit(field.Wire)).Append(")]\n");
        code.Append("    internal ").Append(Dictionary).Append("? ").Append(wire).Append("\n    {\n");
        code.Append("        get => ").Append(member).Append(".Count > 0 ? ").Append(member).Append(" : null;\n");
        code.Append("        init => ").Append(member).Append(" = new(value);\n    }\n\n");
        members.Printed.Add((wire, "conditions", false, false, false));
        members.Compared.Add((wire, Dictionary + "?", "filter", false));
        members.Wires.Add(field.Wire);
    }

    // A clock and its nanoseconds (clock and ns): one DateTimeOffset member over two wire members the JSON contract reads
    // and writes, so the record says one time and the wire keeps its two fields. An internal member keeps the exact
    // instant for the SDK's own reads, which page by it. The nanoseconds are checked as they are read (0 to 999,999,999),
    // so the member never throws.
    private static void EmitClockPair(StringBuilder code, FieldDescription field, string member, RecordMembers members)
    {
        string seconds = member.TrimStart('@') + "Seconds";
        string nanoseconds = member.TrimStart('@') + "Nanoseconds";
        const string Fields = "global::Community.Zabbix.ZabbixClockFields";
        code.Append("    /// <summary>").Append(Doc(DocSentences.Field(field.Summary, member, field.Wire), field.Wire))
            .Append(" To 100 ns, as a <see cref=\"global::System.DateTimeOffset\"/> holds it; the wire's <c>").Append(field.Nanoseconds).Append("</c> field carries the rest.</summary>\n");
        code.Append("    [global::System.Text.Json.Serialization.JsonIgnore]\n");
        code.Append(Versioning(field.Since, field.Removed, field.Replacement is null ? null : "use " + field.Replacement, "    "));
        code.Append("    public global::System.DateTimeOffset? ").Append(member).Append("\n    {\n");
        code.Append("        get => ").Append(Fields).Append(".Time(").Append(seconds).Append(", ").Append(nanoseconds).Append(");\n");
        code.Append("        init => (").Append(seconds).Append(", ").Append(nanoseconds).Append(") = ").Append(Fields).Append(".Split(value);\n    }\n\n");
        code.Append("    internal global::Community.Zabbix.ZabbixClock? ").Append(member.TrimStart('@')).Append("Exact => ").Append(Fields).Append(".Join(").Append(seconds).Append(", ").Append(nanoseconds).Append(");\n\n");
        code.Append("    [global::System.Text.Json.Serialization.JsonInclude]\n");
        code.Append("    [global::System.Text.Json.Serialization.JsonPropertyName(").Append(Lit(field.Wire)).Append(")]\n");
        code.Append("    internal long? ").Append(seconds).Append(" { get; init; }\n\n");
        code.Append("    [global::System.Text.Json.Serialization.JsonInclude]\n");
        code.Append("    [global::System.Text.Json.Serialization.JsonPropertyName(").Append(Lit(field.Nanoseconds!)).Append(")]\n");
        code.Append("    internal int? ").Append(nanoseconds).Append(" { get; init => field = ").Append(Fields).Append(".Nanoseconds(value, ").Append(Lit(field.Nanoseconds!)).Append("); }\n\n");
        members.Printed.Add((member, field.Kind, false, false, false));
        members.Compared.Add((member.TrimStart('@') + "Exact", "global::Community.Zabbix.ZabbixClock?", field.Kind, false));
        members.Wires.Add(field.Wire);
        members.Wires.Add(field.Nanoseconds!);
    }

    // The bag: a read record keeps what the server returned that the description does not model, a sendable record
    // takes what a caller sends beyond it.
    private void EmitAdditionalFields(StringBuilder code, TypeDescription t, string name, RecordShape shape)
    {
        if (t.ExtensionData)
        {
            // A field the server returns that the description does not model is kept, not dropped. A newer Zabbix adds
            // fields before the SDK knows them; refusing the whole reply (JsonUnmappedMemberHandling.Disallow) would turn a
            // harmless addition into a failed call, and ignoring it (the default) would lose it silently.
            code.Append("    /// <summary>Every field the server returned that the description does not model (a newer Zabbix, or a field the\n");
            code.Append("    /// extraction could not type): kept verbatim, never dropped. <see langword=\"null\"/> on a server the description covers.</summary>\n");
        }
        else
        {
            // A caller (the PowerShell -Properties hashtable, or an SDK user on a newer Zabbix) can set a wire field this
            // record does not model. Sent verbatim, beside the modelled fields, on the next serialization.
            code.Append("    /// <summary>A wire field this record does not model, to send anyway (a newer Zabbix, or the PowerShell\n");
            code.Append("    /// <c>-Properties</c> hashtable's escape hatch). A key that names a field this record already models is\n");
            code.Append("    /// refused at serialization time (see the <c>OnSerializing</c> guard below), not sent twice.</summary>\n");
        }
        // A setter, not init: System.Text.Json's source generator binds init-only members through its constructor
        // delegate, which extension data cannot join (it throws InvalidOperationException).
        code.Append("    [global::System.Text.Json.Serialization.JsonExtensionData]\n");
        if (shape.ReadBag)
        {
            // internal (the SDK's JSON context sees it through InternalsVisibleTo) and reached through the interface:
            // ConvertTo-Json reflects public properties and honours only Newtonsoft's [JsonIgnore]
            code.Append("    [global::System.Text.Json.Serialization.JsonInclude]\n");
            code.Append("    internal global::System.Collections.Generic.IDictionary<string, global::System.Text.Json.JsonElement>? AdditionalFields { get; set; }\n\n");
            code.Append("    /// <inheritdoc/>\n");
            code.Append("    global::System.Collections.Generic.IDictionary<string, global::System.Text.Json.JsonElement>? global::Community.Zabbix.IZabbixAdditionalFields.AdditionalFields => AdditionalFields;\n\n");
        }
        else
        {
            // CA2227 is answered once for every bag, in ZabbixApiSuppressions.g.cs (EmitSuppressions)
            _sendableBags.Add(name);
            code.Append("    public global::System.Collections.Generic.IDictionary<string, global::System.Text.Json.JsonElement>? AdditionalFields { get; set; }\n\n");
        }
    }

    // System.Text.Json writes extension data after the modelled properties without checking for a name collision, so a
    // bag key equal to a modelled field's wire name serializes as a duplicate JSON member (`{"host":"h","host":"dup"}`):
    // which member a reader keeps is the reader's choice, not a contract this SDK can promise. IJsonOnSerializing runs
    // under the source generator (no reflection needed), so the collision is refused before anything is sent, rather
    // than shipped as silently ambiguous wire bytes.
    private static void EmitSerializingGuard(StringBuilder code, string name, List<string> wires)
    {
        // the modelled wire names once per type, not a new set on every serialization (CA1861)
        code.Append("    private static readonly global::System.Collections.Frozen.FrozenSet<string> ModelledWires = ").Append(Set([.. wires])).Append(";\n\n");
        code.Append("    void global::System.Text.Json.Serialization.IJsonOnSerializing.OnSerializing()\n");
        code.Append("        => global::Community.Zabbix.ZabbixExtensionDataGuard.Refuse(AdditionalFields, ").Append(Lit(name)).Append(", ModelledWires);\n\n");
    }

    // A get's parameters name the fields filter, search and sortfield accept, as the API validates them.
    private static void EmitFieldSets(StringBuilder code, TypeDescription t)
    {
        code.Append("    /// <summary>The fields <c>filter</c> accepts, as <c>").Append(t.Method ?? t.Name).Append("</c> validates them; empty when the API names no list (any column).</summary>\n");
        code.Append("    public static global::System.Collections.Frozen.FrozenSet<string> FilterableFields { get; } = ").Append(Set(t.Filterable)).Append(";\n\n");
        code.Append("    /// <summary>The fields <c>search</c> accepts, as <c>").Append(t.Method ?? t.Name).Append("</c> validates them; empty when the API names no list (any text column).</summary>\n");
        code.Append("    public static global::System.Collections.Frozen.FrozenSet<string> SearchableFields { get; } = ").Append(Set(t.Searchable)).Append(";\n\n");
        code.Append("    /// <summary>The fields <c>sortfield</c> accepts, as <c>").Append(t.Method ?? t.Name).Append("</c> checks them (its <c>sortfield</c> rule, else the service's <c>$sortColumns</c>); empty when the API sorts by nothing.</summary>\n");
        code.Append("    public static global::System.Collections.Frozen.FrozenSet<string> SortableFields { get; } = ").Append(Set(t.Sortable)).Append(";\n\n");
    }

    /// <summary>
    /// A record's compiler-written <c>ToString()</c> prints every property: a read record's forty members, most of them
    /// empty (<c>MonitoredBy = ,</c>), a list as its type name, and a PSK, a password or a token in clear into an exception
    /// message, a log line, a debugger or PowerShell's default view. Every record writes its own <c>PrintMembers</c> (the
    /// hook the compiler's <c>ToString()</c> calls, C# records spec) over <c>ZabbixRecordText</c>: a member that is set, a
    /// list or dictionary as how many it holds, a secret as <c>***</c>, the bag as how many fields it keeps.
    /// </summary>
    private static void EmitPrintMembers(StringBuilder code, List<(string Member, string Kind, bool Secret, bool Obsolete, bool Optional)> printed, bool bag)
    {
        const string Text = "global::Community.Zabbix.ZabbixRecordText";
        static bool Counted(string kind) => kind is "filter" or "conditions" || kind.StartsWith("list:", StringComparison.Ordinal) || kind.StartsWith("map:", StringComparison.Ordinal);
        bool obsolete = printed.Exists(p => p.Obsolete);
        code.Append("    private bool PrintMembers(global::System.Text.StringBuilder builder)\n    {\n");
        code.Append("        bool more = false;\n");
        if (obsolete) code.Append("#pragma warning disable CS0618 // the text form reads the deprecated members too, as the compiler's would\n");
        foreach ((string member, string kind, bool secret, _, bool optional) in printed)
        {
            string label = Lit(kind == "conditions" ? member[..^"Fields".Length].TrimStart('@') : member.TrimStart('@'));
            code.Append("        ").Append(Text).Append(
                secret ? ".Secret(builder, ref more, " + label + ", " + member + " is not null);\n"
                : Counted(kind) ? ".Count(builder, ref more, " + label + ", " + member + ");\n"
                : optional ? ".Member(builder, ref more, " + label + ", " + member + ".IsSent ? " + member + " : null);\n"
                : ".Member(builder, ref more, " + label + ", " + member + ");\n");
        }
        if (obsolete) code.Append("#pragma warning restore CS0618\n");
        if (bag) code.Append("        ").Append(Text).Append(".Count(builder, ref more, \"AdditionalFields\", AdditionalFields);\n");
        code.Append("        return more;\n    }\n\n");
    }

    /// <summary>
    /// Value equality over the record's lists, filters, JSON and bag: the compiler's synthesized <c>Equals</c> compares an
    /// <c>IReadOnlyList</c>, an <c>IReadOnlyDictionary</c>, the <c>AdditionalFields</c> bag and a <c>JsonElement</c> by
    /// reference, so two records read from equal replies would be unequal. A record with such a member writes its own
    /// <c>Equals(T?)</c> and <c>GetHashCode()</c> (the compiler's <c>==</c>, <c>!=</c> and <c>Equals(object)</c> call them,
    /// C# records spec) over <c>ZabbixValueEquality</c>: a list element by element in order, a dictionary key by key, JSON by
    /// <c>JsonElement.DeepEquals</c>, every other member as the compiler would. A record of scalars keeps the compiler's.
    /// </summary>
    private static void EmitEquality(StringBuilder code, string name, List<(string Member, string Type, string Kind, bool Obsolete)> members, bool bag)
    {
        static string Shape(string kind) => kind.StartsWith("list:", StringComparison.Ordinal) ? "list" : kind is "filter" || kind.StartsWith("map:", StringComparison.Ordinal) ? "dictionary" : kind is "json" ? "json" : "value";
        if (!bag && !members.Exists(m => Shape(m.Kind) != "value")) return;
        bool obsolete = members.Exists(m => m.Obsolete);
        const string Equality = "global::Community.Zabbix.ZabbixValueEquality";
        code.Append("    /// <summary>Whether <paramref name=\"other\"/> holds the same values: a list element by element in order, a dictionary key\n");
        code.Append("    /// by key, JSON by its content, every other member as a record compares it.</summary>\n");
        code.Append("    /// <param name=\"other\">The record to compare with this one; <see langword=\"null\"/> is never equal.</param>\n");
        code.Append("    /// <returns><see langword=\"true\"/> when every member holds the same value; otherwise <see langword=\"false\"/>.</returns>\n");
        code.Append("    public bool Equals(").Append(name).Append("? other)\n    {\n");
        code.Append("        if (ReferenceEquals(this, other)) return true;\n");
        code.Append("        if (other is null) return false;\n");
        if (obsolete) code.Append("#pragma warning disable CS0618 // equality reads the deprecated members too, as the compiler's would\n");
        code.Append("        return true");
        foreach ((string member, string type, string kind, _) in members)
        {
            code.Append("\n            && ");
            code.Append(Shape(kind) switch
            {
                "list" => Equality + ".ListEqual(" + member + ", other." + member + ")",
                "dictionary" => Equality + ".DictionaryEqual(" + member + ", other." + member + ")",
                "json" => Equality + ".JsonEqual(" + member + ", other." + member + ")",
                _ => "global::System.Collections.Generic.EqualityComparer<" + type + ">.Default.Equals(" + member + ", other." + member + ")",
            });
        }
        if (bag) code.Append("\n            && ").Append(Equality).Append(".DictionaryEqual(AdditionalFields, other.AdditionalFields)");
        code.Append(";\n");
        if (obsolete) code.Append("#pragma warning restore CS0618\n");
        code.Append("    }\n\n");
        code.Append("    /// <inheritdoc/>\n    public override int GetHashCode()\n    {\n");
        code.Append("        var hash = new global::System.HashCode();\n");
        if (obsolete) code.Append("#pragma warning disable CS0618 // as Equals\n");
        foreach ((string member, _, string kind, _) in members)
        {
            code.Append(Shape(kind) switch
            {
                "list" => "        " + Equality + ".AddList(ref hash, " + member + ");\n",
                "dictionary" => "        " + Equality + ".AddDictionary(ref hash, " + member + ");\n",
                "json" => "        " + Equality + ".AddJson(ref hash, " + member + ");\n",
                _ => "        hash.Add(" + member + ");\n",
            });
        }
        if (obsolete) code.Append("#pragma warning restore CS0618\n");
        if (bag) code.Append("        ").Append(Equality).Append(".AddDictionary(ref hash, AdditionalFields);\n");
        code.Append("        return hash.ToHashCode();\n    }\n\n");
    }

    private static string Set(EquatableArray<string> values)
        => values.IsEmpty
            ? "global::System.Collections.Frozen.FrozenSet<string>.Empty"
            : "global::System.Collections.Frozen.FrozenSet.Create<string>(global::System.StringComparer.Ordinal, " + string.Join(", ", values.Select(Lit)) + ")";

    // ----- suppressions -------------------------------------------------------------------------------------

    /// <summary>
    /// The one place a rule is suppressed for a whole family of generated members, counted: CA2227 on every sendable
    /// record's <c>AdditionalFields</c>. The bag is the caller's replaceable, null-by-default escape hatch, settable as
    /// the BCL's own extension-data bags are (<c>ProblemDetails.Extensions</c>): System.Text.Json (through .NET 10)
    /// takes extension data only as a mutable <c>IDictionary</c> and its source generator cannot fill an init-only one,
    /// and a get-only bag created per instance would be shared by a record's <c>with</c> copy.
    /// <c>GeneratedApiTests</c> pins the count to the records reflection finds with a settable bag.
    /// </summary>
    private string EmitSuppressions()
    {
        var code = new StringBuilder("// Emitted by Zabbix.Generator from the Zabbix " + api.Range + " API description (schema/zabbix-api.json). Do not edit.\n");
        code.Append("// CA2227 on the AdditionalFields of ").Append(_sendableBags.Count.ToString(CultureInfo.InvariantCulture))
            .Append(" sendable records: the caller's replaceable, null-by-default escape hatch (ZabbixApiEmitter.EmitSuppressions).\n");
        foreach (string record in _sendableBags.OrderBy(r => r, StringComparer.Ordinal))
        {
            code.Append("[assembly: global::System.Diagnostics.CodeAnalysis.SuppressMessage(\"Usage\", \"CA2227:Collection properties should be read only\", Justification = \"The caller's replaceable, null-by-default extension-data bag; System.Text.Json takes extension data only as a settable IDictionary.\", Scope = \"member\", Target = ")
                .Append(Lit("~P:Community.Zabbix." + record + ".AdditionalFields")).Append(")]\n");
        }
        return code.ToString();
    }

    // ----- the capability table --------------------------------------------------------------------------------

    private string EmitCapabilities()
    {
        string? userType = api.Enums.FirstOrDefault(e => e.Family == "USER_TYPE")?.Name;
        var code = new StringBuilder(Header(api.Range, "Describes every API method per version: since, removed in, the minimum user type (<c>ACCESS_RULES</c>), whether it reads, whether it needs a session (<c>requiresAuthentication()</c>), all read from the Zabbix source."));
        code.Append("public static partial class ZabbixApiMethods\n{\n");
        code.Append("    /// <summary>The Zabbix versions the table describes, oldest first.</summary>\n");
        code.Append("    public static global::System.Collections.Generic.IReadOnlyList<global::System.Version> DescribedVersions { get; } = global::System.Array.AsReadOnly(new global::System.Version[] { ")
            .Append(string.Join(", ", api.Versions.Select(VersionLiteral))).Append(" });\n\n");
        code.Append("    private static global::System.Collections.Frozen.FrozenDictionary<string, global::Community.Zabbix.ZabbixApiMethod> Describe() => global::System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new global::System.Collections.Generic.Dictionary<string, global::Community.Zabbix.ZabbixApiMethod>(global::System.StringComparer.OrdinalIgnoreCase)\n    {\n");
        foreach (ServiceDescription service in api.Services)
        {
            foreach (MethodDescription m in service.Methods)
            {
                string since = m.Since ?? service.Since ?? api.Oldest;
                string? removed = m.Removed ?? service.Removed;
                string min = m.MinUserType is int u
                    ? userType is not null ? "(global::Community.Zabbix." + Prefixed(userType) + ")" + u.ToString(CultureInfo.InvariantCulture) : "(int?)" + u.ToString(CultureInfo.InvariantCulture)
                    : "null";
                code.Append("        [").Append(Lit(m.Api)).Append("] = new(").Append(Lit(m.Api)).Append(", ")
                    .Append(VersionLiteral(since)).Append(", ").Append(removed is null ? "null" : VersionLiteral(removed)).Append(", ")
                    .Append(min).Append(", ")
                    .Append(m.IsRead ? "true" : "false").Append(", ")
                    .Append(m.Idempotent ? "true" : "false").Append(", ")
                    .Append(m.RequiresAuthentication ? "true" : "false").Append(", ")
                    .Append(m.AcceptsAuthenticationSince is null ? "null" : VersionLiteral(m.AcceptsAuthenticationSince)).Append(", ")
                    .Append(m.Replacement is null ? "null" : Lit(m.Replacement)).Append("),\n");
            }
        }
        code.Append("    }, global::System.StringComparer.OrdinalIgnoreCase);\n\n");
        // the request fields a version refuses, per method: what the client checks before it sends (EmitFieldGate)
        code.Append("    private static global::System.Collections.Frozen.FrozenDictionary<string, global::System.Collections.Frozen.FrozenDictionary<string, global::Community.Zabbix.ZabbixApiField>> DescribeFields() => global::System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new global::System.Collections.Generic.Dictionary<string, global::System.Collections.Frozen.FrozenDictionary<string, global::Community.Zabbix.ZabbixApiField>>(global::System.StringComparer.OrdinalIgnoreCase)\n    {\n");
        foreach (ServiceDescription service in api.Services)
        {
            foreach (MethodDescription m in service.Methods)
            {
                ImmutableArray<FieldDescription> refused = RefusedFields(m);
                if (refused.IsEmpty) continue;
                code.Append("        [").Append(Lit(m.Api)).Append("] = global::System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(new global::System.Collections.Generic.Dictionary<string, global::Community.Zabbix.ZabbixApiField>(global::System.StringComparer.Ordinal)\n        {\n");
                foreach (FieldDescription f in refused)
                {
                    code.Append("            [").Append(Lit(f.Wire)).Append("] = new(").Append(Lit(m.Api)).Append(", ").Append(Lit(f.Wire)).Append(", ")
                        .Append(VersionLiteral(f.Removed!)).Append(", ")
                        .Append(f.Replacement is null ? "null" : Lit(f.Replacement)).Append("),\n");
                }
                code.Append("        }, global::System.StringComparer.Ordinal),\n");
            }
        }
        code.Append("    }, global::System.StringComparer.OrdinalIgnoreCase);\n}\n");
        return code.ToString();
    }

    /// <summary>The fields of <paramref name="m"/>'s request (or get parameters) that a version it exists in refuses.</summary>
    private ImmutableArray<FieldDescription> RefusedFields(MethodDescription m)
    {
        string p = m.Parameters;
        string? record = p.StartsWith("list:object:", StringComparison.Ordinal) ? p[12..] : p.StartsWith("object:", StringComparison.Ordinal) ? p[7..] : null;
        if (record is null || !_types.TryGetValue(record, out TypeDescription? t)) return [];
        return [.. t.Fields.Where(f => f.Refused && f.Removed is not null && (m.Removed is null || VersionOrder(f.Removed) < VersionOrder(m.Removed)))];
    }

    private static long VersionOrder(string version)
    {
        string[] parts = version.Split('.');
        return long.Parse(parts[0], CultureInfo.InvariantCulture) * 1000 + (parts.Length > 1 ? long.Parse(parts[1], CultureInfo.InvariantCulture) : 0);
    }

    /// <summary>
    /// The field gate: before sending, a request (or get's parameters) that sets a field the server's version refuses is
    /// refused here, naming the field to send instead (<see cref="RefusedFields"/>; <c>ZabbixApiMethods.EnsureFieldSupported</c>).
    /// The version is only asked for when such a field is set. <paramref name="subject"/> is the expression each check
    /// reads the fields from (one request, or the loop variable over <paramref name="loopOver"/>).
    /// </summary>
    private static void EmitFieldGate(StringBuilder code, string api, ImmutableArray<FieldDescription> refused, string subject, string? loopOver, string? loopType, bool nullable)
    {
        if (refused.IsEmpty) return;
        string Read(FieldDescription f) => subject + (nullable ? "?." : ".") + Member(f.Name);
        // the refused fields are API_DEPRECATED before they go, so their members are [Obsolete]: reading them is the point
        code.Append("#pragma warning disable CS0618\n");
        string indent = "        ";
        // The version is asked for once, and only when a refused field is set: one test over every refused field, then
        // each field's check against that one version (a `fieldServer ??=` per check read as always-null to CA1508 on
        // the first check of a one-request gate)
        string any = string.Join(" || ", refused.Select(f => Read(f) + " is not null"));
        if (loopOver is not null)
        {
            code.Append(indent).Append("global::System.Version? fieldServer = null;\n");
            code.Append(indent).Append("foreach (").Append(loopType).Append(' ').Append(subject).Append(" in ").Append(loopOver).Append(")\n").Append(indent).Append("{\n");
            indent += "    ";
            code.Append(indent).Append("if (").Append(any).Append(")\n").Append(indent).Append("{\n");
            code.Append(indent).Append("    fieldServer ??= await ApiVersion(cancellationToken).ConfigureAwait(false);\n");
        }
        else
        {
            code.Append(indent).Append("if (").Append(any).Append(")\n").Append(indent).Append("{\n");
            code.Append(indent).Append("    global::System.Version fieldServer = await ApiVersion(cancellationToken).ConfigureAwait(false);\n");
        }
        foreach (FieldDescription f in refused)
        {
            code.Append(indent).Append("    if (").Append(Read(f)).Append(" is not null) global::Community.Zabbix.ZabbixApiMethods.EnsureFieldSupported(").Append(Lit(api)).Append(", ").Append(Lit(f.Wire))
                .Append(", fieldServer);\n");
        }
        code.Append(indent).Append("}\n");
        if (loopOver is not null) code.Append("        }\n");
        code.Append("#pragma warning restore CS0618\n");
    }

    private static string VersionLiteral(string version) => "new global::System.Version(" + version.Replace(".", ", ") + ")";

    // ----- clients -----------------------------------------------------------------------------------------

    private string EmitClient(ServiceDescription service)
    {
        string client = service.Client;
        string summary = "The <c>" + service.Object + "</c> API" + (service.Summary is not null ? ": " + Doc(service.Summary, service.Object) : ".");
        var code = new StringBuilder(Header(api.Range, summary));
        code.Append(Versioning(service.Since, service.Removed, null, string.Empty));
        code.Append("public partial class ").Append(client).Append("\n{\n");
        code.Append("    /// <summary>Over a transport, with the server's API version for the methods that are not in every version.</summary>\n");
        code.Append("    /// <param name=\"transport\">The JSON-RPC transport.</param>\n    /// <param name=\"apiVersion\">The server's API version (<c>apiinfo.version</c>, read once by the caller).</param>\n");
        code.Append("    internal ").Append(client).Append("(global::Community.Zabbix.IZabbixTransport transport, global::System.Func<global::System.Threading.CancellationToken, global::System.Threading.Tasks.Task<global::System.Version>> apiVersion)\n    {\n");
        code.Append("        global::System.ArgumentNullException.ThrowIfNull(transport);\n        global::System.ArgumentNullException.ThrowIfNull(apiVersion);\n        Transport = transport;\n        ApiVersion = apiVersion;\n    }\n\n");
        // the Azure SDK's shape for a client a caller's test substitutes: a protected parameterless constructor beside the
        // internal one the API builds, every call virtual (dotnet-client-constructor-for-mocking)
        code.Append("    /// <summary>For a type that derives from this client to substitute it in a test; every call is virtual. A client built\n");
        code.Append("    /// this way has no transport, so a call it does not override fails.</summary>\n");
        code.Append("    protected ").Append(client).Append("()\n    {\n        Transport = null!;\n        ApiVersion = null!;\n    }\n\n");
        code.Append("    /// <summary>The transport every call goes through.</summary>\n    internal global::Community.Zabbix.IZabbixTransport Transport { get; }\n\n");
        code.Append("    /// <summary>The server's API version, for the version gate.</summary>\n    internal global::System.Func<global::System.Threading.CancellationToken, global::System.Threading.Tasks.Task<global::System.Version>> ApiVersion { get; }\n");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (MethodDescription method in service.Methods)
        {
            if (!names.Add(method.Name))
            {
                _problems.Add(new(ZabbixDiagnostics.MethodCollision, $"two methods of {service.Object} are both named {method.Name}; name one at schema/overlay.json#/methods/{Pointer(method.Api)}/name", service.Object, method.Api));
                continue;
            }
            EmitMethod(code, service, method);
        }
        return code.Append("}\n").ToString();
    }

    private bool Gated(ServiceDescription service, MethodDescription m)
        => (m.Since ?? service.Since) is string since && since != api.Oldest || (m.Removed ?? service.Removed) is not null;

    private void EmitMethod(StringBuilder code, ServiceDescription service, MethodDescription m)
    {
        if (m.Shape is "get-list" or "get-object")
        {
            EmitGet(code, service, m);
            return;
        }
        string? type;
        string name;
        string argument;
        string info;
        bool optional = false;
        string doc;
        string? batchElement = null;
        if (m.Parameters == "none")
        {
            (type, name, argument, info, doc) = (null, string.Empty, "global::Community.Zabbix.ZabbixIds.EmptyObject", Root("global::System.Text.Json.JsonElement"), string.Empty);
        }
        else if (m.Parameters is "list:id" or "list:string")
        {
            type = m.Parameters == "list:id" ? "global::System.Collections.Generic.IEnumerable<ulong>" : "global::System.Collections.Generic.IEnumerable<string>";
            (name, argument, info, doc) = ("ids", "ids", Root(type), "The ids of the objects <c>" + m.Api + "</c> acts on.");
        }
        else if (m.Parameters.StartsWith("object:", StringComparison.Ordinal) || m.Parameters.StartsWith("list:object:", StringComparison.Ordinal))
        {
            bool list = m.Parameters.StartsWith("list:", StringComparison.Ordinal);
            string record = list ? m.Parameters[12..] : m.Parameters[7..];
            type = ClrType("object:" + record, service.Object, m.Api);
            if (type is null) return;
            bool anyRequired = _types.TryGetValue(record, out TypeDescription? d) && d.Fields.Any(f => f.Required);
            optional = !list && !anyRequired;
            (name, argument, info, doc) = ("request", optional ? "request ?? new()" : "request", Root(type), "The fields <c>" + m.Api + "</c> takes" + (optional ? "; none when omitted." : "."));
            if (list && m.Batch) batchElement = type;
        }
        else if (m.Parameters == "json")
        {
            type = "global::System.Text.Json.JsonElement";
            (name, argument, info, doc) = ("parameters", "parameters", Root(type), "The parameters <c>" + m.Api + "</c> takes, as the API documents them.");
        }
        else
        {
            _problems.Add(new(ZabbixDiagnostics.UnknownKind, $"'{m.Parameters}' is not a parameter kind the generator knows", service.Object, m.Api));
            return;
        }
        (string ReturnType, string? ResultInfo, Func<string, string> Body, string Returns)? result = Result(service, m);
        if (result is null) return;
        bool deletesOne = m.Parameters == "list:id" && m.Name.StartsWith("Delete", StringComparison.Ordinal) && m.Result.StartsWith("ids:", StringComparison.Ordinal);
        bool overloaded = deletesOne || batchElement is not null;
        // one object of a method that takes several: one id back (Azure's general-client-standardize-verbs)
        EmitOne(code, service, m, type, name, argument, info, optional, doc, batchElement is not null && OneId(m) is { } one ? one : result.Value, many: false, overloaded);
        if (deletesOne)
        {
            const string Ids = "global::System.Collections.Generic.IEnumerable<ulong>";
            EmitOne(code, service, m, "ulong", "id", "new ulong[] { id }", Root(Ids), false, "The id of the object <c>" + m.Api + "</c> acts on.",
                ("global::System.Threading.Tasks.Task", null, send => "_ = global::Community.Zabbix.ZabbixIds.ReadOne(await " + send + ".ConfigureAwait(false), " + Lit(m.Result[4..]) + ");", "A task that completes when the server has answered with the object's id."), many: false, overloaded);
        }
        if (batchElement is not null && m.Name == "CreateAsync" && OneId(m) is not null) EmitShortCreate(code, m, m.Parameters[12..], batchElement);
        if (batchElement is not null)
        {
            string listType = "global::System.Collections.Generic.List<" + batchElement + ">";
            EmitOne(code, service, m, "global::System.Collections.Generic.IEnumerable<" + batchElement + ">", "requests", "new " + listType + "(requests)", Root(listType), false, "The objects to send in one <c>" + m.Api + "</c> call, each with the fields it takes.", result.Value, many: true, overloaded);
        }
    }

    // A create from the fields it can't go without, beside the request that has them all: CreateAsync("web-02", [groupId]),
    // as Azure's guidelines ask for the simple case. Too many required fields read worse than the request, a list goes
    // last so a name comes first, and a secret never goes in an argument a log could print.
    private void EmitShortCreate(StringBuilder code, MethodDescription m, string record, string request)
    {
        if (!_types.TryGetValue(record, out TypeDescription? t)) return;
        FieldDescription[] required = [.. t.Fields.Where(f => f.Required).OrderBy(f => f.Kind.StartsWith("list:", StringComparison.Ordinal))];
        if (required.Length is 0 or > 5 || required.Any(f => f.Secret || f.Kind is "bool" or "apibool")) return;
        if (required is [{ Kind: var only }] && only.StartsWith("list:", StringComparison.Ordinal)) return;
        var parameters = new List<string>();
        var assignments = new List<string>();
        code.Append("\n    /// <summary><c>").Append(m.Api).Append("</c> from the fields it requires").Append(Traits(m)).Append(" To set more, pass a <see cref=\"").Append(request).Append("\"/>.</summary>\n");
        foreach (FieldDescription field in required)
        {
            string? type = ClrType(field.Kind, t.Name, field.Wire);
            if (type is null) return;
            string member = Member(field.Name);
            string local = Local(member);
            code.Append("    /// <param name=\"").Append(local.TrimStart('@')).Append("\">").Append(Doc(DocSentences.Field(field.Summary, member, field.Wire), field.Wire)).Append("</param>\n");
            parameters.Add(type + " " + local);
            assignments.Add(member + " = " + local);
        }
        code.Append("    /// <param name=\"cancellationToken\">Cancels the call.</param>\n");
        code.Append("    /// <returns>The id the server answered in <c>").Append(m.Result[4..]).Append("</c>: the object the call created.</returns>\n");
        code.Append(Versioning(m.Since, m.Removed, m.Replacement, "    "));
        code.Append("    ").Append(Suppress("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
            "Each overload of the verb takes one shape of the request and ends in the optional CancellationToken the task-based asynchronous pattern asks for; the shapes differ in their required parameters, so no call is ambiguous.")).Append('\n');
        if (required.Any(f => f.Kind == "string" && Member(f.Name).Contains("Url")))
            code.Append("    ").Append(Suppress("Design", "CA1054:URI-like parameters should not be strings", "The API takes this field as text: it may hold a macro, which isn't a URI.")).Append('\n');
        code.Append("    public virtual global::System.Threading.Tasks.Task<ulong> CreateAsync(").Append(string.Join(", ", parameters)).Append(", global::System.Threading.CancellationToken cancellationToken = default)\n");
        code.Append("        => CreateAsync(new ").Append(request).Append(" { ").Append(string.Join(", ", assignments)).Append(" }, cancellationToken);\n");
    }

    // The single-object form of a write that answers ids: the one id, checked to be one.
    private static (string ReturnType, string? ResultInfo, Func<string, string> Body, string Returns)? OneId(MethodDescription m)
    {
        if (!m.Result.StartsWith("ids:", StringComparison.Ordinal)) return null;
        string field = m.Result[4..];
        return ("global::System.Threading.Tasks.Task<ulong>", null, send => "return global::Community.Zabbix.ZabbixIds.ReadOne(await " + send + ".ConfigureAwait(false), " + Lit(field) + ");",
            "The id the server answered in <c>" + field + "</c>: the object the call created or changed.");
    }

    private (string ReturnType, string? ResultInfo, Func<string, string> Body, string Returns)? Result(ServiceDescription service, MethodDescription m)
    {
        const string Task = "global::System.Threading.Tasks.Task";
        string r = m.Result;
        if (r.StartsWith("ids:", StringComparison.Ordinal))
        {
            string field = r[4..];
            return (Task + "<global::System.Collections.Generic.IReadOnlyList<ulong>>", null, send => "return global::Community.Zabbix.ZabbixIds.Read(await " + send + ".ConfigureAwait(false), " + Lit(field) + ");",
                "The ids the server answered in <c>" + field + "</c>: the objects the call created or changed, in the order it took them.");
        }
        if (r == "json")
            return (Task + "<global::System.Text.Json.JsonElement>", null, send => "return await " + send + ".ConfigureAwait(false);", "The result, as the API answered it.");
        if (r == "string")
            return (Task + "<string>", null, send => "return global::Community.Zabbix.ZabbixApiResults.ReadText(await " + send + ".ConfigureAwait(false), " + Lit(m.Api) + ");", "The text the API answered.");
        string? clr = ClrType(r, service.Object, m.Api);
        if (clr is null) return null;
        return (Task + "<" + clr + ">", Root(clr), send => "return await " + send + ".ConfigureAwait(false);", "The result the API answered.");
    }

    private void EmitOne(StringBuilder code, ServiceDescription service, MethodDescription m, string? parameterType, string parameterName, string argument,
        string info, bool optional, string doc, (string ReturnType, string? ResultInfo, Func<string, string> Body, string Returns) result, bool many, bool overloaded)
    {
        string send = "Transport.SendAsync(" + Lit(m.Api) + ", " + argument + ", " + info + (result.ResultInfo is null ? string.Empty : ", " + result.ResultInfo) + ", cancellationToken)";
        bool gated = Gated(service, m);
        code.Append("\n    /// <summary><c>").Append(m.Api).Append("</c>").Append(many ? " for several at once" : string.Empty).Append(Traits(m)).Append("</summary>\n");
        if (parameterType is not null) code.Append("    /// <param name=\"").Append(parameterName).Append("\">").Append(doc).Append("</param>\n");
        code.Append("    /// <param name=\"cancellationToken\">Cancels the call.</param>\n");
        code.Append("    /// <returns>").Append(result.Returns).Append("</returns>\n");
        ImmutableArray<FieldDescription> gatedFields = parameterType is not null && (parameterName == "request" || parameterName == "requests") ? RefusedFields(m) : [];
        code.Append(Throws(m.Api, gated && m.Emulation is null, gatedFields));
        code.Append(Remarks(m, gated && m.Emulation is not null));
        code.Append(Versioning(m.Since, m.Removed, m.Replacement, "    "));
        // One, several and the one-id delete overload one verb; each ends in the optional CancellationToken the
        // async-method guidelines ask for, so RS0026 is answered here on the overloads it names, not project-wide.
        if (overloaded)
            code.Append("    ").Append(Suppress("ApiDesign", "RS0026:Do not add multiple public overloads with optional parameters",
                "Each overload of the verb takes one shape of the request and ends in the optional CancellationToken the task-based asynchronous pattern asks for; the shapes differ in their required parameter's type, so no call is ambiguous."));
        code.Append("    public virtual async ").Append(result.ReturnType).Append(' ').Append(CodeText.Identifier(m.Name)).Append('(');
        if (parameterType is not null)
            code.Append(parameterType).Append(optional ? "? " : " ").Append(parameterName).Append(optional ? " = null" : string.Empty).Append(", ");
        code.Append("global::System.Threading.CancellationToken cancellationToken = default)\n    {\n");
        if (parameterType is not null && !optional && !parameterType.EndsWith("JsonElement", StringComparison.Ordinal) && parameterType != "ulong")
            code.Append("        global::System.ArgumentNullException.ThrowIfNull(").Append(parameterName).Append(");\n");
        ImmutableArray<FieldDescription> refused = gatedFields;
        if (!refused.IsEmpty && many)
        {
            // several requests: read once into the list that is sent, and check every one of them
            string element = parameterType![(parameterType!.IndexOf('<') + 1)..^1];
            code.Append("        global::System.Collections.Generic.List<").Append(element).Append("> all = ").Append(argument).Append(";\n");
            send = send.Replace(argument, "all");
            EmitFieldGate(code, m.Api, refused, "each", "all", element, nullable: false);
        }
        else if (!refused.IsEmpty)
        {
            EmitFieldGate(code, m.Api, refused, parameterName, null, null, nullable: optional);
        }
        if (gated)
        {
            code.Append("        global::System.Version server = await ApiVersion(cancellationToken).ConfigureAwait(false);\n");
            if (m.Emulation is not null && !many && parameterType is not null)
            {
                code.Append("        if (!global::Community.Zabbix.ZabbixApiMethods.Supports(").Append(Lit(m.Api)).Append(", server))\n");
                // The emulation's parameter is never optional (it always has a request to work from); an optional public
                // parameter defaults it here, the same default the direct call below uses.
                code.Append("            return await ").Append(m.Emulation).Append('(').Append(parameterName).Append(optional ? " ?? new()" : string.Empty).Append(", server, cancellationToken).ConfigureAwait(false);\n");
            }
            else
            {
                code.Append("        global::Community.Zabbix.ZabbixApiMethods.EnsureSupported(").Append(Lit(m.Api)).Append(", server);\n");
            }
        }
        code.Append("        ").Append(result.Body(send)).Append("\n    }\n");
        if (m.Emulation is not null && !many && parameterType is not null)
        {
            code.Append("\n    // What stands in for ").Append(m.Api).Append(" where the server no longer has it: hand-written beside the client (src/Zabbix.Sdk).\n");
            code.Append("    private partial ").Append(result.ReturnType).Append(' ').Append(m.Emulation).Append('(').Append(parameterType).Append(' ').Append(parameterName).Append(", global::System.Version server, global::System.Threading.CancellationToken cancellationToken);\n");
        }
    }

    private static string Traits(MethodDescription m)
    {
        var parts = new List<string> { m.IsRead ? "reads" : m.Idempotent ? "safe to repeat" : "writes" };
        if (!m.RequiresAuthentication) parts.Add("no session");
        return " (" + string.Join(", ", parts) + ").";
    }

    /// <summary>
    /// The exceptions a generated call documents: what every call through the transport can throw (the server's error
    /// answer, the HTTP request failing), and <c>ZabbixVersionException</c> where the client checks the server's version
    /// first, for the method itself or for a field the version refuses.
    /// </summary>
    private static string Throws(string api, bool methodGated, ImmutableArray<FieldDescription> gatedFields)
    {
        var code = new StringBuilder();
        code.Append("    /// <exception cref=\"global::Community.Zabbix.ZabbixApiException\">The server answered <c>").Append(api).Append("</c> with an error (<see cref=\"global::Community.Zabbix.ZabbixApiException.Kind\"/> classifies it).</exception>\n");
        code.Append("    /// <exception cref=\"global::System.Net.Http.HttpRequestException\">The request could not be sent, or the server answered with an HTTP failure status.</exception>\n");
        if (methodGated)
            code.Append("    /// <exception cref=\"global::Community.Zabbix.ZabbixVersionException\">The server's version does not have <c>").Append(api).Append("</c>.</exception>\n");
        else if (!gatedFields.IsEmpty)
            code.Append("    /// <exception cref=\"global::Community.Zabbix.ZabbixVersionException\">A field is set that the server's version refuses (")
                .Append(string.Join(", ", gatedFields.Select(f => "<c>" + f.Wire + "</c>"))).Append(").</exception>\n");
        return code.ToString();
    }

    /// <summary>
    /// The remarks of a generated call: the emulation where the server no longer has the method, and the Zabbix frontend
    /// class that implements it, named without a line number (a line moves with every Zabbix release).
    /// </summary>
    private static string Remarks(MethodDescription m, bool emulated, bool paged = false)
    {
        string? implementation = m.Source is { } source ? source.Split(':')[0] : null;
        implementation = implementation?[(implementation.LastIndexOf('/') + 1)..];
        var sentences = new List<string>();
        if (emulated) sentences.Add("Where the server no longer has <c>" + m.Api + "</c>, the same change is made with the methods it has.");
        if (paged) sentences.Add(OnePage);
        if (implementation is not null) sentences.Add("The Zabbix frontend implements <c>" + m.Api + "</c> in <c>" + implementation + "</c>.");
        return sentences.Count == 0 ? string.Empty : "    /// <remarks>" + string.Join(" ", sentences) + "</remarks>\n";
    }

    /// <summary>
    /// Why a list read is one reply and not a pageable sequence: the <c>.get</c> grammar has <c>limit</c> but no offset
    /// and no comparison operator, so no cursor can be built over it (decisions.md, "A list read is one reply").
    /// </summary>
    internal const string OnePage = "The reply is read whole, bounded by <see cref=\"global::Community.Zabbix.ZabbixOptions.MaxResponseContentBytes\"/>; "
        + "there is no paged form, because the API's <c>.get</c> has a <c>limit</c> but no offset or cursor to continue from. "
        + "Narrow a large read with the parameters.";

    private void EmitGet(StringBuilder code, ServiceDescription service, MethodDescription m)
    {
        string record = m.Parameters.StartsWith("object:", StringComparison.Ordinal) ? m.Parameters[7..] : m.Parameters;
        string? parameters = ClrType("object:" + record, service.Object, m.Api);
        string? result = ClrType(m.Result, service.Object, m.Api);
        if (parameters is null || result is null) return;
        string gate = Gated(service, m) ? "        global::Community.Zabbix.ZabbixApiMethods.EnsureSupported(" + Lit(m.Api) + ", await ApiVersion(cancellationToken).ConfigureAwait(false));\n" : string.Empty;
        // a .get with a required option (hostdashboard.get's hostids) has no "everything" call: the parameters are required
        bool required = _types.TryGetValue(record, out TypeDescription? described) && described.Fields.Any(f => f.Required);
        string declared = required ? parameters + " parameters" : parameters + "? parameters = null";
        string sent = required ? "parameters" : "parameters ?? new()";
        string guard = required ? "        global::System.ArgumentNullException.ThrowIfNull(parameters);\n" : string.Empty;
        code.Append("\n    /// <summary><c>").Append(m.Api).Append("</c>: ").Append(m.Shape == "get-object" ? "the object" : "the objects the parameters select, as one reply").Append(Traits(m)).Append("</summary>\n");
        code.Append("    /// <param name=\"parameters\">What to select and what to return").Append(required ? "." : "; everything when omitted.").Append("</param>\n    /// <param name=\"cancellationToken\">Cancels the call.</param>\n");
        code.Append("    /// <returns>").Append(m.Shape == "get-object" ? "The object, as the server answered it." : "The objects the parameters select, in the order the server answered them; empty when none matches.").Append("</returns>\n");
        code.Append(Throws(m.Api, gate.Length > 0, RefusedFields(m)));
        code.Append(Remarks(m, emulated: false, paged: m.Shape == "get-list"));
        code.Append(Versioning(m.Since, m.Removed, m.Replacement, "    "));
        bool lambda = !required && _types.ContainsKey(record);
        if (lambda) code.Append("    ").Append(OverloadsOfOneVerb).Append('\n');
        code.Append("    public virtual async global::System.Threading.Tasks.Task<").Append(result).Append("> GetAsync(").Append(declared).Append(", global::System.Threading.CancellationToken cancellationToken = default)\n    {\n");
        code.Append(guard).Append(gate);
        EmitFieldGate(code, m.Api, RefusedFields(m), "parameters", null, null, nullable: !required);
        code.Append("        return await Transport.SendAsync(").Append(Lit(m.Api)).Append(", ").Append(sent).Append(", ").Append(Root(parameters)).Append(", ").Append(Root(result)).Append(", cancellationToken).ConfigureAwait(false);\n    }\n");
        if (lambda) EmitConfigured(code, m, parameters, "global::System.Threading.Tasks.Task<" + result + ">", "GetAsync",
            m.Shape == "get-object" ? "The object, as the server answered it." : "The objects the parameters select, in the order the server answered them; empty when none matches.");
        if (m.Shape != "get-list" || !_types.ContainsKey(record)) return;
        if (!required) EmitGetById(code, service, m, record, parameters, result);
        if (m.Count)
        {
            code.Append("\n    /// <summary><c>").Append(m.Api).Append("</c> with <c>countOutput</c>: how many objects the parameters select.</summary>\n");
            code.Append("    /// <param name=\"parameters\">What to select").Append(required ? "." : "; everything when omitted.").Append("</param>\n    /// <param name=\"cancellationToken\">Cancels the call.</param>\n");
            code.Append("    /// <returns>How many objects the parameters select.</returns>\n");
            code.Append(Throws(m.Api, gate.Length > 0, RefusedFields(m)));
            code.Append(Remarks(m, emulated: false));
            if (lambda) code.Append("    ").Append(OverloadsOfOneVerb).Append('\n');
            code.Append("    public virtual async global::System.Threading.Tasks.Task<long> CountAsync(").Append(declared).Append(", global::System.Threading.CancellationToken cancellationToken = default)\n    {\n");
            code.Append(guard).Append(gate);
            EmitFieldGate(code, m.Api, RefusedFields(m), "parameters", null, null, nullable: !required);
            code.Append("        return global::Community.Zabbix.ZabbixApiResults.ReadCount(await Transport.SendAsync(").Append(Lit(m.Api)).Append(", ").Append(required ? "parameters" : "(parameters ?? new())").Append(" with { CountOutput = true }, ").Append(Root(parameters)).Append(", cancellationToken).ConfigureAwait(false), ").Append(Lit(m.Api)).Append(");\n    }\n");
            if (lambda) EmitConfigured(code, m, parameters, "global::System.Threading.Tasks.Task<long>", "CountAsync", "How many objects the parameters select.");
        }
    }

    private const string OverloadsOfOneVerb = "[global::System.Diagnostics.CodeAnalysis.SuppressMessage(\"ApiDesign\", \"RS0026:Do not add multiple public overloads with optional parameters\", Justification = \"One takes the parameters and the other a lambda that fills them, and each ends in the optional CancellationToken the task-based asynchronous pattern asks for.\")]";

    // A get whose parameters a lambda fills: GetAsync(q => { q.Search.VisibleName = "web"; q.Limit = 10; }), as Graph's
    // GetAsync(config => config.QueryParameters.Select = [...]).
    private static void EmitConfigured(StringBuilder code, MethodDescription m, string parameters, string returnType, string verb, string returns)
    {
        code.Append("\n    /// <summary><c>").Append(m.Api).Append("</c>, with parameters <paramref name=\"configure\"/> fills").Append(Traits(m)).Append("</summary>\n");
        code.Append("    /// <param name=\"configure\">Fills the parameters: what to select and what to return.</param>\n    /// <param name=\"cancellationToken\">Cancels the call.</param>\n");
        code.Append("    /// <returns>").Append(returns).Append("</returns>\n");
        code.Append("    /// <exception cref=\"global::System.ArgumentNullException\"><paramref name=\"configure\"/> is <see langword=\"null\"/>.</exception>\n");
        code.Append(Versioning(m.Since, m.Removed, m.Replacement, "    "));
        code.Append("    ").Append(OverloadsOfOneVerb).Append('\n');
        code.Append("    public virtual ").Append(returnType).Append(' ').Append(verb).Append("(global::System.Action<").Append(parameters).Append("> configure, global::System.Threading.CancellationToken cancellationToken = default)\n    {\n");
        code.Append("        global::System.ArgumentNullException.ThrowIfNull(configure);\n");
        code.Append("        var parameters = new ").Append(parameters).Append("();\n        configure(parameters);\n");
        code.Append("        return ").Append(verb).Append("(parameters, cancellationToken);\n    }\n");
    }

    // One object by its id, through the list get (a type that overrides that for a test serves this too): the read
    // record's own id, selected by the get's own *ids option (templateids, not filter.templateid, which template.get
    // ignores). Named as DbSet<T>.FindAsync is, for the same answer (the object, or null): an overload of GetAsync would
    // make GetAsync(new() { ... }) ambiguous, the target-typed parameters being the common call.
    private void EmitGetById(StringBuilder code, ServiceDescription service, MethodDescription m, string record, string parameters, string result)
    {
        if (!m.Result.StartsWith("list:object:", StringComparison.Ordinal) || !_types.TryGetValue(m.Result[12..], out TypeDescription? read)) return;
        if (ZabbixCmdletEmitter.EntityIdField(service, read) is not { } id || !_types.TryGetValue(record, out TypeDescription? get)) return;
        if (get.Fields.FirstOrDefault(f => f.Kind == "list:id" && f.Wire == id.Wire + "s") is not { } ids) return;
        string element = result[(result.IndexOf('<') + 1)..^1];
        code.Append("\n    /// <summary><c>").Append(m.Api).Append("</c> for the one object whose <c>").Append(id.Wire).Append("</c> is <paramref name=\"id\"/>").Append(Traits(m)).Append("</summary>\n");
        code.Append("    /// <param name=\"id\">The object's id.</param>\n    /// <param name=\"cancellationToken\">Cancels the call.</param>\n");
        code.Append("    /// <returns>The object, or <see langword=\"null\"/> when the server has none with that id that the caller may read.</returns>\n");
        code.Append(Throws(m.Api, Gated(service, m), RefusedFields(m)));
        code.Append(Remarks(m, emulated: false));
        code.Append(Versioning(m.Since, m.Removed, m.Replacement, "    "));
        code.Append("    public virtual async global::System.Threading.Tasks.Task<").Append(element).Append("?> FindAsync(ulong id, global::System.Threading.CancellationToken cancellationToken = default)\n    {\n");
        code.Append("        ").Append(result).Append(" found = await GetAsync(new ").Append(parameters).Append(" { ").Append(Member(ids.Name)).Append(" = [id] }, cancellationToken).ConfigureAwait(false);\n");
        code.Append("        return found.Count > 0 ? found[0] : null;\n    }\n");
        bool deletes = service.Methods.Any(d => d.Name == "DeleteAsync" && d.Parameters == "list:id" && d.Result.StartsWith("ids:", StringComparison.Ordinal));
        string node = "global::Community.Zabbix." + (deletes ? "ZabbixDeletableObjectNode<" : "ZabbixObjectNode<") + element + ">";
        code.Append("\n    /// <summary>One <c>").Append(service.Object).Append("</c>, by its id; no call is made until it's asked.</summary>\n");
        code.Append("    /// <param name=\"id\">The object's id.</param>\n    /// <returns>The object's node.</returns>\n");
        code.Append("    public virtual ").Append(node).Append(" this[ulong id] => new(id, ").Append(Lit(service.Object)).Append(", FindAsync").Append(deletes ? ", DeleteAsync" : string.Empty).Append(");\n");
    }

    // ----- aggregate and context ----------------------------------------------------------------------------

    private string EmitAggregate(EquatableArray<ServiceDescription> clients)
    {
        var code = new StringBuilder(Header(api.Range, "Provides the whole Zabbix " + api.Range + " API, one client per object. Each property speaks for one API object; every call goes through the one transport, and the methods not in every version ask the server's version first."));
        code.Append("internal partial class ZabbixApi\n{\n    /// <summary>Over a transport.</summary>\n    /// <param name=\"transport\">The JSON-RPC transport.</param>\n");
        code.Append("    /// <param name=\"apiVersion\">The server's API version (<c>apiinfo.version</c>), read once by the caller and shared by every client.</param>\n");
        code.Append("    internal ZabbixApi(global::Community.Zabbix.IZabbixTransport transport, global::System.Func<global::System.Threading.CancellationToken, global::System.Threading.Tasks.Task<global::System.Version>> apiVersion)\n    {\n");
        code.Append("        global::System.ArgumentNullException.ThrowIfNull(transport);\n        global::System.ArgumentNullException.ThrowIfNull(apiVersion);\n        Transport = transport;\n");
        foreach (ServiceDescription s in clients) code.Append("        ").Append(CodeText.Identifier(s.Property)).Append(" = new global::Community.Zabbix.").Append(s.Client).Append("(transport, apiVersion);\n");
        code.Append("    }\n\n");
        code.Append("    /// <summary>For a type that derives from this one to substitute the API in a test: it overrides the clients the code\n");
        code.Append("    /// under test reads. An API built this way has no transport and no clients of its own.</summary>\n");
        code.Append("    protected ZabbixApi()\n    {\n        Transport = null!;\n");
        foreach (ServiceDescription s in clients) code.Append("        ").Append(CodeText.Identifier(s.Property)).Append(" = null!;\n");
        code.Append("    }\n\n    /// <summary>The transport every client shares.</summary>\n    internal global::Community.Zabbix.IZabbixTransport Transport { get; }\n");
        foreach (ServiceDescription s in clients)
        {
            code.Append("\n    /// <summary>The <see cref=\"global::Community.Zabbix.").Append(s.Client).Append("\"/>.</summary>\n");
            code.Append(Versioning(s.Since, s.Removed, null, "    "));
            code.Append("    public virtual global::Community.Zabbix.").Append(s.Client).Append(' ').Append(CodeText.Identifier(s.Property)).Append(" { get; }\n");
        }
        return code.Append("}\n").ToString();
    }

    // The root's clients: zabbix.Hosts, as Graph's client.Me. Each goes through the internal aggregate, and a test double
    // overrides the property it needs.
    private string EmitClientProperties(EquatableArray<ServiceDescription> clients)
    {
        var code = new StringBuilder("// Emitted by Zabbix.Generator from the Zabbix " + api.Range + " API description (schema/zabbix-api.json). Do not edit.\n#nullable enable\nnamespace Community.Zabbix;\n\n");
        code.Append("public partial class ZabbixClient\n{\n");
        foreach (ServiceDescription s in clients)
        {
            code.Append("    /// <summary>The <c>").Append(s.Object).Append("</c> API").Append(s.Summary is not null ? ": " + Doc(s.Summary, s.Object) : ".").Append("</summary>\n");
            code.Append(Versioning(s.Since, s.Removed, null, "    "));
            code.Append("    public virtual global::Community.Zabbix.").Append(s.Client).Append(' ').Append(CodeText.Identifier(s.Property)).Append(" => Api.").Append(CodeText.Identifier(s.Property)).Append(";\n\n");
        }
        while (code.Length > 1 && code[^1] == '\n' && code[^2] == '\n') code.Length--;
        return code.Append("}\n").ToString();
    }

    private string EmitContext()
    {
        foreach (TypeDescription t in api.Types) Root("global::Community.Zabbix." + Prefixed(t.Name));
        Root("string");
        Root("bool");
        Root("global::System.Text.Json.JsonElement");
        Root("global::System.Text.Json.Nodes.JsonNode");
        Root("global::System.Collections.Generic.IReadOnlyList<string>");
        Root("global::System.Collections.Generic.IEnumerable<string>");
        Root("global::System.Collections.Generic.IReadOnlyList<ulong>");
        Root("global::System.Collections.Generic.IEnumerable<ulong>");
        var code = new StringBuilder(Header(api.Range, "The JSON serializer context for the API: every record, request and parameter set, source-generated."));
        code.Append("[global::System.Text.Json.Serialization.JsonSourceGenerationOptions(\n    DefaultIgnoreCondition = global::System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,\n    NumberHandling = global::System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,\n    Converters = new[] { typeof(global::Community.Zabbix.ZabbixIdConverter), typeof(global::Community.Zabbix.ZabbixFiniteDoubleConverter), typeof(global::Community.Zabbix.ZabbixSortDirectionConverter) },\n    GenerationMode = global::System.Text.Json.Serialization.JsonSourceGenerationMode.Default)]\n");
        foreach (KeyValuePair<string, string> root in _jsonRoots.OrderBy(r => r.Value, StringComparer.Ordinal))
            code.Append("[global::System.Text.Json.Serialization.JsonSerializable(typeof(").Append(root.Key).Append("), TypeInfoPropertyName = ").Append(Lit(root.Value)).Append(")]\n");
        return code.Append("internal sealed partial class ZabbixApiJson : global::System.Text.Json.Serialization.JsonSerializerContext\n{\n}\n").ToString();
    }
}
