// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Globalization;
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Binds a flat hashtable of names to values onto a nested <c>{name, value}</c> list parameter — a
/// <c>*TagRequest[]?</c> (<c>tag</c>/<c>value</c>) or a <c>{name, value}</c> list (<c>New-ZbxScript -WebhookParameters</c>,
/// item/httptest <c>-Header</c>, <c>-Variable</c>, <c>-QueryField</c>, <c>-Parameter</c>):
/// <c>@{ env = 'staging'; role = 'worker' }</c> becomes two tags.
/// </summary>
/// <remarks>
/// <see cref="ZabbixRecordConverter"/> alone cannot tell "two pairs" from "one pair object with two fields", so a bare
/// dictionary carrying no name-field key (<c>Tag</c>/<c>Name</c>, case-insensitively) is reshaped into an array of
/// <c>{name, value}</c> hashtables before <see cref="LanguagePrimitives.ConvertTo(object,Type)"/> runs. Every other
/// shape — a hashtable that carries the name key (<c>@{ Tag = 'x'; Value = 'y' }</c>), an array of either shape, or
/// already-typed <typeparamref name="TRecord"/> instances — is left to the binder. An <c>[ordered]</c> dictionary keeps
/// its order (headers are sent in the order given).
/// </remarks>
/// <typeparam name="TRecord">The owner's own pair record (e.g. <c>ZabbixTagRequest</c>, <c>ZabbixScriptParameterRequest</c>).</typeparam>
/// <param name="nameField">The record's name wire field: <c>tag</c> or <c>name</c>.</param>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixNameValueListAttribute<TRecord>(string nameField) : ArgumentTransformationAttribute
    where TRecord : notnull
{
    /// <summary>The record's name wire field: <c>tag</c> or <c>name</c>.</summary>
    public string NameField { get; } = nameField;

    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        object? input = inputData is PSObject { BaseObject: not PSCustomObject } wrapped ? wrapped.BaseObject : inputData;
        if (input is null) return null;

        // A dictionary is itself IEnumerable (of DictionaryEntry) — excluded from the general "is this a collection
        // of several pair-shaped things" branch, so a bare hashtable is always treated as ONE top-level item, never
        // enumerated element-wise into DictionaryEntry values that match neither shape. The flat-vs-name-keyed shape
        // decision then applies to EVERY item this way, whether the caller wrote one bare hashtable or an array
        // (@(@{ env = 'x' })) — a flat dictionary found at either level expands to one pair per key; anything else (an
        // already-name-keyed hashtable, an already-typed TRecord) is one item.
        IEnumerable<object?> topLevel = input is IEnumerable many and not string and not IDictionary ? many.Cast<object?>() : [input];
        IEnumerable<object?> items = topLevel.SelectMany(item =>
        {
            object? unwrapped = item is PSObject { BaseObject: not PSCustomObject } wrappedItem ? wrappedItem.BaseObject : item;
            return unwrapped is IDictionary table && !HasKey(table, NameField)
                ? FlattenToPairs(table).Cast<object?>()
                : (IEnumerable<object?>)[unwrapped];
        });

        var pairs = new List<TRecord>();
        foreach (object? raw in items)
        {
            try
            {
                pairs.Add(LanguagePrimitives.ConvertTo<TRecord>(raw));
            }
            catch (Exception failure) when (failure is ArgumentException or PSInvalidCastException)
            {
                throw Refuse(raw, failure);
            }
        }
        return pairs.ToArray();
    }

    // The flat several-at-once idiom (@{ env = 'x'; role = 'y' } -> two pairs): one {name, value}
    // hashtable per key/value pair, keyed by the record's own wire names ("tag"/"name" and "value" — ZabbixApiEmitter's
    // own naming), so ZabbixJsonBinder needs no further remap for these keys.
    private IEnumerable<Hashtable> FlattenToPairs(IDictionary table)
    {
        foreach (DictionaryEntry entry in table)
        {
            object? value = entry.Value is PSObject { BaseObject: not PSCustomObject } wrapped ? wrapped.BaseObject : entry.Value;
            if (value is IDictionary || (value is IEnumerable and not string))
                throw Refuse(table, null);
            yield return new Hashtable(StringComparer.OrdinalIgnoreCase) { [NameField] = entry.Key?.ToString(), ["value"] = value };
        }
    }

    private static bool HasKey(IDictionary table, string key)
        => table.Keys.Cast<object>().Any(k => string.Equals(k.ToString(), key, StringComparison.OrdinalIgnoreCase));

    private ArgumentTransformationMetadataException Refuse(object? value, Exception? inner)
        => new(string.Create(CultureInfo.InvariantCulture,
            $"'{value}' is not a {NameField}/value pair: give a hashtable of {NameField} to value (e.g. @{{ env = 'x' }}), a hashtable with {NameField}/value keys, or a {typeof(TRecord).Name}."), inner);
}
