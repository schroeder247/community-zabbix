// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix;

internal sealed class ZabbixWireClientFactory(IServiceProvider provider) : IZabbixWireClientFactory
{
    public ZabbixWireClient Create(string name)
        => TryCreate(name, out ZabbixWireClient? client) ? client : throw new KeyNotFoundException($"No wire client is registered as '{name}'.");

    public bool TryCreate(string name, [NotNullWhen(true)] out ZabbixWireClient? client)
    {
        ArgumentNullException.ThrowIfNull(name);
        client = provider.GetKeyedService<ZabbixWireClient>(name);
        return client is not null;
    }
}
