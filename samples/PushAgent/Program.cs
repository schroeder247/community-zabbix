// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix;

// Samples a value every 30 seconds and sends it to a trapper item, keeping it until the server has it.
string server = Environment.GetEnvironmentVariable("ZABBIX_SERVER") ?? "zbx-server-01";

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, press) =>
{
    press.Cancel = true;
    stop.Cancel();
};

ZabbixItemKey<long> memory = new("app.memory");

await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("web-01")
    .AddServer(server)
    .AddPoll(memory, TimeSpan.FromSeconds(30), () => GC.GetTotalMemory(false))
    .Build();

await agent.RunAsync(stop.Token);
