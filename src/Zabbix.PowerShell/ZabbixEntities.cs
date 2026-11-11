// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Frozen;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Provides the explicit registry of entity shapes (registry records, never assembly scanning). Generated wholly by
/// Zabbix.Generator (ZabbixCmdletEmitter.EmitEntities) from every API object with a <c>read</c>
/// model and a <c>get</c> parameters record: this file declares the partial <see cref="Build"/> the generated
/// <c>ZabbixEntities.g.cs</c> implements, and the two lookup methods every command uses.
/// </summary>
internal static partial class ZabbixEntities
{
    /// <summary>Every registered shape, keyed by its entity type; implemented in the generated <c>ZabbixEntities.g.cs</c>.</summary>
    private static partial Dictionary<Type, object> Build();

    private static readonly FrozenDictionary<Type, object> s_shapes = Build().ToFrozenDictionary();

    /// <summary>The shape of <typeparamref name="TEntity"/>.</summary>
    /// <typeparam name="TEntity">The read model record.</typeparam>
    /// <exception cref="KeyNotFoundException">No shape is registered for <typeparamref name="TEntity"/>.</exception>
    public static ZabbixEntityShape<TEntity> Of<TEntity>()
        => TryGet(out ZabbixEntityShape<TEntity>? shape)
            ? shape
            : throw new KeyNotFoundException($"no entity shape is registered for {typeof(TEntity).Name}");

    /// <summary>The shape of <typeparamref name="TEntity"/>, when one is registered.</summary>
    /// <typeparam name="TEntity">The read model record.</typeparam>
    /// <param name="shape">The shape.</param>
    public static bool TryGet<TEntity>([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ZabbixEntityShape<TEntity>? shape)
    {
        shape = s_shapes.TryGetValue(typeof(TEntity), out object? found) ? (ZabbixEntityShape<TEntity>)found : null;
        return shape is not null;
    }

    /// <summary>Whether <paramref name="type"/> is a registered read record (<c>ZabbixHost</c>, <c>ZabbixItemKey</c>).</summary>
    /// <param name="type">The type.</param>
    public static bool IsEntity(Type type) => s_shapes.ContainsKey(type);

    // Each read record's own name for its id (HostId for hostid): the property a piped record of that object binds its
    // own id by.
    private static readonly FrozenDictionary<Type, string> s_idMembers = s_shapes.ToFrozenDictionary(
        entry => entry.Key,
        entry =>
        {
            string idField = (string)entry.Value.GetType().GetProperty(nameof(ZabbixEntityShape<object>.IdField))!.GetValue(entry.Value)!;
            return entry.Key.GetProperties().FirstOrDefault(property => property.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), inherit: true)
                .OfType<System.Text.Json.Serialization.JsonPropertyNameAttribute>().Any(name => name.Name == idField))?.Name ?? idField;
        });

    /// <summary>
    /// The member of a registered read record that holds the object's own id (<c>HostId</c> on <c>ZabbixHost</c>), or
    /// <see langword="null"/> when <paramref name="type"/> is not one.
    /// </summary>
    /// <param name="type">The type.</param>
    public static string? IdMemberOf(Type type) => s_idMembers.GetValueOrDefault(type);
}
