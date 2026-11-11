// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix;

/// <summary>
/// A record the server answered with, and the fields of that answer its description does not model (a newer Zabbix, or a
/// field the extraction could not type): kept verbatim, never dropped. Every generated read record implements it.
/// </summary>
/// <remarks>
/// An interface member, implemented explicitly, not a public property of the record: every serializer that reflects a
/// record's public properties (PowerShell's <c>ConvertTo-Json</c>, which honours only Newtonsoft's own ignore attribute)
/// would otherwise write the bag's <see cref="JsonElement"/> plumbing beside the
/// modelled fields. The SDK's own JSON contract still fills and writes it (<c>[JsonExtensionData]</c> on an internal
/// member), and the PowerShell module surfaces each key as a property of its own. Read it as
/// <c>((IZabbixAdditionalFields)host).AdditionalFields</c>.
/// </remarks>
public interface IZabbixAdditionalFields
{
    /// <summary>The unmodelled fields by wire name; <see langword="null"/> when the reply carried none.</summary>
    IDictionary<string, JsonElement>? AdditionalFields { get; }
}
