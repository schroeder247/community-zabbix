// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Reflection;
using System.Text.Json;
using Community.Zabbix.Tests.Conformance;

namespace Community.Zabbix.Tests;

/// <summary>
/// A value is a record, and a record's synthesized equality compares a list, a dictionary and a
/// <see cref="JsonElement"/> by reference, so a record holding one must write its equality over the values. Every public
/// record of every library is built twice from equal, separately allocated values: the two must be equal and hash alike.
/// </summary>
public sealed partial class DesignConformanceTests
{
    [Fact]
    public void Every_public_record_with_a_collection_or_json_member_compares_it_by_value()
    {
        List<string> offenders = [];
        int sampled = 0;
        foreach (Type type in Libraries.PublicTypes.Where(IsRecord).Where(type => !type.IsAbstract && !type.ContainsGenericParameters))
        {
            if (!ValueMembers(type).Any()) continue;
            object? left = Sample(type);
            object? right = Sample(type);
            if (left is null || right is null)
            {
                offenders.Add(type.FullName + ": no sample could be built");
                continue;
            }
            sampled++;
            if (!left.Equals(right)) offenders.Add(type.FullName + ": two records of equal values are unequal");
            else if (left.GetHashCode() != right.GetHashCode()) offenders.Add(type.FullName + ": two equal records hash differently");
        }

        AssertNone(offenders);
        Assert.True(sampled > 300, $"only {sampled} records with a collection member were sampled");
    }

    [Fact]
    public void A_generated_record_s_lists_compare_in_order_and_absent_is_not_empty()
    {
        var first = new ZabbixHostGroupGetParameters { HostIds = [1, 2] };

        Assert.Equal(first, new ZabbixHostGroupGetParameters { HostIds = [1, 2] });
        Assert.Equal(first.GetHashCode(), new ZabbixHostGroupGetParameters { HostIds = [1, 2] }.GetHashCode());
        Assert.True(first == new ZabbixHostGroupGetParameters { HostIds = [1, 2] });
        Assert.NotEqual(first, new ZabbixHostGroupGetParameters { HostIds = [2, 1] });
        Assert.NotEqual(new ZabbixHostGroupGetParameters { HostIds = [] }, new ZabbixHostGroupGetParameters());
        Assert.NotEqual(first, first with { AdditionalFields = new Dictionary<string, JsonElement> { ["x"] = JsonDocument.Parse("1").RootElement } });
        Assert.Equal(
            first with { AdditionalFields = new Dictionary<string, JsonElement> { ["x"] = JsonDocument.Parse("1").RootElement } },
            first with { AdditionalFields = new Dictionary<string, JsonElement> { ["x"] = JsonDocument.Parse("1.0").RootElement } });
    }

    private static bool IsRecord(Type type)
        => type.GetProperty("EqualityContract", BindingFlags.Instance | BindingFlags.NonPublic) is not null
            || (type.IsValueType && type.GetMethod("PrintMembers", BindingFlags.Instance | BindingFlags.NonPublic) is not null);

    // The members a synthesized equality would compare by reference: lists, dictionaries, JSON, in the record and its bases.
    private static IEnumerable<PropertyInfo> ValueMembers(Type type)
        => type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(property => property.GetIndexParameters().Length == 0 && IsValueShape(property.PropertyType));

    private static bool IsValueShape(Type type)
    {
        Type bare = Nullable.GetUnderlyingType(type) ?? type;
        return bare == typeof(JsonElement) || (bare != typeof(string) && typeof(IEnumerable).IsAssignableFrom(bare));
    }

    /// <summary>A record whose every settable member holds a freshly built value, or null when none can be built.</summary>
    private static object? Sample(Type type)
    {
        try
        {
            ConstructorInfo? empty = type.GetConstructor(Type.EmptyTypes);
            object record;
            if (empty is not null || type.IsValueType)
            {
                record = empty is not null ? empty.Invoke(null) : Activator.CreateInstance(type)!;
            }
            else
            {
                ConstructorInfo? widest = type.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(c => !(c.GetParameters() is [{ } only] && only.ParameterType == type))                  // not the copy constructor
                    .OrderByDescending(c => c.GetParameters().Length).FirstOrDefault();
                if (widest is null) return null;
                record = widest.Invoke([.. widest.GetParameters().Select(p => Value(p.ParameterType))]);
            }
            foreach (PropertyInfo property in ValueMembers(type))
            {
                MethodInfo? set = property.GetSetMethod(nonPublic: true);
                if (set is not null) set.Invoke(record, [Value(property.PropertyType)]);
            }
            return record;
        }
        catch (Exception exception) when (exception is TargetInvocationException or ArgumentException)
        {
            return null;
        }
    }

    private static object? Value(Type type)
    {
        Type bare = Nullable.GetUnderlyingType(type) ?? type;
        if (bare == typeof(string)) return "x";
        if (bare == typeof(JsonElement)) return JsonDocument.Parse("[1]").RootElement.Clone();
        if (bare.IsArray) return Array(bare.GetElementType()!);
        if (bare.IsGenericType && bare.GetGenericArguments() is [Type key, Type item] && typeof(IEnumerable).IsAssignableFrom(bare))
        {
            var dictionary = (IDictionary)Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(key, item))!;
            dictionary.Add(Value(key)!, Value(item));
            return dictionary;
        }
        if (bare.IsGenericType && bare.GetGenericArguments() is [Type element] && typeof(IEnumerable).IsAssignableFrom(bare))
            return Array(element);
        if (bare.IsEnum) return Activator.CreateInstance(bare);
        if (bare.IsPrimitive) return Convert.ChangeType(1, bare, System.Globalization.CultureInfo.InvariantCulture);   // an id, a count: positive
        if (bare == typeof(ulong)) return 1UL;                                    // an id names an object: not 0
        if (bare.IsValueType) return Activator.CreateInstance(bare);
        return bare.GetConstructor(Type.EmptyTypes)?.Invoke(null);
    }

    private static Array Array(Type element)
    {
        var array = System.Array.CreateInstance(element, 1);
        array.SetValue(Value(element), 0);
        return array;
    }
}
