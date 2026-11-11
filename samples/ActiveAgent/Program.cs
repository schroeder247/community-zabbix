// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix;

// Asks the server which active checks a host has, and answers them. Your keys sit beside Zabbix's own.
string server = Environment.GetEnvironmentVariable("ZABBIX_SERVER") ?? "zbx-server-01";

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, press) =>
{
    press.Cancel = true;
    stop.Cancel();
};

ZabbixItemKey<string> version = new("app.version");
ZabbixItemKey<long> diskFree = new("app.disk.free");

await using ZabbixActiveAgent agent = ZabbixActiveAgent.CreateBuilder("web-01")
    .AddServer(server)
    .AddItem(version, () => "2.4.1")
    .AddItem(diskFree, key => new DriveInfo(key[0]).AvailableFreeSpace)
    .Build();

await agent.RunAsync(stop.Token);
