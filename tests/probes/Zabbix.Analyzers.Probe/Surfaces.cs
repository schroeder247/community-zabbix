// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Analyzers;

/// <summary>
/// No List, array or mutable collection on a public surface. CA1002 and CA1819 catch the direct forms;
/// a nested one (<c>Task&lt;List&lt;T&gt;&gt;</c>) and <c>Dictionary</c>/<c>HashSet</c> pass them, which is why the
/// reflection rule <c>No_public_signature_exposes_a_mutable_collection</c> exists.
/// </summary>
public sealed class SurfacesProbe
{
    /// <summary>A list.</summary>
    /// <returns>An empty one.</returns>
    public static List<int> Items() => [];                                                  // expect: CA1002

    /// <summary>An array.</summary>
    public int[] Numbers { get; } = [];                                                     // expect: CA1819

    /// <summary>A nested list: no analyzer reports it (the reflection rule does).</summary>
    /// <returns>An empty one.</returns>
    public static Task<List<int>> ItemsAsync() => Task.FromResult<List<int>>([]);

    /// <summary>A run.</summary>
    /// <returns>When done.</returns>
    public static async Task Run() => await Task.Yield();                                   // expect: IDE1006
}
