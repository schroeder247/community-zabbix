// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix;

// Polls a group's items and prints each new value until you press Ctrl+C.
string endpoint = Environment.GetEnvironmentVariable("ZABBIX_URL") ?? "https://zabbix.example/";
string token = Environment.GetEnvironmentVariable("ZABBIX_API_TOKEN")
    ?? throw new InvalidOperationException("Set ZABBIX_API_TOKEN to an API token.");
string group = args is [{ } named, ..] ? named : "Linux servers";

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, press) =>
{
    press.Cancel = true;
    stop.Cancel();
};

await using var zabbix = new ZabbixClient(endpoint, token);
await using IZabbixWatcher watcher = zabbix.HostGroups[group].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(30)));
try
{
    await foreach (ZabbixValue value in watcher.WithCancellation(stop.Token))
        Console.WriteLine(value);
}
catch (OperationCanceledException) when (stop.IsCancellationRequested)
{
    Console.WriteLine($"{watcher.SucceededPolls} polls, {watcher.FailedPolls} failed");
}
