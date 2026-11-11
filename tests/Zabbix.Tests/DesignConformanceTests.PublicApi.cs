// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Community.Zabbix.Tests.Conformance;

namespace Community.Zabbix.Tests;

/// <summary>
/// The public-API baseline. A reflection diff of the built assembly against
/// <c>PublicAPI.Shipped.txt</c>/<c>PublicAPI.Unshipped.txt</c> would duplicate
/// <c>Microsoft.CodeAnalysis.PublicApiAnalyzers</c> itself (RS0016/RS0017 already fail the build on drift); what is
/// not already proven by the build is that every library is actually WIRED to the analyzer and the two files, and
/// that the shipped file follows the pre-release rule below. Both are project-file/restore-output
/// inspection, the same shape <c>DesignConformanceTests.Every_library_builds_with_the_banned_symbol_analyzer</c>
/// (<c>DesignConformanceTests.Enforcement.cs</c>) already uses for the banned-symbol analyzer.
/// </summary>
public sealed partial class DesignConformanceTests
{
    [Theory]
    [MemberData(nameof(LibraryNames))]
    public void Every_library_has_the_public_api_analyzer_and_its_two_files(string library)
    {
        using JsonDocument assets = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Repository.Root, "src", library, "obj", "project.assets.json")));

        Assert.Contains(assets.RootElement.GetProperty("libraries").EnumerateObject(), package => package.Name.StartsWith("Microsoft.CodeAnalysis.PublicApiAnalyzers/", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(Repository.Root, "src", library, "PublicAPI.Shipped.txt")), $"{library}/PublicAPI.Shipped.txt is missing");
        Assert.True(File.Exists(Path.Combine(Repository.Root, "src", library, "PublicAPI.Unshipped.txt")), $"{library}/PublicAPI.Unshipped.txt is missing");
    }

    /// <summary>
    /// The one project the analyzer condition would otherwise sweep in that is not a shipped library surface
    /// (Directory.Build.targets' <c>IsPublicApiTracked</c> opt-out): proves the opt-out is real, not just present in
    /// the csproj, the same way the analyzer-presence half of the rule above is proven from the restore output.
    /// </summary>
    [Fact]
    public void The_help_generator_tool_opts_out_of_public_api_tracking()
    {
        string path = Path.Combine(Repository.Root, "tools", "Zabbix.PowerShell.HelpGen", "obj", "project.assets.json");
        using JsonDocument assets = JsonDocument.Parse(File.ReadAllBytes(path));

        Assert.DoesNotContain(assets.RootElement.GetProperty("libraries").EnumerateObject(), package => package.Name.StartsWith("Microsoft.CodeAnalysis.PublicApiAnalyzers/", StringComparison.Ordinal));
    }

    /// <summary>
    /// SemVer: the surface is frozen at the first release tag, not
    /// before. While <c>Directory.Build.props</c> sets a <c>VersionSuffix</c> (a prerelease such as 1.1.0-beta.1),
    /// nothing is shipped: <c>PublicAPI.Shipped.txt</c> is empty in every library and the whole surface is declared in
    /// <c>PublicAPI.Unshipped.txt</c>. Without a suffix (a release), the shipped file holds every library's surface.
    /// Both properties are read from <c>Directory.Build.props</c>, never hand-duplicated here.
    /// </summary>
    [Fact]
    public void The_shipped_public_api_baseline_is_empty_in_a_pre_release_and_holds_the_surface_in_a_release()
    {
        string props = File.ReadAllText(Path.Combine(Repository.Root, "Directory.Build.props"));
        Match prefix = VersionPrefixPattern().Match(props);
        Assert.True(prefix.Success, "Directory.Build.props has no <VersionPrefix>");
        Version version = Version.Parse(prefix.Groups["version"].Value);
        Assert.True(version >= new Version(1, 0, 0), $"Directory.Build.props VersionPrefix is {version}: the first release is 1.0.0 and the version must not go backwards.");
        bool preRelease = VersionSuffixPattern().Match(props) is { Success: true } suffix && suffix.Groups["suffix"].Value.Trim().Length > 0;

        List<string> wrong = [.. Libraries.All.Select(Libraries.Folder)
            .Where(library => preRelease ? ShippedApiLines(library).Any() : !ShippedApiLines(library).Any())];

        AssertNone(wrong);
    }

    /// <summary>
    /// Everything the compiler currently sees as public for a library is declared SOMEWHERE (Shipped or Unshipped) —
    /// proven independently of RS0016 by counting lines rather than trusting the build, so a library whose two files
    /// were deleted or emptied by hand still fails a rule here even if nobody happens to touch that member's surface
    /// again. In a pre-release the unshipped file carries the whole surface; after a release the shipped file does and
    /// the unshipped file holds only what was added since, and may be empty.
    /// </summary>
    /// <summary>
    /// The API client has one raw door, <see cref="ZabbixClient.SendAsync(string, JsonElement, CancellationToken)"/>:
    /// no public or protected member of <c>Zabbix.Sdk</c> or <c>Zabbix.Api</c> hands out or takes the transport
    /// underneath it. A test substitutes a client through its protected constructor and virtual calls instead.
    /// </summary>
    [Fact]
    public void No_public_member_of_the_api_client_exposes_the_transport()
    {
        const BindingFlags Visible = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
        static bool Seen(MemberInfo member) => member switch
        {
            MethodBase method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly,
            PropertyInfo property => property.GetMethod is { } getter && Seen(getter),
            _ => false,
        };
        static IEnumerable<Type> Signature(MemberInfo member) => member switch
        {
            MethodInfo method => method.GetParameters().Select(parameter => parameter.ParameterType).Append(method.ReturnType),
            ConstructorInfo constructor => constructor.GetParameters().Select(parameter => parameter.ParameterType),
            PropertyInfo property => [property.PropertyType],
            _ => [],
        };

        List<string> offenders = [.. new[] { typeof(ZabbixClient).Assembly, typeof(ZabbixHost).Assembly }
            .SelectMany(library => library.GetExportedTypes())
            .SelectMany(type => type.GetMembers(Visible))
            .Where(member => member is not MethodInfo { IsSpecialName: true } && Seen(member) && Signature(member).Contains(typeof(IZabbixTransport)))
            .Select(member => $"{member.DeclaringType!.Name}.{member.Name}")
            .Distinct()];

        AssertNone(offenders);
    }

    [Theory]
    [InlineData(typeof(Zabbix.ZabbixComponentStatus), "Report")]
    [InlineData(typeof(Zabbix.ZabbixComponentStatus), "Attach")]
    [InlineData(typeof(Zabbix.Storage.ZabbixStore<int>), "DropReasonOf")]
    public void The_hosts_plumbing_is_not_part_of_the_public_or_protected_surface(Type type, string name)
        => Assert.DoesNotContain(
            type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly),
            method => method.Name == name && (method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly));

    [Fact]
    public void A_conversation_context_is_made_by_the_listener_only()
        => Assert.Empty(typeof(Zabbix.Listening.ZabbixConversationContext).GetConstructors());

    [Theory]
    [MemberData(nameof(LibraryNames))]
    public void Every_library_declares_its_public_api_in_the_shipped_or_unshipped_file(string library)
    {
        Assert.NotEmpty(ShippedApiLines(library).Concat(UnshippedApiLines(library)));
    }

    /// <summary>
    /// A <c>~</c> line is a member whose nullability the compiler cannot state, so a caller's nullable analysis goes
    /// blind on it. The only ones a library may carry are the language's own: a record struct's synthesized
    /// <c>Equals(object)</c> and <c>ToString()</c>, which cannot be declared by hand.
    /// </summary>
    [Fact]
    public void No_public_api_line_is_nullable_oblivious()
    {
        List<string> oblivious = [.. Libraries.All.Select(Libraries.Folder)
            .SelectMany(library => ShippedApiLines(library).Concat(UnshippedApiLines(library))
                .Where(line => line.StartsWith('~') && !IsRecordStructSynthesized(line))
                .Select(line => $"{library}: {line}"))];

        AssertNone(oblivious);
    }

    [Fact]
    public void The_oblivious_rule_spares_only_a_record_struct_s_synthesized_members()
    {
        Assert.True(IsRecordStructSynthesized("~override Community.Zabbix.ZabbixDuration.Equals(object obj) -> bool"));
        Assert.False(IsRecordStructSynthesized("~override Community.Zabbix.ZabbixHost.Equals(object obj) -> bool"));
        Assert.False(IsRecordStructSynthesized("~Community.Zabbix.ZabbixDuration.Length.get -> System.TimeSpan?"));
    }

    /// <summary>
    /// A source-generated <see cref="System.Text.Json.Serialization.JsonSerializerContext"/> has a public property per
    /// type it knows; made public, it puts the generator's members on the library's surface. The contract is offered as
    /// a resolver (<see cref="ZabbixJson"/>).
    /// </summary>
    [Fact]
    public void No_library_exposes_a_json_serializer_context()
    {
        List<string> exposed = [.. Libraries.PublicTypes
            .Where(type => typeof(System.Text.Json.Serialization.JsonSerializerContext).IsAssignableFrom(type))
            .Select(type => type.FullName!)];

        AssertNone(exposed);
    }

    private static bool IsRecordStructSynthesized(string line)
    {
        Match member = SynthesizedMemberPattern().Match(line);
        if (!member.Success) return false;
        string name = GenericArity().Replace(member.Groups["type"].Value, match => "`" + (match.Value.Count(c => c == ',') + 1));
        Type? type = Libraries.PublicTypes.FirstOrDefault(candidate => candidate.FullName == name);
        return type is { IsValueType: true } && type.GetMethod("PrintMembers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance) is not null;
    }

    [GeneratedRegex(@"^~override (?<type>[\w.<>, ]+)\.(?:Equals\(object obj\) -> bool|ToString\(\) -> string)$")]
    private static partial Regex SynthesizedMemberPattern();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex GenericArity();

    private static IEnumerable<string> ShippedApiLines(string library) => ApiLines(library, "PublicAPI.Shipped.txt");

    private static IEnumerable<string> UnshippedApiLines(string library) => ApiLines(library, "PublicAPI.Unshipped.txt");

    private static IEnumerable<string> ApiLines(string library, string file) => File.ReadLines(Path.Combine(Repository.Root, "src", library, file))
        .Select(line => line.Trim())
        .Where(line => line.Length > 0 && line != "#nullable enable");

    [GeneratedRegex(@"<VersionPrefix>(?<version>[0-9]+\.[0-9]+\.[0-9]+)</VersionPrefix>")]
    private static partial Regex VersionPrefixPattern();

    [GeneratedRegex(@"<VersionSuffix>(?<suffix>[^<]*)</VersionSuffix>")]
    private static partial Regex VersionSuffixPattern();
}
