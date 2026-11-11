// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization.Metadata;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// The SDK's JSON contract for one type, through its public face, <see cref="ZabbixJson.Options"/>: the contract the
/// generated clients send and read with, so a record the module writes is in the form the wire carries, as an
/// application's <c>JsonSerializer.Serialize(record, ZabbixJson.Options)</c> is. The generated commands and the binders
/// look their contracts up here.
/// </summary>
internal static class ZabbixContracts
{
    /// <summary>The contract of <typeparamref name="T"/>, which the SDK's JSON contract knows.</summary>
    /// <typeparam name="T">A record, request, parameter set or list of the API model.</typeparam>
    /// <returns>The contract.</returns>
    /// <exception cref="NotSupportedException"><typeparamref name="T"/> is not part of the API model.</exception>
    public static JsonTypeInfo<T> Of<T>() => (JsonTypeInfo<T>)ZabbixJson.Options.GetTypeInfo(typeof(T));

    /// <summary>The contract of <paramref name="type"/>, or <see langword="null"/> when it is not part of the API model.</summary>
    /// <param name="type">The type.</param>
    /// <param name="contract">The contract when found.</param>
    /// <returns>Whether the SDK's JSON contract knows <paramref name="type"/>.</returns>
    public static bool TryFind(Type type, [NotNullWhen(true)] out JsonTypeInfo? contract) => ZabbixJson.Options.TryGetTypeInfo(type, out contract);
}
