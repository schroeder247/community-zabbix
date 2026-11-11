// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Text.RegularExpressions;
using Community.Zabbix.Tests.Conformance;

namespace Community.Zabbix.Tests;

/// <summary>
/// One id type: a Zabbix object id is <see cref="ulong"/> on every public member of every package, so an id read from
/// the API joins one read from the wire or a host with no conversion. A member is an id by its name (<c>HostId</c>,
/// <c>ItemIds</c>, <c>Id</c>); the exemptions are computed from the member, never listed: a secret (a session's id is a
/// credential, not an object's key), and a name the API description types as something else (an OAuth client's
/// <c>ClientId</c>, a trace's <c>TraceId</c>, a log's <c>LogEventId</c>). The generated records follow the description's
/// kinds by construction, so the rule reads the hand-written packages and takes those names from the generated ones.
/// </summary>
public sealed partial class DesignConformanceTests
{
    [Fact]
    public void Every_public_id_is_a_ulong()
    {
        Assembly described = typeof(ZabbixHost).Assembly;
        HashSet<string> notIds = [.. IdsOfAnotherType(described.GetExportedTypes(), []).Select(offender => offender[(offender.IndexOf('.', StringComparison.Ordinal) + 1)..offender.IndexOf(':', StringComparison.Ordinal)])];

        AssertNone(IdsOfAnotherType(Libraries.PublicTypes.Where(type => type.Assembly != described), notIds));
    }

    [Fact]
    public void The_id_rule_sees_a_text_id_a_number_list_and_a_parameter()
        => Assert.Equal(
            ["IdProbe.Fetch(itemIds): IEnumerable`1", "IdProbe.HostId: String", "IdProbe.ProxyIds: IReadOnlyList`1"],
            IdsOfAnotherType([typeof(IdProbe)], ["ClientId"]).Order(StringComparer.Ordinal));

    internal static IEnumerable<string> IdsOfAnotherType(IEnumerable<Type> types, HashSet<string> notIds)
    {
        foreach (Type type in types.Where(type => !IsCompilerGenerated(type)))
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (IsId(property.Name) && !notIds.Contains(property.Name) && !IsSecret(property) && !CarriesZabbixId(property.PropertyType))
                    yield return $"{type.Name}.{property.Name}: {property.PropertyType.Name}";
            }
            foreach (MethodBase method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(method => !method.IsSpecialName)
                .Cast<MethodBase>()
                .Concat(type.GetConstructors()))
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    if (parameter.Name is { } name && IsId(name) && !notIds.Contains(char.ToUpperInvariant(name[0]) + name[1..]) && !IsSecret(parameter) && !CarriesZabbixId(parameter.ParameterType)
                        && type.GetProperty(char.ToUpperInvariant(name[0]) + name[1..]) is null)       // a record's own parameter is its property's
                        yield return $"{type.Name}.{(method is ConstructorInfo ? ".ctor" : method.Name)}({name}): {parameter.ParameterType.Name}";
                }
            }
        }
    }

    // A word ending the name in Id or Ids, in either case of its first letter: HostId, hostIds, Id, ids.
    private static bool IsId(string name) => IdName().IsMatch(name);

    private static bool IsSecret(ICustomAttributeProvider member) => member.IsDefined(typeof(ZabbixSecretAttribute), inherit: false);

    private static bool CarriesZabbixId(Type type)
    {
        Type element = Nullable.GetUnderlyingType(type) ?? type;
        if (element == typeof(ulong)) return true;
        if (element.IsArray) return CarriesZabbixId(element.GetElementType()!);
        return element.IsGenericType && element.GetGenericArguments() is [{ } argument] && typeof(IEnumerable<>).MakeGenericType(argument).IsAssignableFrom(element) && CarriesZabbixId(argument);
    }

    [GeneratedRegex("(^|[a-z0-9])(Id|Ids)$|^(id|ids)$", RegexOptions.CultureInvariant)]
    private static partial Regex IdName();

    private sealed class IdProbe
    {
        public string? HostId { get; init; }

        public ulong GroupId { get; init; }

        public IReadOnlyList<long>? ProxyIds { get; init; }

        public IReadOnlyList<ulong>? TemplateIds { get; init; }

        [ZabbixSecret]
        public string? SessionId { get; init; }

        public string? Uuid { get; init; }

        public string? ClientId { get; init; }

        public static void Fetch(IEnumerable<string> itemIds, IEnumerable<ulong> hostIds) => _ = (itemIds, hostIds);
    }
}
