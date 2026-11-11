// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix;

/// <summary>
/// Provides the System.Text.Json contract the SDK reads and writes the API with, so that an application can serialize
/// and deserialize the API's records exactly as the SDK sends and receives them: ids as strings, enums as their numbers,
/// absent members omitted.
/// </summary>
/// <remarks>
/// The contract is source-generated. Its context type is internal: it has a public member for each of the API's
/// records and lists, and those members are the generator's, not an API surface. Combine
/// <see cref="TypeInfoResolver"/> with another resolver through <see cref="JsonTypeInfoResolver.Combine(IJsonTypeInfoResolver?[])"/>
/// when a document holds the SDK's records beside types of its own.
/// </remarks>
public static class ZabbixJson
{
    /// <summary>Gets the resolver that knows every record, request and parameter set of the API model.</summary>
    /// <value>The source-generated resolver.</value>
    public static IJsonTypeInfoResolver TypeInfoResolver => ZabbixApiJson.Default;

    /// <summary>Gets the read-only options the SDK serializes the API with.</summary>
    /// <value>Options whose resolver is <see cref="TypeInfoResolver"/>; they cannot be changed.</value>
    /// <example><c>JsonSerializer.Serialize(host, ZabbixJson.Options)</c> writes a <see cref="ZabbixHost"/> as <c>host.create</c> takes it.</example>
    public static JsonSerializerOptions Options => ZabbixApiJson.Default.Options;
}
