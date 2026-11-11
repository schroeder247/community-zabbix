// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Applies the generic <c>-AdditionalProperty</c> escape hatch: every generated <c>New-</c>/<c>Set-</c> takes a
/// <c>-AdditionalProperty [hashtable]</c> merged onto the built request last, so it wins over every named parameter.
/// <c>ZabbixApiEmitter</c> gives every generated request/parameters record (<c>*CreateRequest</c>, <c>*UpdateRequest</c>,
/// <c>*MassUpdateRequest</c>, <c>*GetParameters</c>) its own <c>AdditionalFields</c> bag (a <c>JsonExtensionData</c>
/// property) for a wire field none of them model at all.
///
/// Built on <see cref="ZabbixJsonBinder"/>: serialize the request the cmdlet's named parameters already built,
/// serialize the <c>-AdditionalProperty</c> hashtable after remapping its keys to their wire names, merge (the override wins),
/// deserialize once.
/// </summary>
internal static class ZabbixPropertyOverrides
{
    /// <summary>Applies <paramref name="properties"/> onto <paramref name="request"/> and returns the merged result.</summary>
    /// <typeparam name="TRequest">The request; must be part of the SDK's JSON contract, <c>ZabbixJson</c> (every generated request/parameters record is).</typeparam>
    /// <param name="request">The request built from the cmdlet's named parameters.</param>
    /// <param name="properties">The <c>-AdditionalProperty</c> hashtable, or <see langword="null"/> when not bound.</param>
    /// <exception cref="ArgumentException">
    /// Two distinct keys in <paramref name="properties"/> resolve to the same wire field with different values (see
    /// <see cref="ZabbixJsonBinder.RemapKeys"/>), or a value does not fit its field (<see cref="ZabbixParameterException"/>).
    /// </exception>
    public static TRequest Apply<TRequest>(TRequest request, Hashtable? properties)
        where TRequest : notnull
    {
        ArgumentNullException.ThrowIfNull(request);
        if (properties is null || properties.Count == 0) return request;
        JsonTypeInfo<TRequest> typeInfo = TypeInfo<TRequest>();
        JsonElement namedElement = JsonSerializer.SerializeToElement(request, typeInfo);
        JsonObject named = namedElement.ValueKind == JsonValueKind.Object ? JsonObject.Create(namedElement)! : [];
        JsonObject merged = Merge(named, ZabbixJsonBinder.ToJsonObject(properties, typeof(TRequest)));
        try
        {
            return merged.Deserialize(typeInfo)!;
        }
        catch (JsonException refused)
        {
            // The value that does not fit is the caller's -AdditionalProperty, not the server's answer (a JsonException is
            // otherwise InvalidResult): an InvalidArgument error naming the field.
            throw new ZabbixParameterException("ZabbixPropertyInvalid", "AdditionalProperty",
                $"-AdditionalProperty: {refused.Path?.TrimStart('$', '.') ?? "a value"} does not fit {typeof(TRequest).Name}: {refused.Message}");
        }
    }

    /// <summary>
    /// Whether <paramref name="properties"/> carries a key naming this field — by its own wire name
    /// (<paramref name="wireField"/>, e.g. <c>ipmi_username</c>) or, since <see cref="Apply{TRequest}"/> accepts
    /// either, its C# property name (<paramref name="propertyName"/>, e.g. <c>IpmiUserName</c>). Used by generated
    /// <c>CreateAsync</c>/<c>BuildRequest</c> to decide whether a required field (which a
    /// generated <c>New-</c>/<c>Set-</c> does not mark <c>Mandatory</c>) was nonetheless supplied — through
    /// <c>-AdditionalProperty</c>, when its own named parameter was not bound.
    /// </summary>
    /// <param name="properties">The <c>-AdditionalProperty</c> hashtable, or <see langword="null"/> when not bound.</param>
    /// <param name="propertyName">The field's C# property name.</param>
    /// <param name="wireField">The field's wire name.</param>
    public static bool SuppliesWireField(Hashtable? properties, string propertyName, string wireField)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);
        ArgumentException.ThrowIfNullOrWhiteSpace(wireField);
        if (properties is null) return false;
        foreach (DictionaryEntry entry in properties)
        {
            if (entry.Key is not string key) continue;
            if (string.Equals(key, wireField, StringComparison.OrdinalIgnoreCase) || string.Equals(key, propertyName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// Merges <paramref name="overrides"/> onto <paramref name="baseObject"/> (already-serialized JSON, e.g. a
    /// partially-built request from a cmdlet's named parameters) — a key <paramref name="overrides"/> also carries
    /// always wins over <paramref name="baseObject"/>'s own value for it: <c>-AdditionalProperty</c> is the last word, so
    /// <c>New-ZbxHostGroup -Name 'Databases' -AdditionalProperty @{ Name = 'Overridden' }</c> sends <c>Overridden</c>, not an
    /// error (<see cref="ZabbixJsonBinder.RemapKeys"/>'s conflict check is a different case — two distinct
    /// <paramref name="overrides"/> keys resolving to the same wire field). Returns a new object; neither argument is
    /// mutated.
    /// </summary>
    public static JsonObject Merge(JsonObject baseObject, JsonObject overrides)
    {
        ArgumentNullException.ThrowIfNull(baseObject);
        ArgumentNullException.ThrowIfNull(overrides);
        var merged = new JsonObject();
        foreach ((string key, JsonNode? value) in baseObject) merged[key] = value?.DeepClone();
        foreach ((string key, JsonNode? value) in overrides) merged[key] = value?.DeepClone();
        return merged;
    }

    private static JsonTypeInfo<TRequest> TypeInfo<TRequest>()
        => ZabbixContracts.TryFind(typeof(TRequest), out JsonTypeInfo? contract)
            ? (JsonTypeInfo<TRequest>)contract
            : throw new InvalidOperationException($"{typeof(TRequest).Name} is not part of the SDK's JSON contract (ZabbixJson) — -AdditionalProperty cannot bind it.");
}
