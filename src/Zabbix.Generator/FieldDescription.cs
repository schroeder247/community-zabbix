// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.Generator;

/// <summary>
/// A record's field: its wire name, its C# name, its kind, and what the source says about it. <see cref="Refused"/>: the
/// version that removed it refuses it (its validator has no <c>API_ALLOW_UNEXPECTED</c>), so the client refuses it first;
/// <see cref="Replacement"/>: the field to send instead; <see cref="Secret"/>: a password, key or token (the audit log's
/// masked paths and their kin), classified and kept out of the record's text form; <see cref="Nullable"/>: its <c>null</c>
/// is a request of its own (sent as <c>ZabbixOptional&lt;T&gt;</c>: absent, null or a value); <see cref="PreviousName"/>:
/// the extractor's mechanical name, when <see cref="ZabbixNames"/> named the field by its words instead (a cmdlet keeps it
/// as a parameter alias); <see cref="Nanoseconds"/>: a clock's nanoseconds field on the wire (<c>ns</c> beside
/// <c>clock</c>), folded into the one member by <see cref="ZabbixClockPairs"/>.
/// </summary>
internal sealed record FieldDescription(
    string Name,
    string Wire,
    string Kind,
    bool Required,
    string? Since,
    string? Removed,
    string? Deprecated,
    int? MaxLength,
    string? Summary,
    string? Json,
    bool Refused,
    string? Replacement,
    bool Secret = false,
    bool Nullable = false,
    string? PreviousName = null,
    string? Nanoseconds = null)
{
    public static FieldDescription Read(JsonElement element) => new(
        element.GetProperty("name").GetString()!,
        element.GetProperty("wire").GetString()!,
        element.GetProperty("kind").GetString()!,
        ApiDescription.Flag(element, "required"),
        ApiDescription.Text(element, "since"),
        ApiDescription.Text(element, "removed"),
        ApiDescription.Text(element, "deprecated"),
        element.TryGetProperty("maxLength", out JsonElement length) && length.ValueKind == JsonValueKind.Number ? length.GetInt32() : null,
        ApiDescription.Text(element, "summary"),
        ApiDescription.Text(element, "json"),
        ApiDescription.Flag(element, "refused"),
        ApiDescription.Text(element, "replacement"),
        // A session id is a credential (the trapper and the API take one as a token) though the audit log masks no field
        // of that name, so the description does not flag it.
        ApiDescription.Flag(element, "secret") || element.GetProperty("wire").GetString() == "sessionid",
        ApiDescription.Flag(element, "nullable"));
}
