// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
extern alias ZabbixGenerator;

using System.Reflection;
using Community.Zabbix.Tests.Conformance;
using ZabbixGenerator::Community.Zabbix.Generator;

namespace Community.Zabbix.Tests;

/// <summary>
/// Names and enums as ADR 0013 (section 7) and the Framework Design Guidelines state them: no name of the API packages
/// keeps a spelling <c>schema/words.json</c>'s <c>respell</c> section retires (an abbreviation, <c>Username</c>, a
/// two-letter acronym not in capitals), and no member keeps a wire name its type says more than; every simple enum has a
/// zero value and every flags enum names its zero <c>None</c>; a client looks up by id only records with an id of their
/// own; and a typed item key's parameter is <c>key</c>.
/// </summary>
public sealed partial class DesignConformanceTests
{
    // The packages the generator writes into: the records, the enums and the clients.
    private static readonly string[] ApiPackages = ["Community.Zabbix.Api", "Community.Zabbix.Sdk"];

    private static IEnumerable<Type> ApiPackageTypes
        => Libraries.All.Where(library => ApiPackages.Contains(library.GetName().Name)).SelectMany(library => library.GetExportedTypes());

    [Fact]
    public void No_name_of_the_api_packages_keeps_a_spelling_the_rename_map_retires()
        => AssertNone(RetiredSpellings(ApiPackageTypes));

    [Fact]
    public void The_retired_spelling_rule_sees_each_kind_of_name()
        => Assert.Equal(
            [
                "SpellingProbe.EvalType: EvaluationType",
                "SpellingProbe.Find(lldMacroId): lowLevelDiscoveryMacroId",
                "SpellingProbe.Find(userIp): userIP",
                "SpellingProbe.HaNodeId: HANodeId",
                "SpellingProbe.TechnicalName: HostName",
                "SpellingProbe.Username: UserName",
            ],
            RetiredSpellings([typeof(SpellingProbe)]).Order(StringComparer.Ordinal));

    [Fact]
    public void No_member_keeps_a_wire_name_its_type_or_its_record_says_more_than()
    {
        // words.json's respell.fields: an enum-typed Flags is a DiscoveryKind or an Origin, a History of item value types a
        // ValueType, a Priority of severities a Severity; and a record's text host is a HostName.
        Assembly api = typeof(ZabbixHost).Assembly;
        var offenders = new List<string>();
        foreach ((string field, string of) in ZabbixRespellings.RetiredFieldNames)
        {
            if (of.StartsWith("enum:", StringComparison.Ordinal))
            {
                Type kind = api.GetType("Community.Zabbix.Zabbix" + ZabbixRespellings.Name(of["enum:".Length..]))
                    ?? throw new InvalidOperationException($"respell.fields names {of}, which the API has no enum for");
                offenders.AddRange(api.GetExportedTypes()
                    .SelectMany(type => type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    .Where(property => property.Name == field && Bare(property.PropertyType) == kind)
                    .Select(property => $"{property.DeclaringType!.Name}.{property.Name}: a {kind.Name}"));
            }
            else
            {
                Type record = api.GetType("Community.Zabbix.Zabbix" + ZabbixRespellings.Name(of))
                    ?? throw new InvalidOperationException($"respell.fields names {of}, which the API has no record for");
                if (record.GetProperty(field) is not null) offenders.Add($"{record.Name}.{field}");
            }
        }

        AssertNone(offenders);
    }

    [Fact]
    public void Every_simple_enum_has_a_zero_and_every_flags_enum_names_its_zero_None()
        => AssertNone(EnumsWithoutTheirZero(Libraries.PublicTypes));

    [Fact]
    public void The_enum_zero_rule_sees_a_simple_enum_without_one_and_a_flags_enum_that_misnames_it()
        => Assert.Equal(
            ["NoZeroProbe: no zero value", "PlainFlagsProbe: its zero is Plain, not None"],
            EnumsWithoutTheirZero([typeof(NoZeroProbe), typeof(PlainFlagsProbe), typeof(NoneFlagsProbe)]).Order(StringComparer.Ordinal));

    [Fact]
    public void A_client_looks_up_by_id_only_records_with_an_id_of_their_own()
    {
        // An object the API keys by its own id (its description names the id, or its get preserves keys) has the lookups;
        // a history or trend row leads with its item's id, which its other rows share, and has none.
        ApiDescription description = ApiDescription.Read(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "schema", "zabbix-api.json")));
        Assembly sdk = typeof(ZabbixClient).Assembly;
        var offenders = new List<string>();
        int checkedClients = 0;
        foreach (ServiceDescription service in description.Services)
        {
            if (sdk.GetType("Community.Zabbix." + service.Client) is not { } client) continue;
            if (service.Methods.FirstOrDefault(m => m.Api.EndsWith(".get", StringComparison.Ordinal)) is not { } get) continue;
            TypeDescription? parameters = description.Types.FirstOrDefault(t => t.Name == get.Parameters.Replace("object:", string.Empty, StringComparison.Ordinal));
            bool ownId = service.IdField is not null || parameters?.Fields.Any(f => f.Wire == "preservekeys") == true;
            bool lookups = HasIdLookup(client);
            checkedClients++;
            if (lookups && !ownId) offenders.Add($"{client.Name}: an id lookup on records without an id of their own");
        }

        Assert.True(checkedClients > 50, $"only {checkedClients} clients were checked");
        Assert.False(HasIdLookup(typeof(ZabbixHistoryClient)));
        Assert.False(HasIdLookup(typeof(ZabbixTrendClient)));
        Assert.True(HasIdLookup(typeof(ZabbixItemClient)));
        AssertNone(offenders);
    }

    [Fact]
    public void Every_typed_item_key_parameter_is_named_key()
        => AssertNone(ItemKeyParametersNotNamedKey(Libraries.PublicTypes));

    [Fact]
    public void The_item_key_parameter_rule_sees_a_key_named_otherwise()
        => Assert.Equal(
            ["ItemKeyProbe.Send(item)", "ItemKeyProbe.Watch(itemKey)"],
            ItemKeyParametersNotNamedKey([typeof(ItemKeyProbe)]).Order(StringComparer.Ordinal));

    internal static IEnumerable<string> RetiredSpellings(IEnumerable<Type> types)
    {
        foreach (Type type in types.Where(type => !IsCompilerGenerated(type) && !type.Name.Contains('<', StringComparison.Ordinal)))
        {
            string name = type.Name.Split('`')[0];
            if (Respelled(name) is { } spelled) yield return $"{name}: {spelled}";
            foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (member is Type || IsCompilerGenerated(member) || member.Name.Contains('<', StringComparison.Ordinal)) continue;
                if (member is MethodBase { IsSpecialName: true } and not ConstructorInfo || member is FieldInfo { IsSpecialName: true }) continue;
                if (member is not ConstructorInfo && Respelled(member.Name) is { } respelled) yield return $"{name}.{member.Name}: {respelled}";
                if (member is not MethodBase method) continue;
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    if (parameter.Name is { Length: > 0 } local && RespelledLocal(local) is { } camel)
                        yield return $"{name}.{(member is ConstructorInfo ? ".ctor" : member.Name)}({local}): {camel}";
                }
            }
        }
    }

    // The name the rename map gives a PascalCase name, or null when it keeps it.
    private static string? Respelled(string name)
        => ZabbixRespellings.Name(name) is var spelled && spelled != name ? spelled : null;

    // The same for a camelCase name, whose leading acronym is in lower case (ipRange is IPRange's, userIP UserIP's).
    private static string? RespelledLocal(string local)
    {
        string pascal = char.ToUpperInvariant(local[0]) + local[1..];
        string spelled = ZabbixRespellings.Name(pascal);
        if (spelled == pascal) return null;
        int run = 0;
        while (run < spelled.Length && char.IsUpper(spelled[run])) run++;
        int lower = run == spelled.Length || run <= 1 ? run : run - 1;
        string camel = string.Concat(new string([.. spelled[..lower].Select(char.ToLowerInvariant)]), spelled[lower..]);
        return camel == local ? null : camel;
    }

    private static Type Bare(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>) ? Bare(type.GetGenericArguments()[0]) : type;
    }

    internal static IEnumerable<string> EnumsWithoutTheirZero(IEnumerable<Type> types)
    {
        foreach (Type type in types.Where(type => type.IsEnum))
        {
            string? zero = type.GetFields(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(field => Convert.ToDecimal(field.GetRawConstantValue(), System.Globalization.CultureInfo.InvariantCulture) == 0)?.Name;
            if (zero is null) yield return $"{type.Name}: no zero value";
            else if (type.IsDefined(typeof(FlagsAttribute), false) && zero != "None") yield return $"{type.Name}: its zero is {zero}, not None";
        }
    }

    // A FindAsync by a ulong id, or an indexer taking one.
    private static bool HasIdLookup(Type client)
        => client.GetMethods(BindingFlags.Public | BindingFlags.Instance).Any(method => method.Name == "FindAsync" && method.GetParameters() is [{ ParameterType: var id }, ..] && id == typeof(ulong))
            || client.GetProperties(BindingFlags.Public | BindingFlags.Instance).Any(property => property.GetIndexParameters() is [{ ParameterType: var id }] && id == typeof(ulong));

    internal static IEnumerable<string> ItemKeyParametersNotNamedKey(IEnumerable<Type> types)
    {
        foreach (Type type in types.Where(type => !IsCompilerGenerated(type)))
        {
            IEnumerable<MethodBase> methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                .Where(method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly);
            foreach (MethodBase method in methods)
            {
                // an accessor's value, an operator's operands, an equality's other value and a TryParse's result are not the
                // key a member reads
                if (method is { IsSpecialName: true } and not ConstructorInfo || method.Name is "Equals" or "CompareTo") continue;
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    Type bare = parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType()! : parameter.ParameterType;
                    bare = Nullable.GetUnderlyingType(bare) ?? bare;
                    bool key = bare == typeof(ZabbixItemKey) || (bare.IsGenericType && bare.GetGenericTypeDefinition() == typeof(ZabbixItemKey<>));
                    if (key && !parameter.IsOut && parameter.Name is not ("key" or "Key"))
                        yield return $"{type.Name}.{(method is ConstructorInfo ? ".ctor" : method.Name)}({parameter.Name})";
                }
            }
        }
    }

    private sealed class SpellingProbe
    {
        public int EvalType { get; init; }

        public ulong HaNodeId { get; init; }

        public string? TechnicalName { get; init; }

        public string? Username { get; init; }

        public string? UserName { get; init; }

        public string? IPRange { get; init; }

        public string? IpmiPassword { get; init; }

        public static void Find(ulong lldMacroId, string ipRange, string userIp, string ip) => _ = (lldMacroId, ipRange, userIp, ip);
    }

    private enum NoZeroProbe
    {
        One = 1,
    }

    [Flags]
    private enum PlainFlagsProbe
    {
        Plain = 0,
        One = 1,
    }

    [Flags]
    private enum NoneFlagsProbe
    {
        None = 0,
        One = 1,
    }

    private static class ItemKeyProbe
    {
        public static void Send(ZabbixItemKey<long> item) => _ = item;

        public static void Watch(ZabbixItemKey itemKey) => _ = itemKey;

        public static void Read(ZabbixItemKey<long> key) => _ = key;

        public static bool TryParse(string text, out ZabbixItemKey result) => ZabbixItemKey.TryParse(text, out result);
    }
}
