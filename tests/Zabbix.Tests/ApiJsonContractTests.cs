// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.Tests;

/// <summary>The public JSON contract: an application serializes the API's records exactly as the SDK does.</summary>
public sealed class ApiJsonContractTests
{
    [Fact]
    public void The_public_options_write_a_record_as_the_api_takes_it_and_read_it_back()
    {
        var host = new ZabbixHost { HostId = 10084, HostName = "web-01", Status = ZabbixHostStatus.Disabled };

        string json = JsonSerializer.Serialize(host, ZabbixJson.Options);
        ZabbixHost? back = JsonSerializer.Deserialize<ZabbixHost>(json, ZabbixJson.Options);

        Assert.Equal("""{"hostid":"10084","host":"web-01","status":1}""", json);
        Assert.Equal(host, back);
    }

    [Fact]
    public void The_public_options_cannot_be_changed_and_resolve_through_the_public_resolver()
    {
        Assert.True(ZabbixJson.Options.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => ZabbixJson.Options.WriteIndented = true);
        Assert.NotNull(ZabbixJson.TypeInfoResolver.GetTypeInfo(typeof(ZabbixHost), ZabbixJson.Options));
    }
}
