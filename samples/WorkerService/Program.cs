// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix;

// The same pieces in an application with a host: named API clients and a hosted agent, bound from configuration.
ZabbixItemKey<string> version = new("app.version");

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddZabbixClients(builder.Configuration);
builder.Services.AddZabbixAgents(builder.Configuration);
builder.Services.AddZabbixActiveAgent("web-01")          // the agent the configuration declares: its items and its health check
    .AddItem(version, () => "2.4.1")
    .AddHealthCheck();
builder.Services.AddHostedService<Inventory>();

await builder.Build().RunAsync();

internal sealed partial class Inventory(IZabbixClientFactory clients, ILogger<Inventory> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IReadOnlyList<ZabbixHost> hosts = await clients.Create("prod").Hosts.GetAsync(stoppingToken);
        LogHosts(logger, hosts.Count);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "{Count} hosts")]
    private static partial void LogHosts(ILogger logger, int count);
}
