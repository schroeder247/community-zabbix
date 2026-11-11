// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix;

internal sealed class ZabbixClientFactory(IServiceProvider provider) : IZabbixClientFactory
{
    public ZabbixClient Create(string name)
        => TryCreate(name, out ZabbixClient? client) ? client : throw new KeyNotFoundException($"No Zabbix server is registered as '{name}'.");

    public bool TryCreate(string name, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ZabbixClient? client)
    {
        ArgumentNullException.ThrowIfNull(name);
        client = provider.GetKeyedService<ZabbixClient>(name);
        return client is not null;
    }
}
