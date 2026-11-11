// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using Community.Zabbix.Hosting;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>
/// Registers a <see cref="ZabbixPushAgent"/> in a container: its named options, validated by the agent's own rules when the
/// host starts, and the agent, which the container builds from them, hosts and disposes. A named agent is a keyed service.
/// <see cref="ZabbixPushAgent.CreateBuilder"/> builds its agent through the same registration, in a container of its own.
/// </summary>
public static class ZabbixPushAgentServiceCollectionExtensions
{
    /// <summary>
    /// Registers a push agent named <paramref name="name"/>, which the host starts and stops: it collects, stores and sends
    /// for the host's lifetime, and drains at shutdown. Its host name is <paramref name="name"/> unless <c>Configure</c> or
    /// <c>Bind</c> sets another. Registering a name again continues setting up that agent, as <c>AddHttpClient</c> does.
    /// </summary>
    /// <param name="services">The container to register the agent in.</param>
    /// <param name="name">The agent's name: its options' name, its key in the container, and its host name unless set.</param>
    /// <returns>The agent's builder, whose verbs set it up.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> or <paramref name="name"/> is <see langword="null"/>.</exception>
    /// <remarks>
    /// The agent is the keyed service <c>GetRequiredKeyedService&lt;ZabbixPushAgent&gt;(name)</c>, and its statuses, one per
    /// destination once it starts, are <c>GetRequiredKeyedService&lt;IReadOnlyList&lt;ZabbixComponentStatus&gt;&gt;(name)</c>.
    /// A push and an active agent need names of their own for their statuses to be told apart.
    /// </remarks>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddZabbixPushAgent("web-01")
    ///     .AddServer("zbx-server-01")
    ///     .AddPoll(requests, TimeSpan.FromSeconds(30), () =&gt; counter.Value)
    ///     .AddHealthCheck();
    /// </code>
    /// </example>
    public static IZabbixPushAgentBuilder AddZabbixPushAgent(this IServiceCollection services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var builder = new Registration(name, services);
        // The keyed agent is registered exactly once per name, below; its presence says this name is registered.
        if (services.Any(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(ZabbixPushAgent) && Equals(descriptor.ServiceKey, name)))
            return builder;

        services.AddLogging();
        services.AddZabbixAgentTelemetry();
        // The name is the agent's host unless a later Configure or Bind says otherwise: this runs first.
        services.AddOptions<ZabbixPushAgentOptions>(name).Configure(options => options.HostName = name).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ZabbixPushAgentOptions>, ZabbixPushAgentOptionsValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ZabbixPushAgentOptions>, ZabbixPushAgentServersValidator>());
        // The agent's constructor, from the named options, over the container its sources and store resolve from. One hosted
        // service per name: AddHostedService<T> de-duplicates by type, so the agent is a keyed singleton and the hosted
        // service forwards to it.
        ZabbixComponentRegistration.AddShared<ZabbixPushAgent>(services, name, static (provider, key) =>
        {
            string agent = NameOf(key);
            ZabbixPushAgentOptions options = provider.GetRequiredService<IOptionsMonitor<ZabbixPushAgentOptions>>().Get(agent);
            foreach ((Type service, string keyName) in options.Requires) ZabbixServiceDelegates.Require(provider, service, keyName);
            return new ZabbixPushAgent(agent, options, provider, provider.GetRequiredService<ILoggerFactory>(),
                provider.GetRequiredService<TimeProvider>(), provider.GetService<IMeterFactory>(), provider.GetRequiredService<ZabbixAgentTelemetry>(), provider.GetRequiredService<ZabbixWireTelemetry>());
        });
        services.AddSingleton<IHostedService>(provider => new ZabbixHostedComponent(provider.GetRequiredKeyedService<ZabbixPushAgent>(name)));
        services.AddKeyedTransient<IReadOnlyList<ZabbixComponentStatus>>(name, static (provider, key) => provider.GetRequiredKeyedService<ZabbixPushAgent>(key).Statuses);
        return builder;
    }

    // A keyed registration is resolved by the agent's name, the key it was registered under.
    private static string NameOf(object? key) => key as string ?? throw new InvalidOperationException("A push agent is resolved by its name.");

    // The builder over the application's container: the verbs write there, and the container builds the agent.
    private sealed record Registration(string Name, IServiceCollection Services) : IZabbixPushAgentBuilder;
}
