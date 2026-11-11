// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix;

// Lists every host through the JSON-RPC API, then the latest values of the first one.
string endpoint = Environment.GetEnvironmentVariable("ZABBIX_URL") ?? "https://zabbix.example/";
string token = Environment.GetEnvironmentVariable("ZABBIX_API_TOKEN")
    ?? throw new InvalidOperationException("Set ZABBIX_API_TOKEN to an API token.");

await using var zabbix = new ZabbixClient(endpoint, token);
Console.WriteLine($"Zabbix API {await zabbix.GetApiVersionAsync()}");

var hosts = await zabbix.Hosts.GetAsync(q => q.Output = [ZabbixHostFields.HostId, ZabbixHostFields.HostName, ZabbixHostFields.VisibleName]);
foreach (ZabbixHost host in hosts)
    Console.WriteLine(host.VisibleName);

if (hosts is [{ HostName: { } first }, ..])
{
    await foreach (ZabbixValue value in zabbix.Hosts[first].GetLatestAsync())
        Console.WriteLine($"{value.Key} = {value.Value}");
}
