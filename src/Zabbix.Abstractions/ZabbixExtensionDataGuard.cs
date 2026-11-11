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
    /// <summary>Throws when <paramref name="additionalFields"/> holds a key <paramref name="wireFields"/> already names.</summary>
    /// <param name="additionalFields">The record's <c>AdditionalFields</c> bag, or <see langword="null"/>.</param>
    /// <param name="typeName">The record's name, for the message.</param>
    /// <param name="wireFields">Every wire field the record already models.</param>
    /// <exception cref="ArgumentException">A key in <paramref name="additionalFields"/> names a field in <paramref name="wireFields"/>.</exception>
    public static void Refuse(IDictionary<string, JsonElement>? additionalFields, string typeName, IReadOnlyCollection<string> wireFields)
    {
        if (additionalFields is null || additionalFields.Count == 0) return;
        foreach (string key in additionalFields.Keys)
        {
            if (!wireFields.Contains(key, StringComparer.Ordinal)) continue;
            throw new ArgumentException(
                string.Create(CultureInfo.InvariantCulture, $"'{key}' is already a field of {typeName}: AdditionalFields cannot duplicate a modelled field on the wire."),
                nameof(additionalFields));
        }
    }
}
