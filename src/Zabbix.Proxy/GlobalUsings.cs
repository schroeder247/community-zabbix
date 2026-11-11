// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
// What each callback is, by a name that reads better than its Func in this project's own code. The public surface is the Func.
global using ZabbixConversationCallback = System.Func<Community.Zabbix.Listening.ZabbixConversationContext, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask>;
global using ZabbixExchangeCallback = System.Func<System.ReadOnlyMemory<byte>, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<System.ReadOnlyMemory<byte>>>;
global using ZabbixPassiveProxyConfigCallback = System.Func<Community.Zabbix.Proxy.ZabbixProxyConfig, Community.Zabbix.Proxy.ZabbixProxyConfig, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<Community.Zabbix.Proxy.ZabbixProxyConfigVerdict>>;
global using ZabbixPeerResolverCallback = System.Func<string, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<System.Collections.Generic.IReadOnlyList<System.Net.IPAddress>>>;
global using ZabbixProxyDataCallback = System.Func<Community.Zabbix.Proxy.ZabbixProxyConfig, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<Community.Zabbix.Proxy.ZabbixProxyDataPage?>>;
global using ZabbixProxyStatsCallback = System.Func<System.ReadOnlyMemory<byte>, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<System.Collections.Generic.IReadOnlyDictionary<string, System.Text.Json.JsonElement>?>>;
global using ZabbixProxyTaskCallback = System.Func<Community.Zabbix.Proxy.ZabbixProxyTask, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<Community.Zabbix.Proxy.ZabbixProxyTaskResult>>;
global using ZabbixRequestCallback = System.Func<Community.Zabbix.Listening.ZabbixRequest, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<System.ReadOnlyMemory<byte>>>;
