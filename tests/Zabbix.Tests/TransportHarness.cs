// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The transport as the container composes it, with the primary handler swapped for a test double — the seam
/// Microsoft's own HTTP tests use — so every test goes through the real pipeline: auth, resilience, logging.
/// </summary>
internal sealed record Harness(ServiceProvider Provider, IZabbixTransport Transport, FakeLogCollector Logs) : IDisposable
{
    public void Dispose() => Provider.Dispose();
}

internal static class TransportHarness
{
    public static Harness Build(HttpMessageHandler primary, string? token = "tok", Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddZabbix().Configure(options =>
        {
            options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php");
            options.ApiToken = token;
        });
        services.AddLogging(logging => logging.AddFakeLogging().SetMinimumLevel(LogLevel.Trace));
        services.AddHttpClient(ZabbixHttp.GetClientName()).ConfigurePrimaryHttpMessageHandler(() => primary);
        configure?.Invoke(services);
        ServiceProvider provider = services.BuildValidated();
        return new Harness(provider, provider.GetRequiredService<IZabbixTransport>(), provider.GetFakeLogCollector());
    }
}
