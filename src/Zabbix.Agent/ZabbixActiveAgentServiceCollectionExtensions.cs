// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using Community.Zabbix.Hosting;
using Community.Zabbix.Items;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>
/// Registers a <see cref="ZabbixActiveAgent"/> in a container: its named options, validated by the agent's own rules when the
/// host starts, and the agent, which the container builds from them, hosts and disposes. A named agent is a keyed service.
/// <see cref="ZabbixActiveAgent.CreateBuilder"/> builds its agent through the same registration, in a container of its own.
/// </summary>
public static class ZabbixActiveAgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers an active agent named <paramref name="name"/>, which the host starts and stops: it asks its servers which
    /// keys to collect, collects them on each key's schedule, and sends the values. Its host name is
    /// <paramref name="name"/> unless <c>Configure</c> or <c>Bind</c> sets another. Registering a name again continues setting
    /// up that agent, as <c>AddHttpClient</c> does.
    /// </summary>
    /// <param name="services">The container to register the agent in.</param>
    /// <param name="name">The agent's name: its options' name, its key in the container, and its host name unless set.</param>
    /// <returns>The agent's builder, whose verbs set it up.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="name"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// This method binds no configuration: <c>Bind</c> does, and <see cref="ZabbixAgentsServiceCollectionExtensions.AddZabbixAgents"/>
    /// binds each <c>ActiveAgents:{name}</c> row of a document. The agent is the keyed service
    /// <c>GetRequiredKeyedService&lt;ZabbixActiveAgent&gt;(name)</c>, and its statuses, one per destination, are
    /// <c>GetRequiredKeyedService&lt;IReadOnlyList&lt;ZabbixComponentStatus&gt;&gt;(name)</c>.
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddZabbixActiveAgent("web-01")
    ///     .AddServer("zbx-server-01")
    ///     .AddItem(pending, (IOrderQueue queue) =&gt; queue.Count)
    ///     .AddHealthCheck();
    /// </code>
    /// </example>
    public static IZabbixActiveAgentBuilder AddZabbixActiveAgent(this IServiceCollection services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var builder = new Registration(name, services);
        // The keyed agent is registered exactly once per name, below; its presence says this name is registered.
        if (services.Any(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(ZabbixActiveAgent) && Equals(descriptor.ServiceKey, name)))
            return builder;

        services.AddLogging();
        services.AddZabbixAgentTelemetry();
        // Disposed with the container, which stops the bundled keys' sampling with the agent.
        services.TryAddSingleton(static provider => new ZabbixItemCollector(ZabbixHostFiles.Host, provider.GetRequiredService<TimeProvider>()));
        // The name is the agent's host unless a later Configure or Bind says otherwise: this runs first.
        services.AddOptions<ZabbixActiveAgentOptions>(name).Configure(options => options.HostName = name).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ZabbixActiveAgentOptions>, ZabbixActiveAgentOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ZabbixActiveAgentOptions>, ZabbixActiveAgentServersValidator>());
        ZabbixComponentRegistration.AddShared<ZabbixActiveAgent>(services, name, static (provider, key) => Create(provider, NameOf(key)));
        services.AddSingleton<IHostedService>(provider => new ZabbixHostedComponent(provider.GetRequiredKeyedService<ZabbixActiveAgent>(name)));
        services.AddKeyedTransient<IReadOnlyList<ZabbixComponentStatus>>(name, static (provider, key) => provider.GetRequiredKeyedService<ZabbixActiveAgent>(key).Statuses);
        return builder;
    }

    // The agent's constructor, from the named options, with the items registered under its name composed into its table.
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The container owns the agent it resolves; a composition that fails disposes it here.")]
    private static ZabbixActiveAgent Create(IServiceProvider provider, string name)
    {
        var agent = new ZabbixActiveAgent(name, provider.GetRequiredService<IOptionsMonitor<ZabbixActiveAgentOptions>>().Get(name), provider.GetRequiredService<ILoggerFactory>(), provider.GetRequiredService<TimeProvider>(),
            provider.GetService<System.Diagnostics.Metrics.IMeterFactory>(), provider.GetRequiredService<ZabbixAgentTelemetry>(), provider.GetRequiredService<ZabbixWireTelemetry>(), provider.GetRequiredService<ZabbixItemCollector>());
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
    private static string NameOf(object? key) => key as string ?? throw new InvalidOperationException("An active agent is resolved by its name.");

    // The builder over the application's container: the verbs write there, and the container builds the agent.
    private sealed record Registration(string Name, IServiceCollection Services) : IZabbixActiveAgentBuilder;
}
