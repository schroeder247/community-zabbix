// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using Community.Zabbix.Hosting;
using Community.Zabbix.Items;
using Community.Zabbix.Listening;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>
/// Registers a <see cref="ZabbixPassiveAgent"/> in a container: its named options, validated by the agent's own rules when
/// the host starts, and the agent, which the container builds from them, hosts and disposes. A named agent is a keyed
/// service. <see cref="ZabbixPassiveAgent.CreateBuilder"/> builds its agent through the same registration, in a container of
/// its own.
/// </summary>
public static class ZabbixPassiveAgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers a passive agent named <paramref name="name"/>, which the host starts and stops: it listens on the agent
    /// port, 10050 unless set, and answers the keys a server asks for. What <c>agent.hostname</c> answers is
    /// <paramref name="name"/> unless <c>Configure</c> or <c>Bind</c> sets another. Registering a name again continues setting
    /// up that agent, as <c>AddHttpClient</c> does.
    /// </summary>
    /// <param name="services">The container to register the agent in.</param>
    /// <param name="name">The agent's name: its options' name, its key in the container, and its host name unless set.</param>
    /// <returns>The agent's builder, whose verbs set it up.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="name"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// You must name who may poll the agent, with <c>AllowPeer</c>: an agent without peers doesn't start. This method binds no
    /// configuration: <c>Bind</c> does, and <c>AddZabbixProxies</c> binds each <c>PassiveAgents:{name}</c> row of a document.
    /// The options are read once, at start, so a reload doesn't move the port.
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddZabbixPassiveAgent("web-01")
    ///     .AllowPeer(IPNetwork.Parse("10.0.0.0/24"))
    ///     .AddItem(pending, (IOrderQueue queue) =&gt; queue.Count)
    ///     .AddHealthCheck();
    /// </code>
    /// </example>
    public static IZabbixPassiveAgentBuilder AddZabbixPassiveAgent(this IServiceCollection services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var builder = new Registration(name, services);
        // The keyed agent is registered exactly once per name, below; its presence says this name is registered.
        if (services.Any(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(ZabbixPassiveAgent) && Equals(descriptor.ServiceKey, name)))
            return builder;

        services.AddZabbixSocketTransport();
        services.TryAddSingleton(TimeProvider.System);
        // Disposed with the container, which stops the bundled keys' sampling with the agent.
        services.TryAddSingleton(static provider => new ZabbixItemCollector(ZabbixHostFiles.Host, provider.GetRequiredService<TimeProvider>()));
        // The name is the agent's host unless a later Configure or Bind says otherwise: this runs first.
        services.AddOptions<ZabbixPassiveAgentOptions>(name).Configure(options => options.Hostname = name).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ZabbixPassiveAgentOptions>, ZabbixPassiveAgentOptionsValidator>());
        services.AddKeyedSingleton<ZabbixPassiveAgent>(name, static (provider, key) => Create(provider, NameOf(key)));
        services.AddSingleton<IHostedService>(provider => new ZabbixHostedComponent(provider.GetRequiredKeyedService<ZabbixPassiveAgent>(name)));
        return builder;
    }

    // The agent's constructor, from the named options, with the items registered under its name composed into its table.
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The container owns the agent it resolves; a composition that fails disposes it here.")]
    private static ZabbixPassiveAgent Create(IServiceProvider provider, string name)
    {
        var agent = new ZabbixPassiveAgent(name, provider.GetRequiredService<IOptionsMonitor<ZabbixPassiveAgentOptions>>().Get(name), provider.GetRequiredKeyedService<IConnectionListenerFactory>(ZabbixListener.TransportKey),
            provider.GetRequiredService<ILoggerFactory>(), provider.GetRequiredService<TimeProvider>(), provider.GetService<System.Diagnostics.Metrics.IMeterFactory>(), provider.GetRequiredService<ZabbixItemCollector>());
        var key = new ZabbixItemsKey(name);
        try
        {
            agent.Items.Compose(provider.GetKeyedServices<ZabbixItemDictionary>(key), provider.GetKeyedServices<ZabbixItemProvider>(key));
        }
        catch
        {
            agent.Dispose();
            throw;
        }
        return agent;
    }

    // A keyed registration is resolved by the agent's name, the key it was registered under.
    private static string NameOf(object? key) => key as string ?? throw new InvalidOperationException("A passive agent is resolved by its name.");

    // The builder over the application's container: the verbs write there, and the container builds the agent.
    private sealed record Registration(string Name, IServiceCollection Services) : IZabbixPassiveAgentBuilder;
}
