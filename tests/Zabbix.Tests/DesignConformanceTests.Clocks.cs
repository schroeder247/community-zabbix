// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using Community.Zabbix.Tests.Conformance;

namespace Community.Zabbix.Tests;

/// <summary>
/// One time type: every public clock (<c>Clock</c>, <c>LastClock</c>, <c>NextCheck</c>'s kin named for a clock) is a
/// <see cref="DateTimeOffset"/>, and no public member carries a clock's nanoseconds on their own (<c>Ns</c>): the two
/// fields of the wire are one member.
/// </summary>
public sealed partial class DesignConformanceTests
{
    [Fact]
    public void Every_public_clock_is_a_DateTimeOffset()
        => AssertNone(ClocksOfAnotherType(Libraries.PublicTypes));

    [Fact]
    public void The_clock_rule_sees_a_number_clock_and_a_lone_ns()
        => Assert.Equal(["ClockProbe.LastClock: Int64", "ClockProbe.Ns: Int32"], ClocksOfAnotherType([typeof(ClockProbe)]).Order(StringComparer.Ordinal));

    internal static IEnumerable<string> ClocksOfAnotherType(IEnumerable<Type> types)
    {
        foreach (Type type in types.Where(type => !IsCompilerGenerated(type)))
        {
            foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Type bare = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
                bool clock = property.Name.EndsWith("Clock", StringComparison.Ordinal);
                bool nanoseconds = property.Name == "Ns" || property.Name.EndsWith("Ns", StringComparison.Ordinal) && char.IsLower(property.Name[^3]);
                if ((clock && !Carries(bare, typeof(DateTimeOffset))) || nanoseconds) yield return $"{type.Name}.{property.Name}: {bare.Name}";
            }
        }
    }

    // The type itself, or a list of it (a filter's values).
    private static bool Carries(Type type, Type value)
    {
        Type bare = Nullable.GetUnderlyingType(type) ?? type;
        return bare == value || (bare.IsGenericType && bare.GetGenericArguments() is [{ } element]
            && typeof(IEnumerable<>).MakeGenericType(element).IsAssignableFrom(bare) && Carries(element, value));
    }

    private sealed class ClockProbe
    {
        public DateTimeOffset? Clock { get; init; }

        public long LastClock { get; init; }

        public int Ns { get; init; }

        public string? Columns { get; init; }
    }
}
