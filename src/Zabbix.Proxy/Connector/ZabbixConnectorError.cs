// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Connector;

/// <summary>The body the server reads when it is not answered 2xx: its <c>error</c> tag goes to its log (<c>connector_worker.c</c>).</summary>
/// <param name="Error">What went wrong.</param>
public sealed record ZabbixConnectorError([property: JsonPropertyName("error")] string Error);
