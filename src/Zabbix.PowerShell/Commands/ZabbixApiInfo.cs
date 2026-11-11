// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// Represents a connection's API info, <c>Get-ZbxApiInfo</c>'s output: the server's API version alongside the
/// connection's own name, endpoint and how it authenticates.
/// </summary>
/// <param name="Version">The server's API version (<c>apiinfo.version</c>).</param>
/// <param name="Connection">The connection's name in the runspace's context.</param>
/// <param name="Endpoint">The JSON-RPC endpoint.</param>
/// <param name="Credential">How the connection authenticates, e.g. <c>ZabbixApiTokenCredential</c>.</param>
public sealed record ZabbixApiInfo(Version Version, string Connection, Uri Endpoint, string Credential);
