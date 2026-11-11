// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Represents how an <c>item.test</c> request's preprocessing test runs: what it assumes of the item, and the previous
/// value it compares with (the request's <c>options</c>).
/// </summary>
public sealed record ZabbixItemTestConditions : IJsonOnSerializing
{
    /// <summary>
    /// Whether the test leaves the steps' final value as it is, rather than checking that it converts to the item's
    /// value type; the frontend's Test sets it when it doesn't show the final result.
    /// </summary>
    [JsonPropertyName("single")]
    public bool? SkipValueTypeCheck { get; set; }

    /// <summary>
    /// The item's state the test assumes, as the API's number for it: 1 tests the steps that handle a not supported
    /// item.
    /// </summary>
    [JsonPropertyName("state")]
    public int? State { get; set; }

    /// <summary>The error a not supported item's test starts from.</summary>
    [JsonPropertyName("runtime_error")]
    public string? RuntimeError { get; set; }

    /// <summary>The previous value the steps that compare with it read.</summary>
    [JsonPropertyName("history")]
    public ZabbixItemTestPreviousValue? PreviousValue { get; set; }

    /// <summary>
    /// Fields this record doesn't model, sent as they are beside the modelled ones: what a newer server reads, by its wire
    /// name.
    /// </summary>
    [JsonExtensionData]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "System.Text.Json fills extension data through a setter, as the generated requests do.")]
    public IDictionary<string, JsonElement>? AdditionalFields { get; set; }

    private static readonly System.Collections.Frozen.FrozenSet<string> s_modelledWires = System.Collections.Frozen.FrozenSet.Create(StringComparer.Ordinal, "single", "state", "runtime_error", "history");

    void IJsonOnSerializing.OnSerializing() => ZabbixExtensionDataGuard.Refuse(AdditionalFields, nameof(ZabbixItemTestConditions), s_modelledWires);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Member(builder, ref more, nameof(SkipValueTypeCheck), SkipValueTypeCheck);
        ZabbixRecordText.Member(builder, ref more, nameof(State), State);
        ZabbixRecordText.Member(builder, ref more, nameof(RuntimeError), RuntimeError);
        ZabbixRecordText.Member(builder, ref more, nameof(PreviousValue), PreviousValue);
        ZabbixRecordText.Count(builder, ref more, nameof(AdditionalFields), AdditionalFields);
        return more;
    }

    /// <summary>Whether <paramref name="other"/> holds the same values, its lists and fields compared by value.</summary>
    /// <param name="other">The other record.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixItemTestConditions? other)
        => other is not null
            && Equals(SkipValueTypeCheck, other.SkipValueTypeCheck)
            && Equals(State, other.State)
            && string.Equals(RuntimeError, other.RuntimeError, StringComparison.Ordinal)
            && Equals(PreviousValue, other.PreviousValue)
            && ZabbixValueEquality.DictionaryEqual(AdditionalFields, other.AdditionalFields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(SkipValueTypeCheck);
        hash.Add(State);
        hash.Add(RuntimeError, StringComparer.Ordinal);
        hash.Add(PreviousValue);
        ZabbixValueEquality.AddDictionary(ref hash, AdditionalFields);
        return hash.ToHashCode();
    }
}
