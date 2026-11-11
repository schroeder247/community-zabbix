// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix;

/// <summary>
/// Value equality for a record's collection and JSON members: a record's synthesized equality compares an
/// <see cref="IReadOnlyList{T}"/>, an <see cref="IReadOnlyDictionary{TKey, TValue}"/> and a <see cref="JsonElement"/> by
/// reference, so two records read from the same reply, or built from the same values, would be unequal. A list is equal by
/// its elements in order, a dictionary by its keys and their values, a JSON value by <see cref="JsonElement.DeepEquals"/>;
/// absent (<see langword="null"/>) is equal only to absent, never to empty (the wire tells the two apart). The generated
/// records (<c>ZabbixApiEmitter</c>) and the hand-written ones with a collection member write their
/// <c>Equals</c>/<c>GetHashCode</c> over these, as <see cref="ZabbixOutput"/> does over its fields.
/// </summary>
/// <remarks>
/// The hash is consistent with the equality and cheap: a list adds its elements, a dictionary its count (its order is
/// not its value), a JSON value its kind (<see cref="JsonElement.DeepEquals"/> equates <c>1</c> and <c>1.0</c>).
/// </remarks>
internal static class ZabbixValueEquality
{
    /// <summary>Whether both are absent, or hold equal elements in the same order.</summary>
    public static bool ListEqual<T>(IReadOnlyList<T>? left, IReadOnlyList<T>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        for (int i = 0; i < left.Count; i++)
        {
            if (!ItemEqual(left[i], right[i])) return false;
        }
        return true;
    }

    /// <summary>Whether both are absent, or hold the same keys with equal values.</summary>
    public static bool DictionaryEqual<TValue>(IReadOnlyDictionary<string, TValue>? left, IReadOnlyDictionary<string, TValue>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        foreach (KeyValuePair<string, TValue> entry in left)
        {
            if (!right.TryGetValue(entry.Key, out TValue? other) || !ItemEqual(entry.Value, other)) return false;
        }
        return true;
    }

    /// <summary>Whether both are absent, or hold the same keys with equal values (a mutable bag).</summary>
    public static bool DictionaryEqual<TValue>(IDictionary<string, TValue>? left, IDictionary<string, TValue>? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null || left.Count != right.Count) return false;
        foreach (KeyValuePair<string, TValue> entry in left)
        {
            if (!right.TryGetValue(entry.Key, out TValue? other) || !ItemEqual(entry.Value, other)) return false;
        }
        return true;
    }

    /// <summary>Whether both carry the same JSON (<see cref="JsonElement.DeepEquals"/>); an undefined element equals only another.</summary>
    public static bool JsonEqual(JsonElement left, JsonElement right)
        => left.ValueKind == JsonValueKind.Undefined || right.ValueKind == JsonValueKind.Undefined
            ? left.ValueKind == right.ValueKind
            : JsonElement.DeepEquals(left, right);

    /// <summary><see cref="JsonEqual(JsonElement, JsonElement)"/> for an optional member.</summary>
    public static bool JsonEqual(JsonElement? left, JsonElement? right)
        => left is { } l ? right is { } r && JsonEqual(l, r) : right is null;

    /// <summary>Adds a list to a hash: absent, or its elements in order.</summary>
    public static void AddList<T>(ref HashCode hash, IReadOnlyList<T>? list)
    {
        hash.Add(list?.Count ?? -1);
        if (list is null) return;
        foreach (T item in list) hash.Add(ItemHash(item));
    }

    /// <summary>Adds a dictionary to a hash: absent, or its count.</summary>
    public static void AddDictionary<TValue>(ref HashCode hash, IReadOnlyDictionary<string, TValue>? dictionary) => hash.Add(dictionary?.Count ?? -1);

    /// <summary>Adds a mutable bag to a hash: absent, or its count.</summary>
    public static void AddDictionary<TValue>(ref HashCode hash, IDictionary<string, TValue>? dictionary) => hash.Add(dictionary?.Count ?? -1);

    /// <summary>Adds a JSON value to a hash: its kind.</summary>
    public static void AddJson(ref HashCode hash, JsonElement? element) => hash.Add(element is { } value ? (int)value.ValueKind : -1);

    private static bool ItemEqual<T>(T left, T right)
        => left is JsonElement l && right is JsonElement r ? JsonEqual(l, r) : EqualityComparer<T>.Default.Equals(left, right);

    private static int ItemHash<T>(T item)
        => item is JsonElement element ? (int)element.ValueKind : item is null ? 0 : EqualityComparer<T>.Default.GetHashCode(item);
}
