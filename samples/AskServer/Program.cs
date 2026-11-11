// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Community.Zabbix;

// Asks a server for its status and queue on its trapper port, as the web frontend does.
string server = Environment.GetEnvironmentVariable("ZABBIX_SERVER") ?? "zbx-server-01";
string token = Environment.GetEnvironmentVariable("ZABBIX_API_TOKEN")
    ?? throw new InvalidOperationException("Set ZABBIX_API_TOKEN to a super admin's API token.");

using ZabbixWireClient wire = ZabbixWireClient.CreateBuilder()
    .AddServer(server)
    .WithToken(token)
    .Build();
await wire.PingAsync();

JsonElement status = await wire.GetStatusAsync();
Console.WriteLine(status);

foreach (ZabbixQueueDelays row in await wire.GetQueueAsync(ZabbixQueueOverview.ByItemType))
    Console.WriteLine($"item type {row.ItemType}: {row.Delay5} overdue 5-10 s, {row.Delay600} over 10 min");
