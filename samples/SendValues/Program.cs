// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix;

// Sends one value to a trapper item, as zabbix_sender does. The host and the trapper item must exist on the server.
string server = Environment.GetEnvironmentVariable("ZABBIX_SERVER") ?? "zbx-server-01";

using var wire = new ZabbixWireClient(server);
ZabbixSenderResponse sent = await wire.SendAsync("web-01", new ZabbixItemKey<long>("app.requests"), 42);

Console.WriteLine($"processed {sent.Processed}, failed {sent.Failed} of {sent.Total}");
