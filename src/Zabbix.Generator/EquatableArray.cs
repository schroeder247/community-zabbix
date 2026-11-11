// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Generator;

/// <summary>Builds an <see cref="EquatableArray{T}"/> from a collection expression.</summary>
internal static class EquatableArray
{
    /// <summary>The elements of <paramref name="items"/>, copied.</summary>
    /// <param name="items">The elements.</param>
    public static EquatableArray<T> Create<T>(ReadOnlySpan<T> items) where T : IEquatable<T> => new(items.ToArray());
}
