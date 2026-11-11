// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix;

// The program docs/GETTING-STARTED.md builds step by step. It creates and deletes objects, so run it against a lab server.
// args: <frontend address> <token file> <trapper address> [name prefix]
var endpoint = new Uri(args[0]);                               // https://zabbix.example/
string token = (await File.ReadAllTextAsync(args[1])).Trim();  // zabbix.token
string trapper = args[2];                                      // zabbix.example:10051
string prefix = args.Length > 3 ? args[3] : "";

await using var zabbix = new ZabbixClient(endpoint, token);
Console.WriteLine($"Zabbix API {await zabbix.GetApiVersionAsync()}");

string hostName = prefix + "gs-web-01";
ulong groupId = await zabbix.HostGroups.CreateAsync(prefix + "Getting started");
ulong hostId = await zabbix.Hosts.CreateAsync(hostName, [groupId]);
await zabbix.Items.CreateAsync(new ZabbixItemCreateRequest
{
    HostId = hostId,
    Name = "Requests",
    Key = "app.requests",
    Type = ZabbixItemType.Trapper,
    ValueType = ZabbixItemValueType.Unsigned,
    TrapperHosts = "0.0.0.0/0,::/0",                  // who may send to it
});
Console.WriteLine($"created {hostName} ({hostId})");

try
{
    var hosts = await zabbix.Hosts.GetAsync(q =>
    {
        q.Filter.TechnicalName = [hostName];
        q.Output = [ZabbixHostFields.HostId, ZabbixHostFields.TechnicalName];
    });
    foreach (ZabbixHost host in hosts)
        Console.WriteLine($"host:   {host.HostId} {host.TechnicalName}");

    var items = await zabbix.Items.GetAsync(q => q.HostIds = [hostId]);
    Console.WriteLine($"items:  {string.Join(", ", items.Select(i => i.Key))}");

    ZabbixItemKey<long> requests = new("app.requests");

    using var wire = new ZabbixWireClient(trapper);
    ZabbixSenderResponse sent;
    do
    {
        sent = await wire.SendAsync(hostName, requests, 42);
        if (sent.Processed == 0) await Task.Delay(TimeSpan.FromSeconds(5));
    }
    while (sent.Processed == 0);
    Console.WriteLine($"sent:   processed={sent.Processed} failed={sent.Failed}");

    for (int attempt = 0; attempt < 30; attempt++)
    {
        if (await zabbix.Hosts[hostName].FindLastAsync(requests) is { } last)
        {
            Console.WriteLine($"read:   {last.Value} at {last.Time:u}");
            break;
        }

        await Task.Delay(TimeSpan.FromSeconds(1));
    }

    try
    {
        await zabbix.HostGroups.CreateAsync(prefix + "Getting started");
    }
    catch (ZabbixApiException duplicate)
    {
        Console.WriteLine($"error:  {duplicate.Kind}: {duplicate.Message}");
    }
}
finally
{
    await zabbix.Hosts.DeleteAsync(hostId);
    await zabbix.HostGroups.DeleteAsync(groupId);
    Console.WriteLine("deleted");
}
