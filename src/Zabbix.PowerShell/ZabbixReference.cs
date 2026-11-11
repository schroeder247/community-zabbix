// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Globalization;
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Represents a reference to an API object: a parameter that points at an API object takes its id, its name, or the
/// object itself piped from a <c>Get-</c> cmdlet, and becomes an id before the call — one parameter, three spellings,
/// one resolution.
/// </summary>
/// <typeparam name="TEntity">The object referred to.</typeparam>
public sealed record ZabbixReference<TEntity>
{
    private ZabbixReference(ulong? id, string? name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>The id, when it is known without asking the server.</summary>
    public ulong? Id { get; }

    /// <summary>The name, when the reference was written as one and the id is not yet known.</summary>
    public string? Name { get; }

    /// <summary>A reference by id.</summary>
    /// <param name="id">The id.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "The reference's factories name the entity they reference, as ImmutableArray<T>.Empty names its element; the type argument is the point of the call.")]
    public static ZabbixReference<TEntity> ById(ulong id) => new(id, null);

    /// <summary>A reference by name; the server resolves it.</summary>
    /// <param name="name">The name.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "The reference's factories name the entity they reference, as ImmutableArray<T>.Empty names its element; the type argument is the point of the call.")]
    public static ZabbixReference<TEntity> ByName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return new ZabbixReference<TEntity>(null, name);
    }

    /// <summary>A reference to an object read from the server: its id, and its name for messages.</summary>
    /// <param name="entity">The object.</param>
    /// <exception cref="ArgumentException">The object carries no id (it was read with an <c>output</c> that left the id out).</exception>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "The reference's factories name the entity they reference, as ImmutableArray<T>.Empty names its element; the type argument is the point of the call.")]
    public static ZabbixReference<TEntity> Of(TEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ZabbixEntityShape<TEntity> shape = ZabbixEntities.Of<TEntity>();
        return shape.IdOf(entity) is { } id
            ? new ZabbixReference<TEntity>(id, shape.NameOf(entity))
            : throw new ArgumentException($"the {shape.Api} carries no {shape.IdField}: read it with an output that includes the id", nameof(entity));
    }

    /// <summary>
    /// Reads one argument as a reference: a reference as is, an object of <typeparamref name="TEntity"/>, a
    /// <see cref="ulong"/>, an integer, a decimal string (an id), or any other string (a name). A
    /// <see cref="PSObject"/> is unwrapped first.
    /// </summary>
    /// <param name="value">The argument.</param>
    /// <param name="reference">The reference.</param>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1000:Do not declare static members on generic types", Justification = "The reference's factories name the entity they reference, as ImmutableArray<T>.Empty names its element; the type argument is the point of the call.")]
    public static bool TryFrom(object? value, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ZabbixReference<TEntity>? reference)
    {
        reference = (value is PSObject wrapped ? wrapped.BaseObject : value) switch
        {
            ZabbixReference<TEntity> given => given,
            TEntity entity when ZabbixEntities.TryGet(out ZabbixEntityShape<TEntity>? shape)
                                && shape.IdOf(entity) is not null => Of(entity),
            ulong id => ById(id),
            string text when ulong.TryParse(text, CultureInfo.InvariantCulture, out ulong id) => ById(id),
            string { Length: > 0 } text when !string.IsNullOrWhiteSpace(text) => ByName(text),
            int number and >= 0 => ById((ulong)number),
            long number and >= 0 => ById((ulong)number),
            uint number => ById(number),
            // A record that carries the object's id by its own name (an Import-Csv row, a [pscustomobject]@{ HostId = 7 }):
            // the id binds as Get-'s -Id does by property name. Only such a record — a
            // typed object of another entity (an item carrying a HostId) stays refused, so a change never lands on an
            // object the pipeline did not name.
            PSCustomObject when value is PSObject record && IdOfRecord(record) is { } id => ById(id),
            _ => null,
        };
        return reference is not null;
    }

    // The typed record's own name for its id (UserGroupId for usrgrpid): what Get-'s -Id takes by property name as an alias.
    private static readonly string s_idMember = ZabbixEntities.TryGet(out ZabbixEntityShape<TEntity>? idShape)
        ? typeof(TEntity).GetProperties().FirstOrDefault(property => property.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), inherit: true)
            .OfType<System.Text.Json.Serialization.JsonPropertyNameAttribute>().Any(name => name.Name == idShape.IdField))?.Name ?? idShape.IdField
        : string.Empty;

    private static ulong? IdOfRecord(PSObject record)
    {
        if (!ZabbixEntities.TryGet(out ZabbixEntityShape<TEntity>? shape)) return null;
        // By the wire's name or the typed record's (usrgrpid or UserGroupId); PSObject's member lookup ignores case, so
        // the wire's "hostid" also names a CSV's HostId column.
        object? id = (record.Properties[shape.IdField] ?? record.Properties[s_idMember])?.Value;
        id = id is PSObject wrapped ? wrapped.BaseObject : id;
        return id switch
        {
            ulong given => given,
            string text when ulong.TryParse(text, CultureInfo.InvariantCulture, out ulong parsed) => parsed,
            int number and > 0 => (ulong)number,
            long number and > 0 => (ulong)number,
            _ => null,
        };
    }

    /// <summary>
    /// The id as the wire's string, for a nested-record builder (<c>New-ZbxActionOperation -HostGroupIds</c>): a builder
    /// talks to no server, so a reference given by name has no id it could write, and is refused rather than dropped.
    /// </summary>
    /// <param name="parameter">The builder's parameter, for the message.</param>
    /// <exception cref="ArgumentException">The reference is a name.</exception>
    internal ulong RequireId(string parameter)
        => Id ?? throw new ArgumentException(string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"-{parameter} '{Name}': a nested-record builder resolves no names; give the {typeof(TEntity).Name[6..]}'s id, or the object itself (e.g. from Get-Zbx{typeof(TEntity).Name[6..]})."), parameter);

    /// <summary>The id, or the name in quotes.</summary>
    public override string ToString() => Id?.ToString(CultureInfo.InvariantCulture) ?? $"'{Name}'";
}

/// <summary>
/// Binds any spelling of a reference — id, name, piped object, or a list of them — to
/// <see cref="ZabbixReference{TEntity}"/> or an array of it, at parameter binding. Nothing is resolved here: a name stays a name
/// until the cmdlet resolves every name of the call in one <c>.get</c> under its stop token.
/// </summary>
/// <typeparam name="TEntity">The object referred to.</typeparam>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixReferenceAttribute<TEntity> : ArgumentTransformationAttribute
{
    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        // A [pscustomobject] stays wrapped: its members live on the PSObject (TryFrom reads the id from them).
        object? input = inputData is PSObject { BaseObject: not PSCustomObject } wrapped && wrapped.BaseObject is not TEntity ? wrapped.BaseObject : inputData;
        if (input is null) return null;
        // Id 0 is Zabbix's "none" (a host's templateid/maintenanceid when it has none): never a reference. On
        // 8.0.0 a piped host binds its own TemplateId/MaintenanceId "0" by property name onto
        // Get-ZbxTrigger -Template/-Maintenance, and templateids: [0] empties the result. So a 0 is dropped, and a
        // parameter given only 0s is not bound at all.
        if (ZabbixReference<TEntity>.TryFrom(input, out ZabbixReference<TEntity>? single)) return IsNone(single) ? null : single;
        if (input is IEnumerable many and not string)
        {
            var references = new List<ZabbixReference<TEntity>>();
            bool any = false;
            foreach (object? item in many)
            {
                any = true;
                ZabbixReference<TEntity> reference = ZabbixReference<TEntity>.TryFrom(item, out ZabbixReference<TEntity>? parsed) ? parsed : throw Refuse(item);
                if (!IsNone(reference)) references.Add(reference);
            }
            // An empty list given is a list, and sent: Set-ZbxProxy -Host @() empties the proxy's hosts rather than
            // being dropped as unbound. Only a list of nothing but "none" ids is no reference at all.
            return references.Count == 0 && any ? null : references.ToArray();
        }
        throw Refuse(input);
    }

    private static bool IsNone(ZabbixReference<TEntity> reference) => reference.Id is 0;

    private static ArgumentTransformationMetadataException Refuse(object? value)
        => new(string.Create(CultureInfo.InvariantCulture,
            $"'{value}' ({value?.GetType().Name ?? "null"}) is not a reference to a {typeof(TEntity).Name}: give its id, its name, or the object itself."));
}
