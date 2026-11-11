// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>Represents the SNMP settings of the interface an SNMP item is tested on.</summary>
public sealed record ZabbixItemTestSnmpDetails : IJsonOnSerializing
{
    /// <summary>The SNMP version: 1, 2 for SNMPv2c, or 3.</summary>
    [JsonPropertyName("version")]
    public int? Version { get; set; }

    /// <summary>The SNMPv1 or SNMPv2c community.</summary>
    [JsonPropertyName("community")]
    public string? Community { get; set; }

    /// <summary>How many values an SNMP bulk request asks for.</summary>
    [JsonPropertyName("max_repetitions")]
    public int? MaxRepetitions { get; set; }

    /// <summary>The SNMPv3 security name.</summary>
    [JsonPropertyName("securityname")]
    public string? SecurityName { get; set; }

    /// <summary>The SNMPv3 security level, as the API's number for it.</summary>
    [JsonPropertyName("securitylevel")]
    public int? SecurityLevel { get; set; }

    /// <summary>The SNMPv3 authentication protocol, as the API's number for it.</summary>
    [JsonPropertyName("authprotocol")]
    public int? AuthenticationProtocol { get; set; }

    /// <summary>The SNMPv3 authentication passphrase.</summary>
    [JsonPropertyName("authpassphrase")]
    [ZabbixSecret]
    public string? AuthenticationPassphrase { get; set; }

    /// <summary>The SNMPv3 privacy protocol, as the API's number for it.</summary>
    [JsonPropertyName("privprotocol")]
    public int? PrivacyProtocol { get; set; }

    /// <summary>The SNMPv3 privacy passphrase.</summary>
    [JsonPropertyName("privpassphrase")]
    [ZabbixSecret]
    public string? PrivacyPassphrase { get; set; }

    /// <summary>The SNMPv3 context name.</summary>
    [JsonPropertyName("contextname")]
    public string? ContextName { get; set; }

    /// <summary>
    /// Fields this record doesn't model, sent as they are beside the modelled ones: what a newer server reads, by its wire
    /// name.
    /// </summary>
    [JsonExtensionData]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "System.Text.Json fills extension data through a setter, as the generated requests do.")]
    public IDictionary<string, JsonElement>? AdditionalFields { get; set; }

    private static readonly System.Collections.Frozen.FrozenSet<string> s_modelledWires = System.Collections.Frozen.FrozenSet.Create(StringComparer.Ordinal, "version", "community", "max_repetitions", "securityname", "securitylevel", "authprotocol", "authpassphrase", "privprotocol", "privpassphrase", "contextname");

    void IJsonOnSerializing.OnSerializing() => ZabbixExtensionDataGuard.Refuse(AdditionalFields, nameof(ZabbixItemTestSnmpDetails), s_modelledWires);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Member(builder, ref more, nameof(Version), Version);
        ZabbixRecordText.Member(builder, ref more, nameof(Community), Community);
        ZabbixRecordText.Member(builder, ref more, nameof(MaxRepetitions), MaxRepetitions);
        ZabbixRecordText.Member(builder, ref more, nameof(SecurityName), SecurityName);
        ZabbixRecordText.Member(builder, ref more, nameof(SecurityLevel), SecurityLevel);
        ZabbixRecordText.Member(builder, ref more, nameof(AuthenticationProtocol), AuthenticationProtocol);
        ZabbixRecordText.Secret(builder, ref more, nameof(AuthenticationPassphrase), AuthenticationPassphrase is not null);
        ZabbixRecordText.Member(builder, ref more, nameof(PrivacyProtocol), PrivacyProtocol);
        ZabbixRecordText.Secret(builder, ref more, nameof(PrivacyPassphrase), PrivacyPassphrase is not null);
        ZabbixRecordText.Member(builder, ref more, nameof(ContextName), ContextName);
        ZabbixRecordText.Count(builder, ref more, nameof(AdditionalFields), AdditionalFields);
        return more;
    }

    /// <summary>Whether <paramref name="other"/> holds the same values, its lists and fields compared by value.</summary>
    /// <param name="other">The other record.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixItemTestSnmpDetails? other)
        => other is not null
            && Equals(Version, other.Version)
            && string.Equals(Community, other.Community, StringComparison.Ordinal)
            && Equals(MaxRepetitions, other.MaxRepetitions)
            && string.Equals(SecurityName, other.SecurityName, StringComparison.Ordinal)
            && Equals(SecurityLevel, other.SecurityLevel)
            && Equals(AuthenticationProtocol, other.AuthenticationProtocol)
            && string.Equals(AuthenticationPassphrase, other.AuthenticationPassphrase, StringComparison.Ordinal)
            && Equals(PrivacyProtocol, other.PrivacyProtocol)
            && string.Equals(PrivacyPassphrase, other.PrivacyPassphrase, StringComparison.Ordinal)
            && string.Equals(ContextName, other.ContextName, StringComparison.Ordinal)
            && ZabbixValueEquality.DictionaryEqual(AdditionalFields, other.AdditionalFields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Version);
        hash.Add(Community, StringComparer.Ordinal);
        hash.Add(MaxRepetitions);
        hash.Add(SecurityName, StringComparer.Ordinal);
        hash.Add(SecurityLevel);
        hash.Add(AuthenticationProtocol);
        hash.Add(AuthenticationPassphrase, StringComparer.Ordinal);
        hash.Add(PrivacyProtocol);
        hash.Add(PrivacyPassphrase, StringComparer.Ordinal);
        hash.Add(ContextName, StringComparer.Ordinal);
        ZabbixValueEquality.AddDictionary(ref hash, AdditionalFields);
        return hash.ToHashCode();
    }
}
