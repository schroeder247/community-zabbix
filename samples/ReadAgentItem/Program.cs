// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix;

// Reads one item from an agent, as zabbix_get does. The agent must allow this address in its Server setting.
string agent = args is [{ } named, ..] ? named : "web-01";
string key = args is [_, { } asked, ..] ? asked : "system.uname";

using var wire = new ZabbixWireClient();
ZabbixAgentAnswer answer = await wire.GetAgentItemAsync(agent, key);

Console.WriteLine(answer.IsSupported ? answer.Value : $"not supported: {answer.Error}");
