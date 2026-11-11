// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Runtime.CompilerServices;

namespace Community.Zabbix.Generator;

/// <summary>
/// An immutable array equal to another by its elements: what the incremental pipeline caches. An
/// <see cref="System.Collections.Immutable.ImmutableArray{T}"/> compares by reference, so a model holding one is never
/// equal to the same model read again and every step after it reruns (Roslyn's incremental-generators cookbook, "use
/// value equality"); this is the cookbook's own shape. Default is empty.
/// </summary>
/// <typeparam name="T">The element, itself equatable.</typeparam>
[CollectionBuilder(typeof(EquatableArray), nameof(EquatableArray.Create))]
internal readonly struct EquatableArray<T> : IEquatable<EquatableArray<T>>, IReadOnlyList<T>
    where T : IEquatable<T>
{
    private readonly T[]? _items;

    /// <summary>Wraps <paramref name="items"/>, which the caller no longer changes.</summary>
    /// <param name="items">The elements.</param>
    public EquatableArray(T[] items) => _items = items;

    /// <summary>How many elements.</summary>
    public int Length => _items?.Length ?? 0;

    /// <inheritdoc/>
    public int Count => Length;

    /// <summary>Whether there are none.</summary>
    public bool IsEmpty => Length == 0;

    /// <inheritdoc/>
    public T this[int index] => (_items ?? [])[index];

    /// <inheritdoc/>
    public bool Equals(EquatableArray<T> other)
    {
        T[] left = _items ?? [];
        T[] right = other._items ?? [];
        if (ReferenceEquals(left, right)) return true;
        if (left.Length != right.Length) return false;
        for (int i = 0; i < left.Length; i++)
        {
            if (!EqualityComparer<T>.Default.Equals(left[i], right[i])) return false;
        }
        return true;
    }

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        int hash = Length;
        foreach (T item in _items ?? []) hash = unchecked((hash * 31) + (item is null ? 0 : EqualityComparer<T>.Default.GetHashCode(item)));
        return hash;
    }

    /// <summary>Whether both hold equal elements in order.</summary>
    public static bool operator ==(EquatableArray<T> left, EquatableArray<T> right) => left.Equals(right);

    /// <summary>Whether they differ.</summary>
    public static bool operator !=(EquatableArray<T> left, EquatableArray<T> right) => !left.Equals(right);

    /// <inheritdoc/>
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)(_items ?? [])).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
