// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Community.Zabbix.Tests;

/// <summary>A registration run the way an application runs it: the generic host, with nothing else in it.</summary>
internal static class TestHost
{
    public static async Task<IHost> StartAsync(Action<IServiceCollection> register, CancellationToken cancellationToken)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = Environments.Production });
        register(builder.Services);
        IHost host = builder.Build();
        try
        {
            await host.StartAsync(cancellationToken);
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
    }
}
