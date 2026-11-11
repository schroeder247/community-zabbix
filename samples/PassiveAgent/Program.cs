// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using Community.Zabbix;

// Listens on port 10050 and answers the server's polls. Only the addresses you name may poll it.
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, press) =>
{
    press.Cancel = true;
    stop.Cancel();
};

ZabbixItemKey<string> version = new("app.version");

await using ZabbixPassiveAgent agent = ZabbixPassiveAgent.CreateBuilder("web-01")
    .AllowPeer(IPAddress.Loopback)
    .AllowPeer(IPNetwork.Parse("10.0.0.0/24"))
    .AddItem(version, () => "2.4.1")
    .Build();

await agent.RunAsync(stop.Token);
