// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;

namespace Community.Zabbix.Generator;

/// <summary>
/// Checks the description once, before any emitter reads it, so no emitter throws on it: every name an emitter turns
/// into a C# identifier or a source file is one (<see cref="SyntaxFacts.IsValidIdentifier"/>) and is declared once
/// (enums and records share one namespace and one file per name; API objects, clients and methods are unique in their
/// scope); every version is <c>major.minor</c>. A repeated entry is left out after the first, a malformed value is
/// dropped from its entry, and each is a <see cref="ZabbixDiagnostics.Malformed"/> problem at the entry.
/// </summary>
internal static class DescriptionValidator
{
    private static readonly Regex s_version = new(@"^\d{1,4}\.\d{1,4}$", RegexOptions.CultureInvariant);

    // The files the emitters write beside one per enum, record and client.
    private static readonly string[] s_fixedFiles = ["ZabbixApiMethods", "ZabbixApiSuppressions", "ZabbixApi", "ZabbixApiJson", "ZabbixEntities", "ZabbixNestedBuilders"];

    /// <summary>The description with every repeated or malformed entry left out, each reported.</summary>
    /// <param name="api">The description as read.</param>
    /// <param name="problems">Receives a problem per entry or value left out.</param>
    public static ApiDescription Validate(ApiDescription api, List<EmitProblem> problems)
    {
        EquatableArray<string> versions = [.. api.Versions.Where(v => Version(v, "zabbix-api.json", "versions", problems) is not null)];
        var files = new HashSet<string>(s_fixedFiles, StringComparer.Ordinal);

        var enums = new List<EnumDescription>();
        foreach (EnumDescription e in api.Enums)
        {
            if (!Named(e.Name, "enum", files, problems)) continue;
            var members = new HashSet<string>(StringComparer.Ordinal);
            enums.Add(e with
            {
                Members = [.. e.Members
                    .Where(m => Unique(m.Name, members, e.Name, "enum member", problems) && Identifier(m.Name, e.Name, m.Name, problems) && Bits(e, m, problems))
                    .Select(m => m with { Since = Version(m.Since, e.Name, m.Name, problems), Removed = Version(m.Removed, e.Name, m.Name, problems) })],
            });
        }

        var types = new List<TypeDescription>();
        foreach (TypeDescription t in api.Types)
        {
            if (!Named(t.Name, "record", files, problems)) continue;
            var wires = new HashSet<string>(StringComparer.Ordinal);
            types.Add(t with
            {
                Fields = [.. t.Fields
                    .Where(f => Unique(f.Wire, wires, t.Name, "field", problems) && Identifier(f.Name, t.Name, f.Wire, problems) && Closed(f, t.Name, problems))
                    .Select(f => f with
                    {
                        Since = Version(f.Since, t.Name, f.Wire, problems),
                        Removed = Version(f.Removed, t.Name, f.Wire, problems),
                        Deprecated = Version(f.Deprecated, t.Name, f.Wire, problems),
                    })],
            });
        }

        var services = new List<ServiceDescription>();
        var objects = new HashSet<string>(StringComparer.Ordinal);
        var properties = new HashSet<string>(StringComparer.Ordinal);
        foreach (ServiceDescription s in api.Services)
        {
            if (!Unique(s.Object, objects, "services", "API object", problems) || !Named(s.Client, "client", files, problems, prefixed: false)
                || !Identifier(s.Property, s.Object, null, problems) || !Unique(s.Property, properties, s.Object, "client property", problems)) continue;
            var apis = new HashSet<string>(StringComparer.Ordinal);
            services.Add(s with
            {
                Since = Version(s.Since, s.Object, null, problems),
                Removed = Version(s.Removed, s.Object, null, problems),
                Methods = [.. s.Methods
                    .Where(m => Unique(m.Api, apis, s.Object, "method", problems) && Identifier(m.Name, s.Object, m.Api, problems))
                    .Select(m => m with
                    {
                        Since = Version(m.Since, s.Object, m.Api, problems),
                        Removed = Version(m.Removed, s.Object, m.Api, problems),
                        AcceptsAuthenticationSince = Version(m.AcceptsAuthenticationSince, s.Object, m.Api, problems),
                    })],
            });
        }
        return new ApiDescription(versions, [.. enums], [.. types], [.. services], api.References, Cmdlets(api.CmdletShape, problems));
    }

    // The cmdlets' shape: every name that becomes a class, a parameter, an attribute or an enum member reference is an
    // identifier, and a builder's concept (its command's class) is declared once.
    private static CmdletDescription Cmdlets(CmdletDescription cmdlets, List<EmitProblem> problems)
    {
        var concepts = new HashSet<string>(StringComparer.Ordinal);
        var parameters = new HashSet<string>(StringComparer.Ordinal);
        return new(
            [.. cmdlets.Objects.Select(o => o with
            {
                Switches = [.. o.Switches.Where(s => Identifier(s.On, o.Object, s.Wire, problems) && Identifier(s.Off, o.Object, s.Wire, problems))],
                ValueForms = Forms(o.ValueForms, o.Object, problems),
                Unhidden = [.. o.Unhidden.Where(u => Identifier(u.Property, o.Object, u.Wire, problems))],
            })],
            [.. cmdlets.Builders
                .Where(b => Unique(b.Concept, concepts, "cmdlets", "builder", problems) && Identifier(b.Concept, b.Concept, null, problems))
                .Select(b => b with { ValueForms = Forms(b.ValueForms, b.Concept, problems) })],
            [.. cmdlets.GetSwitches.Where(s => Unique(s.Parameter, parameters, "getSwitches", "switch", problems) && Identifier(s.Parameter, "getSwitches", s.Wire, problems))],
            // One parameter name for a switch and a list alike: both become properties of the same Get- class.
            [.. cmdlets.GetLists.Where(l => Unique(l.Parameter, parameters, "getLists", "list", problems) && Identifier(l.Parameter, "getLists", l.Wire, problems))]);
    }

    private static EquatableArray<ValueForm> Forms(EquatableArray<ValueForm> forms, string owner, List<EmitProblem> problems)
        => [.. forms.Where(f => f.Attributes.All(a => Identifier(a, owner, f.Wire, problems)))];

    // An int whose own summary names the defines that are its values (<c>ZBX_EVENT_NORMAL</c>, <c>ZBX_EVENT_COPIED</c>) is a
    // closed set of values the extractor missed: a closed set is an enum (decisions.md, "The Zabbix source is the model's
    // authority"), named in the overlay's fieldKinds or fieldEnums. The field is kept as it is; the build fails on it.
    private static bool Closed(FieldDescription field, string record, List<EmitProblem> problems)
    {
        if (field.Kind != "int" || field.Summary is null) return true;
        string[] defines = [.. s_define.Matches(field.Summary).Cast<Match>().Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];
        if (defines.Length < 2) return true;
        problems.Add(new(ZabbixDiagnostics.Malformed, "an int whose summary names the defines " + string.Join(", ", defines)
            + " is a closed set of values, which is an enum; name its family in the overlay's fieldKinds or fieldEnums", record, field.Wire));
        return true;
    }

    private static readonly Regex s_define = new(@"<c>([A-Z][A-Z0-9]*(?:_[A-Z0-9]+)+)</c>", RegexOptions.CultureInvariant);

    // A record's, enum's or client's name: an identifier, and the only one to take its file (Prefixed, as the emitters name it).
    private static bool Named(string? name, string what, HashSet<string> files, List<EmitProblem> problems, bool prefixed = true)
    {
        if (!Identifier(name, name ?? "(unnamed " + what + ")", null, problems)) return false;
        string file = prefixed && !name!.StartsWith("Zabbix", StringComparison.Ordinal) ? "Zabbix" + name : name!;
        if (files.Add(file)) return true;
        problems.Add(new(ZabbixDiagnostics.Malformed, "the " + what + " is declared twice (or takes the name another type or file already has); the later one is left out", name!, null));
        return false;
    }

    private static bool Unique(string? key, HashSet<string> seen, string record, string what, List<EmitProblem> problems)
    {
        if (string.IsNullOrEmpty(key))
        {
            problems.Add(new(ZabbixDiagnostics.Malformed, "a " + what + " has no name and is left out", record, null));
            return false;
        }
        if (seen.Add(key!)) return true;
        problems.Add(new(ZabbixDiagnostics.Malformed, "the " + what + " '" + key + "' is declared twice; the later one is left out", record, key));
        return false;
    }

    private static bool Identifier(string? name, string record, string? wire, List<EmitProblem> problems)
    {
        if (name is not null && SyntaxFacts.IsValidIdentifier(name)) return true;
        problems.Add(new(ZabbixDiagnostics.Malformed, "'" + name + "' is not a C# identifier; the entry is left out", record, wire));
        return false;
    }

    // A [Flags] member is no bit (0), one bit, or a union of the other members' bits (ZBX_FLAG_DISCOVERY's 3, 5, 6, 7):
    // anything else would read as a combination it is not.
    private static bool Bits(EnumDescription e, EnumMember member, List<EmitProblem> problems)
    {
        if (!e.Flags || member.Value == 0 || (member.Value > 0 && (member.Value & (member.Value - 1)) == 0)) return true;
        long union = 0;
        foreach (EnumMember other in e.Members)
        {
            if (other.Value > 0 && other.Value != member.Value && (other.Value & member.Value) == other.Value) union |= other.Value;
        }
        if (member.Value > 0 && union == member.Value) return true;
        problems.Add(new(ZabbixDiagnostics.Malformed, member.Value + " is neither a bit nor a union of the other members' bits of a [Flags] enum; the member is left out", e.Name, member.Name));
        return false;
    }

    private static string? Version(string? version, string record, string? wire, List<EmitProblem> problems)
    {
        if (version is null || s_version.IsMatch(version)) return version;
        problems.Add(new(ZabbixDiagnostics.Malformed, "'" + version + "' is not a major.minor version; it is ignored", record, wire));
        return null;
    }
}
