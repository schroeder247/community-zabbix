// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>Represents one preprocessing step of an item under test, as <c>item.test</c> reads it.</summary>
public sealed record ZabbixItemTestStep : IJsonOnSerializing
{
    /// <summary>The step's type, the API's <c>ZabbixPreprocessingType</c> as its number.</summary>
    [JsonPropertyName("type")]
    public int? StepType { get; set; }

    /// <summary>The step's parameters, as Zabbix spells them: one per line.</summary>
    [JsonPropertyName("params")]
    public string? Parameters { get; set; }

    /// <summary>
    /// What a failure of the step does, the API's <c>ZabbixPreprocessingErrorHandler</c> as its number.
    /// </summary>
    [JsonPropertyName("error_handler")]
    public int? ErrorHandler { get; set; }

    /// <summary>The value or the error message the error handler sets.</summary>
    [JsonPropertyName("error_handler_params")]
    public string? ErrorHandlerParameters { get; set; }

    /// <summary>
    /// Fields this record doesn't model, sent as they are beside the modelled ones: what a newer server reads, by its wire
    /// name.
    /// </summary>
    [JsonExtensionData]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "System.Text.Json fills extension data through a setter, as the generated requests do.")]
    public IDictionary<string, JsonElement>? AdditionalFields { get; set; }

    private static readonly System.Collections.Frozen.FrozenSet<string> s_modelledWires = System.Collections.Frozen.FrozenSet.Create(StringComparer.Ordinal, "type", "params", "error_handler", "error_handler_params");

    void IJsonOnSerializing.OnSerializing() => ZabbixExtensionDataGuard.Refuse(AdditionalFields, nameof(ZabbixItemTestStep), s_modelledWires);

    [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0051", Justification = "The record's synthesized ToString calls it; the analyser does not see the call.")]
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        bool more = false;
        ZabbixRecordText.Member(builder, ref more, nameof(StepType), StepType);
        ZabbixRecordText.Member(builder, ref more, nameof(Parameters), Parameters);
        ZabbixRecordText.Member(builder, ref more, nameof(ErrorHandler), ErrorHandler);
        ZabbixRecordText.Member(builder, ref more, nameof(ErrorHandlerParameters), ErrorHandlerParameters);
        ZabbixRecordText.Count(builder, ref more, nameof(AdditionalFields), AdditionalFields);
        return more;
    }

    /// <summary>Whether <paramref name="other"/> holds the same values, its lists and fields compared by value.</summary>
    /// <param name="other">The other record.</param>
    /// <returns><see langword="true"/> when every member holds the same value; otherwise <see langword="false"/>.</returns>
    public bool Equals(ZabbixItemTestStep? other)
        => other is not null
            && Equals(StepType, other.StepType)
            && string.Equals(Parameters, other.Parameters, StringComparison.Ordinal)
            && Equals(ErrorHandler, other.ErrorHandler)
            && string.Equals(ErrorHandlerParameters, other.ErrorHandlerParameters, StringComparison.Ordinal)
            && ZabbixValueEquality.DictionaryEqual(AdditionalFields, other.AdditionalFields);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(StepType);
        hash.Add(Parameters, StringComparer.Ordinal);
        hash.Add(ErrorHandler);
        hash.Add(ErrorHandlerParameters, StringComparer.Ordinal);
        ZabbixValueEquality.AddDictionary(ref hash, AdditionalFields);
        return hash.ToHashCode();
    }
}
