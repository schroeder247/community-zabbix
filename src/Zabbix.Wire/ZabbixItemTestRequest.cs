// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents an <c>item.test</c> request, as the frontend's item "Test" sends it: the item, the host it's on, and how the
/// preprocessing test runs (<c>trapper_item_test.c</c> reads it).
/// </summary>
/// <example>
/// <code language="csharp">
/// ZabbixItemTestResult tested = await wire.TestItemAsync(new ZabbixItemTestRequest(new ZabbixItemTestItem
/// {
///     ItemType = 0,                                   // a Zabbix agent item
///     Key = ZabbixItemKey.Parse("agent.ping"),
///     ValueType = 3,                                  // an unsigned integer
/// })
/// {
///     Host = new() { Interface = new() { Address = "10.0.0.2", UseIP = true, Port = "10050" } },
/// });
/// Console.WriteLine(tested.Item?.Result ?? tested.Item?.Error);
/// </code>
/// </example>
public sealed record ZabbixItemTestRequest : IJsonOnSerializing
{
    /// <summary>A request with no field set; an initializer sets its <see langword="required"/> members.</summary>
    public ZabbixItemTestRequest()
    {
    }

    /// <summary>A request to test <paramref name="item"/>.</summary>
    /// <param name="item">The item under test.</param>
    [SetsRequiredMembers]
    public ZabbixItemTestRequest(ZabbixItemTestItem item)
    {
        Item = item;
    }

    /// <summary>The item under test.</summary>
    [JsonPropertyName("item")]
    [JsonRequired]
    public required ZabbixItemTestItem Item { get; set; }

    /// <summary>The host the item is on: its interface, and the proxy that collects its value.</summary>
    [JsonPropertyName("host")]
    public ZabbixItemTestHost? Host { get; set; }

    /// <summary>How the preprocessing test runs: what it assumes of the item, and the previous value it compares with.</summary>
    [JsonPropertyName("options")]
    public ZabbixItemTestConditions? Conditions { get; set; }

    /// <summary>
    /// Fields this record doesn't model, sent as they are beside the modelled ones: what a newer server reads, by its wire
    /// name.
    /// </summary>
    [JsonExtensionData]
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "System.Text.Json fills extension data through a setter, as the generated requests do.")]
    public IDictionary<string, JsonElement>? AdditionalFields { get; set; }

    private static readonly System.Collections.Frozen.FrozenSet<string> s_modelledWires = System.Collections.Frozen.FrozenSet.Create(StringComparer.Ordinal, "item", "host", "options");

    void IJsonOnSerializing.OnSerializing() => ZabbixExtensionDataGuard.Refuse(AdditionalFields, nameof(ZabbixItemTestRequest), s_modelledWires);

    /// <summary>Whether <paramref name="other"/> holds the same values, its fields compared by value.</summary>
    /// <param name="other">The other request.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixItemTestRequest? other)
        => other is not null
            && Equals(Item, other.Item)
            && Equals(Host, other.Host)
            && Equals(Conditions, other.Conditions)
            && ZabbixValueEquality.DictionaryEqual(AdditionalFields, other.AdditionalFields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Item);
        hash.Add(Host);
        hash.Add(Conditions);
        ZabbixValueEquality.AddDictionary(ref hash, AdditionalFields);
        return hash.ToHashCode();
    }
}
