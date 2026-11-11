// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Community.Zabbix;

/// <summary>
/// Provides the <c>AdditionalFields</c> guard that every generated top-level request record (every <c>*Request</c> a
/// method takes, and every <c>*GetParameters</c>; not a nested request) calls from
/// <c>IJsonOnSerializing.OnSerializing</c>. System.Text.Json writes <see cref="JsonExtensionDataAttribute"/> members
/// after the modelled properties without checking for a name collision (a bag key equal to a modelled field's wire name
/// serializes as a duplicate JSON member, e.g. <c>{"host":"h","host":"dup"}</c>, and which one a reader keeps is the
/// reader's choice). Refusing the collision here, at serialization time, keeps the ambiguity
/// off the wire instead of shipping it.
/// </summary>
internal static class ZabbixExtensionDataGuard
{
    /// <summary>
    /// The options of a <c>.get</c> that change the shape of its reply: a count instead of the records, rows of counts, or
    /// the records keyed by id. A get's parameters leave them out, because the typed reply couldn't read it (ADR 0013,
    /// section 4), and its bag refuses them; <c>CountAsync</c> counts.
    /// </summary>
    public static IReadOnlyCollection<string> ReplyShapes { get; } = ["countOutput", "groupCount", "groupBy", "preservekeys"];

    /// <summary>
    /// Throws when <paramref name="additionalFields"/> holds a key <paramref name="wireFields"/> already names, or one of
    /// <paramref name="replyShapes"/>.
    /// </summary>
    /// <param name="additionalFields">The record's <c>AdditionalFields</c> bag, or <see langword="null"/>.</param>
    /// <param name="typeName">The record's name, for the message.</param>
    /// <param name="wireFields">Every wire field the record already models.</param>
    /// <param name="replyShapes">The options the record leaves out because they change the shape of the reply: a get's <see cref="ReplyShapes"/>.</param>
    /// <exception cref="ArgumentException">A key in <paramref name="additionalFields"/> names a field in <paramref name="wireFields"/> or <paramref name="replyShapes"/>.</exception>
    public static void Refuse(IDictionary<string, JsonElement>? additionalFields, string typeName, IReadOnlyCollection<string> wireFields, IReadOnlyCollection<string>? replyShapes = null)
    {
        if (additionalFields is null || additionalFields.Count == 0) return;
        foreach (string key in additionalFields.Keys)
        {
            if (replyShapes is not null && replyShapes.Contains(key, StringComparer.Ordinal))
                throw new ArgumentException(
                    string.Create(CultureInfo.InvariantCulture, $"'{key}' changes the shape of the reply, which the get reads as its records: AdditionalFields of {typeName} cannot carry it. CountAsync counts."),
                    nameof(additionalFields));
            if (!wireFields.Contains(key, StringComparer.Ordinal)) continue;
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"'{key}' is already a field of {typeName}: AdditionalFields cannot duplicate a modelled field on the wire."),
                nameof(additionalFields));
        }
    }
}
