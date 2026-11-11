// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents the host an item is tested on: its interface, the proxy that collects its values, and what the server
/// needs to reach it.
/// </summary>
public sealed record ZabbixItemTestHost : IJsonOnSerializing
{
    /// <summary>The host, when it is saved.</summary>
    [JsonPropertyName("hostid")]
    public ulong? HostId { get; set; }

    /// <summary>The host's technical name; a calculated item's formula reads it.</summary>
    [JsonPropertyName("host")]
    public string? HostName { get; set; }

    /// <summary>The proxy that collects the value; 0 or absent for the server itself.</summary>
    [JsonPropertyName("proxyid")]
    public ulong? ProxyId { get; set; }

    /// <summary>The interface the server connects to.</summary>
    [JsonPropertyName("interface")]
    public ZabbixItemTestInterface? Interface { get; set; }

    /// <summary>Whether the host is in maintenance, as the API's number for it.</summary>
    [JsonPropertyName("maintenance_status")]
    public int? MaintenanceStatus { get; set; }

    /// <summary>Whether the host's maintenance collects data, as the API's number for it.</summary>
    [JsonPropertyName("maintenance_type")]
    public int? MaintenanceType { get; set; }

    /// <summary>The host's IPMI authentication algorithm, as the API's number for it.</summary>
    [JsonPropertyName("ipmi_authtype")]
    public int? IpmiAuthenticationType { get; set; }

    /// <summary>The host's IPMI privilege level, as the API's number for it.</summary>
    [JsonPropertyName("ipmi_privilege")]
    public int? IpmiPrivilege { get; set; }

    /// <summary>The host's IPMI user name.</summary>
    [JsonPropertyName("ipmi_username")]
    public string? IpmiUserName { get; set; }

    /// <summary>The host's IPMI password.</summary>
    [JsonPropertyName("ipmi_password")]
    [ZabbixSecret]
    public string? IpmiPassword { get; set; }

    /// <summary>How the server connects to the host, as the API's number for it.</summary>
    [JsonPropertyName("tls_connect")]
    public int? TlsConnect { get; set; }

    /// <summary>The issuer the host's certificate must have.</summary>
    [JsonPropertyName("tls_issuer")]
    public string? TlsIssuer { get; set; }

    /// <summary>The subject the host's certificate must have.</summary>
    [JsonPropertyName("tls_subject")]
    public string? TlsSubject { get; set; }

    /// <summary>The host's pre-shared key identity.</summary>
    [JsonPropertyName("tls_psk_identity")]
    [ZabbixSecret]
    public string? TlsPskIdentity { get; set; }

    /// <summary>The host's pre-shared key, as hex text.</summary>
    [JsonPropertyName("tls_psk")]
    [ZabbixSecret]
    public string? TlsPsk { get; set; }

    /// <summary>
    /// Fields this record doesn't model, sent as they are beside the modelled ones: what a newer server reads, by its wire
    /// name.
    /// </summary>
    [JsonExtensionData]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "System.Text.Json fills extension data through a setter, as the generated requests do.")]
    public IDictionary<string, JsonElement>? AdditionalFields { get; set; }

    private static readonly System.Collections.Frozen.FrozenSet<string> s_modelledWires = System.Collections.Frozen.FrozenSet.Create(StringComparer.Ordinal, "hostid", "host", "proxyid", "interface", "maintenance_status", "maintenance_type", "ipmi_authtype", "ipmi_privilege", "ipmi_username", "ipmi_password", "tls_connect", "tls_issuer", "tls_subject", "tls_psk_identity", "tls_psk");

    void IJsonOnSerializing.OnSerializing() => ZabbixExtensionDataGuard.Refuse(AdditionalFields, nameof(ZabbixItemTestHost), s_modelledWires);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Member(builder, ref more, nameof(HostId), HostId);
        ZabbixRecordText.Member(builder, ref more, nameof(HostName), HostName);
        ZabbixRecordText.Member(builder, ref more, nameof(ProxyId), ProxyId);
        ZabbixRecordText.Member(builder, ref more, nameof(Interface), Interface);
        ZabbixRecordText.Member(builder, ref more, nameof(MaintenanceStatus), MaintenanceStatus);
        ZabbixRecordText.Member(builder, ref more, nameof(MaintenanceType), MaintenanceType);
        ZabbixRecordText.Member(builder, ref more, nameof(IpmiAuthenticationType), IpmiAuthenticationType);
        ZabbixRecordText.Member(builder, ref more, nameof(IpmiPrivilege), IpmiPrivilege);
        ZabbixRecordText.Member(builder, ref more, nameof(IpmiUserName), IpmiUserName);
        ZabbixRecordText.Secret(builder, ref more, nameof(IpmiPassword), IpmiPassword is not null);
        ZabbixRecordText.Member(builder, ref more, nameof(TlsConnect), TlsConnect);
        ZabbixRecordText.Member(builder, ref more, nameof(TlsIssuer), TlsIssuer);
        ZabbixRecordText.Member(builder, ref more, nameof(TlsSubject), TlsSubject);
        ZabbixRecordText.Secret(builder, ref more, nameof(TlsPskIdentity), TlsPskIdentity is not null);
        ZabbixRecordText.Secret(builder, ref more, nameof(TlsPsk), TlsPsk is not null);
        ZabbixRecordText.Count(builder, ref more, nameof(AdditionalFields), AdditionalFields);
        return more;
    }

    /// <summary>Whether <paramref name="other"/> holds the same values, its lists and fields compared by value.</summary>
    /// <param name="other">The other record.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixItemTestHost? other)
        => other is not null
            && Equals(HostId, other.HostId)
            && string.Equals(HostName, other.HostName, StringComparison.Ordinal)
            && Equals(ProxyId, other.ProxyId)
            && Equals(Interface, other.Interface)
            && Equals(MaintenanceStatus, other.MaintenanceStatus)
            && Equals(MaintenanceType, other.MaintenanceType)
            && Equals(IpmiAuthenticationType, other.IpmiAuthenticationType)
            && Equals(IpmiPrivilege, other.IpmiPrivilege)
            && string.Equals(IpmiUserName, other.IpmiUserName, StringComparison.Ordinal)
            && string.Equals(IpmiPassword, other.IpmiPassword, StringComparison.Ordinal)
            && Equals(TlsConnect, other.TlsConnect)
            && string.Equals(TlsIssuer, other.TlsIssuer, StringComparison.Ordinal)
            && string.Equals(TlsSubject, other.TlsSubject, StringComparison.Ordinal)
            && string.Equals(TlsPskIdentity, other.TlsPskIdentity, StringComparison.Ordinal)
            && string.Equals(TlsPsk, other.TlsPsk, StringComparison.Ordinal)
            && ZabbixValueEquality.DictionaryEqual(AdditionalFields, other.AdditionalFields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(HostId);
        hash.Add(HostName, StringComparer.Ordinal);
        hash.Add(ProxyId);
        hash.Add(Interface);
        hash.Add(MaintenanceStatus);
        hash.Add(MaintenanceType);
        hash.Add(IpmiAuthenticationType);
        hash.Add(IpmiPrivilege);
        hash.Add(IpmiUserName, StringComparer.Ordinal);
        hash.Add(IpmiPassword, StringComparer.Ordinal);
        hash.Add(TlsConnect);
        hash.Add(TlsIssuer, StringComparer.Ordinal);
        hash.Add(TlsSubject, StringComparer.Ordinal);
        hash.Add(TlsPskIdentity, StringComparer.Ordinal);
        hash.Add(TlsPsk, StringComparer.Ordinal);
        ZabbixValueEquality.AddDictionary(ref hash, AdditionalFields);
        return hash.ToHashCode();
    }
}
