// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents a <c>report.test</c> request: the scheduled report the server generates and sends now, to the user the
/// session belongs to (<c>zbx_report_test</c> in <c>report_protocol.c</c>).
/// </summary>
/// <remarks>
/// Zabbix.Wire doesn't reference the API's records, so the report's period is its number here:
/// <c>(int)ZabbixReportPeriod.Week</c>.
/// </remarks>
public sealed record ZabbixReportTestRequest : IJsonOnSerializing
{
    /// <summary>A request with no field set; an initializer sets its <see langword="required"/> members.</summary>
    public ZabbixReportTestRequest()
    {
    }

    /// <summary>A request for the report of <paramref name="dashboardId"/>, as <paramref name="userId"/> sees it.</summary>
    /// <param name="name">The report's name, which names its attachment.</param>
    /// <param name="dashboardId">The dashboard the report shows.</param>
    /// <param name="userId">The user whose view of the dashboard the report shows.</param>
    [SetsRequiredMembers]
    public ZabbixReportTestRequest(string name, ulong dashboardId, ulong userId)
    {
        Name = name;
        DashboardId = dashboardId;
        UserId = userId;
    }

    /// <summary>The report's name, which names its attachment.</summary>
    [JsonPropertyName("name")]
    [JsonRequired]
    public required string Name { get; set; }

    /// <summary>The dashboard the report shows.</summary>
    [JsonPropertyName("dashboardid")]
    [JsonRequired]
    public required ulong DashboardId { get; set; }

    /// <summary>The user whose view of the dashboard the report shows.</summary>
    [JsonPropertyName("userid")]
    [JsonRequired]
    public required ulong UserId { get; set; }

    /// <summary>The period the report covers, the API's <c>ZabbixReportPeriod</c> as its number: 0 a day, 1 a week, 2 a month, 3 a year.</summary>
    [JsonPropertyName("period")]
    public int Period { get; set; }

    /// <summary>When the report is generated as of: the period ends here. The client's clock's now when <see langword="null"/>.</summary>
    [JsonPropertyName("now")]
    [JsonConverter(typeof(ZabbixUnixTimeConverter))]
    public DateTimeOffset? Now { get; set; }

    /// <summary>The message's parameters, by name: <c>subject</c> and <c>body</c>.</summary>
    [JsonPropertyName("params")]
    public IReadOnlyDictionary<string, string>? Parameters { get; set; }

    /// <summary>
    /// Fields this record doesn't model, sent as they are beside the modelled ones: what a newer server reads, by its wire
    /// name.
    /// </summary>
    [JsonExtensionData]
    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "System.Text.Json fills extension data through a setter, as the generated requests do.")]
    public IDictionary<string, JsonElement>? AdditionalFields { get; set; }

    private static readonly System.Collections.Frozen.FrozenSet<string> s_modelledWires = System.Collections.Frozen.FrozenSet.Create(StringComparer.Ordinal, "name", "dashboardid", "userid", "period", "now", "params");

    void IJsonOnSerializing.OnSerializing() => ZabbixExtensionDataGuard.Refuse(AdditionalFields, nameof(ZabbixReportTestRequest), s_modelledWires);

    /// <summary>Whether <paramref name="other"/> holds the same values, its parameters and fields compared by value.</summary>
    /// <param name="other">The other request.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixReportTestRequest? other)
        => other is not null
            && string.Equals(Name, other.Name, StringComparison.Ordinal)
            && DashboardId == other.DashboardId
            && UserId == other.UserId
            && Period == other.Period
            && Now == other.Now
            && ZabbixValueEquality.DictionaryEqual(Parameters, other.Parameters)
            && ZabbixValueEquality.DictionaryEqual(AdditionalFields, other.AdditionalFields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name, StringComparer.Ordinal);
        hash.Add(DashboardId);
        hash.Add(UserId);
        hash.Add(Period);
        hash.Add(Now);
        ZabbixValueEquality.AddDictionary(ref hash, Parameters);
        ZabbixValueEquality.AddDictionary(ref hash, AdditionalFields);
        return hash.ToHashCode();
    }
}
