// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents a field of <typeparamref name="TRecord"/>, by the name the API gives it, for a get's <c>output</c>, its
/// <c>select</c> options and its sort: <c>q.Output = [ZabbixHostFields.HostId, ZabbixHostFields.VisibleName]</c>.
/// A field of another record is another type, so a host's field doesn't compile in an item's query.
/// </summary>
/// <remarks>
/// The <c>Zabbix*Fields</c> classes hold every field the SDK knows. To name one it doesn't know, such as a field a newer
/// Zabbix adds, construct it: <c>new ZabbixField&lt;ZabbixHost&gt;("a_new_field")</c>. The default value names no field,
/// and a request that holds it isn't sent.
/// </remarks>
/// <typeparam name="TRecord">The record the field belongs to.</typeparam>
public readonly record struct ZabbixField<TRecord>
{
    private readonly string? _name;

    /// <summary>Initializes a field from the name the API gives it.</summary>
    /// <param name="name">The field's name, as Zabbix spells it: <c>hostid</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public ZabbixField(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _name = name;
    }

    /// <summary>Gets the name the API gives the field; empty for the default value.</summary>
    public string Name => _name ?? string.Empty;

    /// <summary>Sorts by this field from the lowest value to the highest, as a bare field in a sort does.</summary>
    /// <returns>The sort.</returns>
    public ZabbixSort<TRecord> Ascending() => new(this, ListSortDirection.Ascending);

    /// <summary>Sorts by this field from the highest value to the lowest: <c>q.Sort = [ZabbixHostFields.VisibleName.Descending()]</c>.</summary>
    /// <returns>The sort.</returns>
    public ZabbixSort<TRecord> Descending() => new(this, ListSortDirection.Descending);

    /// <summary>Returns the name the API gives the field.</summary>
    /// <returns>The field's name; empty for the default value.</returns>
    public override string ToString() => Name;
}

/// <summary>
/// Writes a get's <c>sortfield</c> as the list of its fields' names, and reads a list or one name, as the API takes
/// either. Owned because <see cref="ZabbixField{TRecord}"/> is generic and System.Text.Json's source generator takes a
/// converter only for a closed type, so each generated record names this one for its own.
/// </summary>
/// <typeparam name="TRecord">The record the fields belong to.</typeparam>
internal sealed class ZabbixFieldListConverter<TRecord> : JsonConverter<IReadOnlyList<ZabbixField<TRecord>>>
{
    /// <inheritdoc/>
    public override IReadOnlyList<ZabbixField<TRecord>> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String) return [Field(reader.GetString())];
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("A sort field is a field's name or a list of names.");
        var fields = new List<ZabbixField<TRecord>>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            fields.Add(reader.TokenType == JsonTokenType.String ? Field(reader.GetString()) : throw new JsonException($"A sort field's list holds a {reader.TokenType}, not a name."));
        return fields;
    }

    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, IReadOnlyList<ZabbixField<TRecord>> value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);
        writer.WriteStartArray();
        foreach (ZabbixField<TRecord> field in value) writer.WriteStringValue(Named(field));
        writer.WriteEndArray();
    }

    /// <summary>The field's name, or an exception for the default value, which names no field.</summary>
    /// <param name="field">The field to write.</param>
    /// <returns>The field's name.</returns>
    /// <exception cref="ArgumentException"><paramref name="field"/> is the default value.</exception>
    internal static string Named(ZabbixField<TRecord> field)
        => field.Name.Length > 0 ? field.Name : throw new ArgumentException($"A field of {typeof(TRecord).Name} names no field: use a Zabbix*Fields member, or new ZabbixField<T>(name) for a field the SDK doesn't know.", nameof(field));

    private static ZabbixField<TRecord> Field(string? name)
        => string.IsNullOrEmpty(name) ? throw new JsonException("A field's name is empty.") : new(name);
}
