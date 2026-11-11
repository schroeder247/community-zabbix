// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using Community.Zabbix.Hosting;
using Community.Zabbix.Listening;
using Community.Zabbix.Protocol;
using Community.Zabbix.Proxy;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>
/// Registers a <see cref="ZabbixActiveProxy"/> in a container: its named options, validated by the proxy's own rules when
/// the host starts, and the proxy, which the container builds from them, hosts and disposes. A named proxy is a keyed
/// service. <see cref="ZabbixActiveProxy.CreateBuilder"/> builds its proxy through the same registration, in a container of
/// its own.
/// </summary>
public static class ZabbixActiveProxyServiceCollectionExtensions
{
    /// <summary>
    /// Registers an active proxy named <paramref name="name"/>, which the host starts and stops: a trapper on 10051 unless
    /// set, the configuration pulled from its server, and history uploaded as <c>proxy data</c>. Registering a name again
    /// continues setting up that proxy, as <c>AddHttpClient</c> does. The proxy is the keyed service
    /// <c>GetRequiredKeyedService&lt;ZabbixActiveProxy&gt;(name)</c>.
    /// </summary>
    /// <param name="services">The container to register the proxy in.</param>
    /// <param name="name">The proxy's name as the server knows it: its options' name, and its key in the container.</param>
    /// <returns>The proxy's builder, whose verbs set it up.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="name"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// You must name its server with <c>AddServer</c>, and who may reach its trapper with <c>AllowPeer</c>: a proxy without
    /// them doesn't start. This method binds no configuration: <c>Bind</c> does, and <c>AddZabbixProxies</c> binds each
    /// <c>Proxies:{name}</c> row of a document.
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddZabbixActiveProxy("proxy-01")
    ///     .AddServer("zbx-server-01")
    ///     .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
    ///     .AddHealthCheck();
    /// </code>
    /// </example>
    public static IZabbixActiveProxyBuilder AddZabbixActiveProxy(this IServiceCollection services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var builder = new Registration(name, services);
        // The keyed proxy is registered exactly once per name, below; its presence says this name is registered.
        if (services.Any(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(ZabbixActiveProxy) && Equals(descriptor.ServiceKey, name)))
            return builder;

        services.AddZabbixSocketTransport();
        services.AddLogging();
        services.AddZabbixWireTelemetry();                                                 // the host's meter factory and the wire's telemetry, as every Add* registers them
        services.AddOptions<ZabbixProxyOptions>(name).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ZabbixProxyOptions>, ZabbixProxyOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ZabbixProxyOptions>, ZabbixProxyProtocolValidator>());
        // This proxy's server, and not a passive proxy's of another name in the same container, which has none.
        services.AddSingleton<IValidateOptions<ZabbixProxyOptions>>(new ZabbixProxyServersValidator(name));
        services.AddKeyedSingleton<ZabbixActiveProxy>(name, static (provider, key) =>
        {
            string proxy = NameOf(key);
            ZabbixProxyOptions options = provider.GetRequiredService<IOptionsMonitor<ZabbixProxyOptions>>().Get(proxy);
            return new ZabbixActiveProxy(proxy, options, ZabbixProxyStores.HistoryFactory(options, provider),
                provider.GetRequiredKeyedService<IConnectionListenerFactory>(ZabbixListener.TransportKey), provider.GetRequiredService<ILoggerFactory>(), provider.GetRequiredService<TimeProvider>(),
                provider.GetService<IMeterFactory>(), provider.GetService<ZabbixWireTelemetry>());
        });
        services.AddSingleton<IHostedService>(provider => new ZabbixHostedComponent(provider.GetRequiredKeyedService<ZabbixActiveProxy>(name)));
        return builder;
    }

    // A keyed registration is resolved by the proxy's name, the key it was registered under.
    private static string NameOf(object? key) => key as string ?? throw new InvalidOperationException("An active proxy is resolved by its name.");

    // The builder over the application's container: the verbs write there, and the container builds the proxy.
    private sealed record Registration(string Name, IServiceCollection Services) : IZabbixActiveProxyBuilder;
}
