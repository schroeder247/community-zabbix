// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using Community.Zabbix.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix;

/// <summary>
/// Builds a <see cref="ZabbixActiveAgent"/> without a host: <see cref="ZabbixActiveAgent.CreateBuilder"/> returns one. The
/// verbs of <see cref="ZabbixActiveAgentBuilderExtensions"/> and the item verbs (<c>AddItem</c>, <c>AddItems</c>,
/// <c>AddPlugins</c>) set the agent up, and <see cref="Build"/> validates its options and returns it.
/// </summary>
/// <remarks>
/// The builder holds a container of its own, <see cref="Services"/>, where it registers the agent with
/// <see cref="ZabbixActiveAgentServiceCollectionExtensions.AddZabbixActiveAgent"/>, the registration an application with a
/// host makes. The agent it builds owns that container and disposes it, and starts what the container hosts after itself.
/// </remarks>
/// <example>
/// <code language="csharp">
/// ZabbixItemKey&lt;long&gt; pending = new("orders.pending");
/// await using ZabbixActiveAgent agent = ZabbixActiveAgent.CreateBuilder("web-01")
///     .AddServer("zbx-server-01")
///     .AddItem(pending, () =&gt; queue.Count)
///     .Build();
/// await agent.StartAsync();
/// </code>
/// </example>
public sealed class ZabbixActiveAgentBuilder : IZabbixActiveAgentBuilder
{
    private readonly ServiceCollection _services = [];
    private bool _built;

    /// <summary>A builder for the agent of <paramref name="hostName"/>, its host as the server knows it unless <c>Configure</c> sets another.</summary>
    /// <param name="hostName">The agent's name, and its host.</param>
    /// <exception cref="ArgumentException"><paramref name="hostName"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="hostName"/> is <see langword="null"/>.</exception>
    public ZabbixActiveAgentBuilder(string hostName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostName);
        Name = hostName;
        _services.AddZabbixActiveAgent(hostName);
    }

    /// <summary>The agent's name: its host unless <c>Configure</c> sets another.</summary>
    public string Name { get; }

    /// <summary>
    /// The builder's own container. The verbs write the agent's options and items here, and the application can register
    /// what the agent resolves from it: a service an item reads, a provider's dependencies.
    /// </summary>
    public IServiceCollection Services => _services;

    /// <summary>Makes the agent log through <paramref name="loggers"/>. Without it, the agent logs nowhere. A later call replaces it.</summary>
    /// <param name="loggers">The application's logger factory. The application keeps owning it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loggers"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixActiveAgentBuilder UseLogging(ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        _services.Replace(ServiceDescriptor.Singleton(loggers));
        return this;
    }

    /// <summary>
    /// Makes the agent's schedules run on <paramref name="time"/>, not the system's clock: its checks' delays, read in the
    /// clock's local time zone, and the times of the values it sends. A later call replaces it.
    /// </summary>
    /// <param name="time">The clock.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixActiveAgentBuilder UseTimeProvider(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _services.Replace(ServiceDescriptor.Singleton(time));
        return this;
    }

    /// <summary>Makes the agent count on the application's meters. Without it, the agent's container owns its meters. A later call replaces it.</summary>
    /// <param name="meters">The application's meter factory. The application keeps owning it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="meters"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixActiveAgentBuilder UseMeterFactory(IMeterFactory meters)
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
    public ZabbixActiveAgent Build()
    {
        if (_built) throw new InvalidOperationException("This builder has built its agent. Create another builder for another agent.");
        _built = true;
        return ZabbixComponentBuilding.Build<ZabbixActiveAgent>(_services, Name, "active agent");
    }
}
