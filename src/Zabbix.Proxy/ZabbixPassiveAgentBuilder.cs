// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using Community.Zabbix.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix;

/// <summary>
/// Builds a <see cref="ZabbixPassiveAgent"/> without a host: <see cref="ZabbixPassiveAgent.CreateBuilder"/> returns one. The
/// verbs of <see cref="ZabbixPassiveAgentBuilderExtensions"/> and the item verbs (<c>AddItem</c>, <c>AddItems</c>,
/// <c>AddPlugins</c>) set the agent up, and <see cref="Build"/> validates its options and returns it.
/// </summary>
/// <remarks>
/// The builder holds a container of its own, <see cref="Services"/>, where it registers the agent with
/// <see cref="ZabbixPassiveAgentServiceCollectionExtensions.AddZabbixPassiveAgent"/>, the registration an application with a
/// host makes. The agent it builds owns that container and disposes it, and starts what the container hosts after itself.
/// </remarks>
/// <example>
/// <code language="csharp">
/// await using ZabbixPassiveAgent agent = ZabbixPassiveAgent.CreateBuilder("web-01")
///     .AllowPeer(IPNetwork.Parse("10.0.0.0/24"))
///     .AddItem("app.version", _ =&gt; "2.4.1")
///     .Build();
/// await agent.StartAsync();
/// </code>
/// </example>
public sealed class ZabbixPassiveAgentBuilder : IZabbixPassiveAgentBuilder
{
    private readonly ServiceCollection _services = [];
    private bool _built;

    /// <summary>A builder for the agent of <paramref name="hostName"/>, what <c>agent.hostname</c> answers unless <c>Configure</c> sets another.</summary>
    /// <param name="hostName">The agent's name, and its host.</param>
    /// <exception cref="ArgumentException"><paramref name="hostName"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="hostName"/> is <see langword="null"/>.</exception>
    public ZabbixPassiveAgentBuilder(string hostName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        Name = hostName;
        _services.AddZabbixPassiveAgent(hostName);
    }

    /// <summary>The agent's name: its host unless <c>Configure</c> sets another.</summary>
    public string Name { get; }

    /// <summary>
    /// The builder's own container. The verbs write the agent's options and items here, and the application can register
    /// what the agent resolves from it: a service an item reads, a provider's dependencies, another transport.
    /// </summary>
    public IServiceCollection Services => _services;

    /// <summary>Makes the agent log through <paramref name="loggers"/>. Without it, the agent logs nowhere. A later call replaces it.</summary>
    /// <param name="loggers">The application's logger factory. The application keeps owning it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loggers"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixPassiveAgentBuilder UseLogging(ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        _services.Replace(ServiceDescriptor.Singleton(loggers));
        return this;
    }

    /// <summary>Makes the agent's deadlines run on <paramref name="time"/>, not the system's clock. A later call replaces it.</summary>
    /// <param name="time">The clock a check's deadline and a connection's timeout run on.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixPassiveAgentBuilder UseTimeProvider(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _services.Replace(ServiceDescriptor.Singleton(time));
        return this;
    }

    /// <summary>Makes the agent count on the application's meters. Without it, the agent owns its meter. A later call replaces it.</summary>
    /// <param name="meters">The application's meter factory. The application keeps owning it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="meters"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixPassiveAgentBuilder UseMeterFactory(IMeterFactory meters)
    {
        ArgumentNullException.ThrowIfNull(meters);
        _services.Replace(ServiceDescriptor.Singleton(meters));
        return this;
    }

    /// <summary>
    /// Builds the agent: checks its options with the rules a host checks at start, composes its items, and returns it. The
    /// agent owns the builder's container and disposes it. <see cref="Build"/> runs once, and <see cref="Services"/> can't
    /// change after it.
    /// </summary>
    /// <returns>The agent, which you must dispose.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Build"/> ran before, the options don't validate, or a service the agent's verbs or <see cref="Services"/>
    /// registered can't be built: the message says what to set, and the exception inside, an <c>OptionsValidationException</c>
    /// for the options, holds each failure.
    /// </exception>
    public ZabbixPassiveAgent Build()
    {
        if (_built) throw new InvalidOperationException("This builder has built its agent. Create another builder for another agent.");
        _built = true;
        return ZabbixComponentBuilding.Build<ZabbixPassiveAgent>(_services, Name, "passive agent");
    }
}
