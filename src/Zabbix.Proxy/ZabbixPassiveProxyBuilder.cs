// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using Community.Zabbix.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix;

/// <summary>
/// Builds a <see cref="ZabbixPassiveProxy"/> without a host: <see cref="ZabbixPassiveProxy.CreateBuilder"/> returns one. The verbs
/// of <see cref="ZabbixPassiveProxyBuilderExtensions"/> and <see cref="ZabbixProxyBuilderExtensions"/>, and the store's and the
/// listener's, set the proxy up, and <see cref="Build"/> validates its options and returns it.
/// </summary>
/// <remarks>
/// The builder holds a container of its own, <see cref="Services"/>, where it registers the proxy with
/// <see cref="ZabbixPassiveProxyServiceCollectionExtensions.AddZabbixPassiveProxy"/>, the registration an application with a
/// host makes. The proxy it builds owns that container and disposes it.
/// </remarks>
/// <example>
/// <code language="csharp">
/// await using ZabbixPassiveProxy proxy = ZabbixPassiveProxy.CreateBuilder("proxy-dmz")
///     .WithVersion("7.4")
///     .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
///     .Build();
/// await proxy.StartAsync();
/// </code>
/// </example>
public sealed class ZabbixPassiveProxyBuilder : IZabbixPassiveProxyBuilder
{
    private readonly ServiceCollection _services = [];
    private bool _built;

    /// <summary>A builder for the proxy named <paramref name="name"/>, its name as the server knows it.</summary>
    /// <param name="name">The proxy's name.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is <see langword="null"/>.</exception>
    public ZabbixPassiveProxyBuilder(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _services.AddZabbixPassiveProxy(name);
    }

    /// <summary>The proxy's name as the server knows it.</summary>
    public string Name { get; }

    /// <summary>
    /// The builder's own container. The verbs write the proxy's options here, and the application can register what the
    /// proxy resolves from it: a store's dependencies, another transport.
    /// </summary>
    public IServiceCollection Services => _services;

    /// <summary>Makes the proxy log through <paramref name="loggers"/>. Without it, the proxy logs nowhere. A later call replaces it.</summary>
    /// <param name="loggers">The application's logger factory. The application keeps owning it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loggers"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixPassiveProxyBuilder UseLogging(ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        _services.Replace(ServiceDescriptor.Singleton(loggers));
        return this;
    }

    /// <summary>Makes the proxy's schedules and deadlines run on <paramref name="time"/>, not the system's clock. A later call replaces it.</summary>
    /// <param name="time">The clock a connection's deadline runs on, and that dates what the proxy sends.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixPassiveProxyBuilder UseTimeProvider(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _services.Replace(ServiceDescriptor.Singleton(time));
        return this;
    }

    /// <summary>Makes the proxy count on the application's meters. Without it, the proxy's container owns its meters. A later call replaces it.</summary>
    /// <param name="meters">The application's meter factory. The application keeps owning it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="meters"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixPassiveProxyBuilder UseMeterFactory(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _services.Replace(ServiceDescriptor.Singleton(meters));
        return this;
    }

    /// <summary>
    /// Builds the proxy: checks its options with the rules a host checks at start, and returns it. The proxy owns the builder's
    /// container and disposes it. <see cref="Build"/> runs once, and <see cref="Services"/> can't change after it.
    /// </summary>
    /// <returns>The proxy, which you must dispose.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Build"/> ran before, the options don't validate, or a service the proxy's verbs or <see cref="Services"/>
    /// registered can't be built: the message says what to set, and the exception inside, an <c>OptionsValidationException</c>
    /// for the options, holds each failure.
    /// </exception>
    public ZabbixPassiveProxy Build()
    {
        if (_built) throw new InvalidOperationException("This builder has built its proxy. Create another builder for another proxy.");
        _built = true;
        return ZabbixComponentBuilding.Build<ZabbixPassiveProxy>(_services, Name, "passive proxy");
    }
}
