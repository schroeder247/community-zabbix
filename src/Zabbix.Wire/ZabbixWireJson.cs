// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix;

/// <summary>
/// Provides the System.Text.Json contract the wire client reads and writes a server's requests and replies with, so
/// that an application can serialize and deserialize the wire's records exactly as the client sends and receives them:
/// <see cref="ZabbixItemTestRequest"/>, <see cref="ZabbixServerStatistics"/> and the others the client's methods take and
/// return.
/// </summary>
/// <remarks>
/// The contract is source-generated, and its context type is internal. Combine <see cref="TypeInfoResolver"/> with
/// another resolver through <see cref="JsonTypeInfoResolver.Combine(IJsonTypeInfoResolver?[])"/> when a document holds
/// the wire's records beside types of its own.
/// </remarks>
public static class ZabbixWireJson
{
    /// <summary>Gets the resolver that knows the records the wire client's requests take and its replies return.</summary>
    /// <value>The source-generated resolver.</value>
    public static IJsonTypeInfoResolver TypeInfoResolver => ZabbixServerWireJson.Default;

    /// <summary>Gets the read-only options the wire client serializes a server's requests with.</summary>
    /// <value>Options whose resolver is <see cref="TypeInfoResolver"/>; they cannot be changed.</value>
    /// <example>
    /// <c>JsonSerializer.Deserialize(text, (JsonTypeInfo&lt;ZabbixItemTestRequest&gt;)ZabbixWireJson.Options.GetTypeInfo(typeof(ZabbixItemTestRequest)))</c>
    /// reads an <c>item.test</c> request as the frontend sends it.
    /// </example>
    public static JsonSerializerOptions Options => ZabbixServerWireJson.Default.Options;
}
