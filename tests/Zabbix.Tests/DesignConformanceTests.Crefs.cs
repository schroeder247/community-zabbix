// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Xml.Linq;
using Community.Zabbix.Tests.Conformance;

namespace Community.Zabbix.Tests;

/// <summary>
/// The public reference documentation links only to what its reader can see. A <c>cref</c> in the XML documentation of a
/// public member that names an internal type or member compiles (the compiler resolves it), but the reference site has no
/// page for it, so docfx renders it as unlinked text without a warning. The rule reads the XML documentation the build
/// writes and resolves every <c>cref</c> of a public member against the libraries: one that lands on a non-public symbol
/// of a library is an offender. An internal name a public summary needs is written as <c>&lt;c&gt;</c> text.
/// </summary>
public sealed partial class DesignConformanceTests
{
    [Fact]
    public void No_public_documentation_links_to_a_non_public_symbol()
        => AssertNone(NonPublicCrefs(LibraryTypes(Libraries.All),
            Libraries.All.SelectMany(library => XDocument.Load(Path.ChangeExtension(library.Location, ".xml")).Descendants("member"))));

    [Fact]
    public void The_cref_rule_finds_a_public_member_that_links_to_an_internal_type_or_member()
    {
        Dictionary<string, Type> types = LibraryTypes([typeof(CrefProbe).Assembly]);
        string probe = typeof(CrefProbe).FullName!.Replace('+', '.');
        string hidden = typeof(HiddenCrefProbe).FullName!.Replace('+', '.');
        XElement[] members =
        [
            new("member", new XAttribute("name", "T:" + probe), new XElement("summary", new XElement("see", new XAttribute("cref", "T:" + hidden)))),
            new("member", new XAttribute("name", "P:" + probe + ".Shown"), new XElement("summary", new XElement("see", new XAttribute("cref", "F:" + probe + ".Hidden")))),
            new("member", new XAttribute("name", "M:" + probe + ".#ctor"), new XElement("summary", new XElement("see", new XAttribute("cref", "P:" + probe + ".Shown")), new XElement("see", new XAttribute("cref", "T:System.String")))),
            new("member", new XAttribute("name", "T:" + hidden), new XElement("summary", new XElement("see", new XAttribute("cref", "F:" + probe + ".Hidden")))),
        ];

        Assert.Equal([$"T:{probe}: T:{hidden}", $"P:{probe}.Shown: F:{probe}.Hidden"], NonPublicCrefs(types, members));
    }

    internal static Dictionary<string, Type> LibraryTypes(IEnumerable<Assembly> libraries)
        => libraries.SelectMany(library => library.GetTypes())
            .Where(type => type.FullName is not null && !type.FullName.Contains('<', StringComparison.Ordinal))
            .ToDictionary(type => type.FullName!.Replace('+', '.'), StringComparer.Ordinal);

    /// <summary>Each <c>cref</c> of a visible member that resolves to a non-public symbol of <paramref name="types"/>: <c>owner: cref</c>.</summary>
    internal static List<string> NonPublicCrefs(IReadOnlyDictionary<string, Type> types, IEnumerable<XElement> members)
        => [.. members
            .Select(member => (Id: member.Attribute("name")?.Value, Member: member))
            .Where(entry => entry.Id is not null && Visible(types, entry.Id) == true)
            .SelectMany(entry => entry.Member.Descendants().Attributes("cref")
                .Where(cref => Visible(types, cref.Value) == false)
                .Select(cref => $"{entry.Id}: {cref.Value}"))
            .Distinct()];

    /// <summary>
    /// Whether the symbol a documentation id names is visible outside its assembly; <see langword="null"/> when it is not
    /// a symbol of <paramref name="types"/> (the platform's, a namespace) and so not this rule's to judge.
    /// </summary>
    private static bool? Visible(IReadOnlyDictionary<string, Type> types, string id)
    {
        if (id.Length < 3 || id[1] != ':') return null;
        string name = id[2..];
        int parameters = name.IndexOf('(', StringComparison.Ordinal);
        if (parameters >= 0) name = name[..parameters];
        if (id[0] == 'T') return types.TryGetValue(name, out Type? type) ? type.IsVisible : null;
        if (id[0] is not ('M' or 'P' or 'F' or 'E')) return null;
        int dot = name.LastIndexOf('.');
        if (dot < 0 || !types.TryGetValue(name[..dot], out Type? owner)) return null;
        string member = name[(dot + 1)..];
        int arity = member.IndexOf("``", StringComparison.Ordinal);
        if (arity >= 0) member = member[..arity];
        if (!owner.IsVisible) return false;
        if (member.Contains('#', StringComparison.Ordinal) && member != "#ctor") return true;   // an explicit interface implementation
        const BindingFlags all = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        MemberInfo[] found = member == "#ctor" ? owner.GetConstructors(all) : owner.GetMember(member, all);
        return found.Length == 0 ? null : found.Any(Exposed);
    }

    private static bool Exposed(MemberInfo member) => member switch
    {
        MethodBase method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly,
        FieldInfo field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly,
        PropertyInfo property => property.GetAccessors(nonPublic: true).Any(Exposed),
        EventInfo @event => @event.AddMethod is { } add && Exposed(add),
        Type nested => nested.IsVisible,
        _ => false,
    };
}

/// <summary>A public type whose documentation the cref rule's probe links from.</summary>
public sealed class CrefProbe
{
    internal const int Hidden = 1;

    /// <summary>Gets a public value.</summary>
    public int Shown => Hidden;
}

/// <summary>An internal type the cref rule's probe links to.</summary>
internal sealed class HiddenCrefProbe
{
}
