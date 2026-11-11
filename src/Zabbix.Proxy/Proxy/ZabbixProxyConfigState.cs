// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Proxy;

/// <summary>What a passive proxy answers the server's <c>proxy config</c> with, before the configuration arrives (<c>zbx_recv_proxyconfig</c>).</summary>
internal sealed record ZabbixProxyConfigState(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("session")] string Session,
    [property: JsonPropertyName("config_revision")] long ConfigRevision,
    [property: JsonPropertyName("proxy_secrets_provider")] int ProxySecretsProvider,
    [property: JsonPropertyName("hostmap_revision")] long? HostmapRevision);
