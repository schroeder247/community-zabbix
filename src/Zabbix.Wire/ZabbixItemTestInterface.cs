// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents the host interface an item is tested on: where the server connects to collect its value.
/// </summary>
public sealed record ZabbixItemTestInterface : IJsonOnSerializing
{
    /// <summary>The interface, when it is saved.</summary>
    [JsonPropertyName("interfaceid")]
    public ulong? InterfaceId { get; set; }

    /// <summary>Whether the server connects to the address as an IP address rather than a DNS name.</summary>
    [JsonPropertyName("useip")]
    [JsonConverter(typeof(ZabbixFlagConverter))]
    public bool? UseIP { get; set; }

    /// <summary>The IP address or the DNS name, as <see cref="UseIP"/> says.</summary>
    [JsonPropertyName("address")]
    public string? Address { get; set; }

    /// <summary>The port, or a macro that holds it.</summary>
    [JsonPropertyName("port")]
    public ZabbixPort? Port { get; set; }

    /// <summary>The interface's availability, as the API's number for it.</summary>
    [JsonPropertyName("available")]
    public int? Available { get; set; }

    /// <summary>An SNMP interface's settings.</summary>
    [JsonPropertyName("details")]
    public ZabbixItemTestSnmpDetails? Details { get; set; }

    /// <summary>
    /// Fields this record doesn't model, sent as they are beside the modelled ones: what a newer server reads, by its wire
    /// name.
    /// </summary>
    [JsonExtensionData]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "System.Text.Json fills extension data through a setter, as the generated requests do.")]
    public IDictionary<string, JsonElement>? AdditionalFields { get; set; }

    private static readonly System.Collections.Frozen.FrozenSet<string> s_modelledWires = System.Collections.Frozen.FrozenSet.Create(StringComparer.Ordinal, "interfaceid", "useip", "address", "port", "available", "details");

    void IJsonOnSerializing.OnSerializing() => ZabbixExtensionDataGuard.Refuse(AdditionalFields, nameof(ZabbixItemTestInterface), s_modelledWires);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Member(builder, ref more, nameof(InterfaceId), InterfaceId);
        ZabbixRecordText.Member(builder, ref more, nameof(UseIP), UseIP);
        ZabbixRecordText.Member(builder, ref more, nameof(Address), Address);
        ZabbixRecordText.Member(builder, ref more, nameof(Port), Port);
        ZabbixRecordText.Member(builder, ref more, nameof(Available), Available);
        ZabbixRecordText.Member(builder, ref more, nameof(Details), Details);
        ZabbixRecordText.Count(builder, ref more, nameof(AdditionalFields), AdditionalFields);
        return more;
    }

    /// <summary>Whether <paramref name="other"/> holds the same values, its lists and fields compared by value.</summary>
    /// <param name="other">The other record.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixItemTestInterface? other)
        => other is not null
            && Equals(InterfaceId, other.InterfaceId)
            && Equals(UseIP, other.UseIP)
            && string.Equals(Address, other.Address, StringComparison.Ordinal)
            && Equals(Port, other.Port)
            && Equals(Available, other.Available)
            && Equals(Details, other.Details)
            && ZabbixValueEquality.DictionaryEqual(AdditionalFields, other.AdditionalFields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(InterfaceId);
        hash.Add(UseIP);
        hash.Add(Address, StringComparer.Ordinal);
        hash.Add(Port);
        hash.Add(Available);
        hash.Add(Details);
        ZabbixValueEquality.AddDictionary(ref hash, AdditionalFields);
        return hash.ToHashCode();
    }
}
