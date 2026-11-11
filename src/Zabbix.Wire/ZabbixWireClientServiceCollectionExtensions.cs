// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>
/// Registers a <see cref="ZabbixWireClient"/> in a container: its named options, validated by the client's own rules when
/// the host starts, and the client, which the container builds from them, shares and disposes. A named client is a keyed
/// service, which <see cref="IZabbixWireClientFactory"/> returns by name. <see cref="ZabbixWireClient.CreateBuilder"/> builds
/// its client through the same registration, in a container of its own.
/// </summary>
public static class ZabbixWireClientServiceCollectionExtensions
{
    /// <summary>Registers the unnamed wire client, which is also injected as plain <see cref="ZabbixWireClient"/>.</summary>
    /// <param name="services">The container to register the client in.</param>
    /// <returns>The client's builder, whose verbs set it up.</returns>
    public static IZabbixWireClientBuilder AddZabbixWireClient(this IServiceCollection services) => services.AddZabbixWireClient(Options.DefaultName);

    /// <summary>
    /// Registers a wire client named <paramref name="name"/>. Registering a name again continues setting up that client, as
    /// <c>AddHttpClient</c> does.
    /// </summary>
    /// <param name="services">The container to register the client in.</param>
    /// <param name="name">The name; <see cref="Options.DefaultName"/> is the unnamed client.</param>
    /// <returns>The client's builder, whose verbs set it up.</returns>
    /// <example>
    /// <code language="csharp">
    /// builder.Services.AddZabbixWireClient("prod")
    ///     .AddServer("zbx-server-01")
    ///     .AddServer("zbx-server-02")
    ///     .WithTimeout(TimeSpan.FromSeconds(5));
    /// // later, from the container
    /// ZabbixWireClient prod = services.GetRequiredService&lt;IZabbixWireClientFactory&gt;().Create("prod");
    /// </code>
    /// </example>
    public static IZabbixWireClientBuilder AddZabbixWireClient(this IServiceCollection services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(name);

        var builder = new Registration(name, services);
        // The keyed client is registered exactly once per name, below; its presence says this name is registered.
        if (services.Any(descriptor => descriptor.IsKeyedService && descriptor.ServiceType == typeof(ZabbixWireClient) && Equals(descriptor.ServiceKey, name)))
            return builder;

        services.AddOptions<ZabbixWireOptions>(name).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ZabbixWireOptions>, ZabbixWireOptionsValidator>());
        services.TryAddSingleton<IZabbixWireClientFactory, ZabbixWireClientFactory>();

        // The client's own constructor, from the named options. The container's logging and clock are used when it has them,
        // and the client's defaults otherwise: no logging, and the system's clock.
        services.TryAddKeyedSingleton(name, static (provider, key) => new ZabbixWireClient(
            provider.GetRequiredService<IOptionsMonitor<ZabbixWireOptions>>().Get(NameOf(key)),
            provider.GetService<ILoggerFactory>(),
            provider.GetService<TimeProvider>()));

        // The unnamed client is the plain unkeyed service too, so the one-client case injects ZabbixWireClient.
        if (name == Options.DefaultName)
            services.TryAddSingleton(static provider => provider.GetRequiredKeyedService<ZabbixWireClient>(Options.DefaultName));

        return builder;
    }

    // A keyed registration is resolved by the client's name, the key it was registered under.
    private static string NameOf(object? key) => key as string ?? throw new InvalidOperationException("A wire client is resolved by its name.");

    // The builder over the application's container: the verbs write there, and the container builds the client.
    private sealed record Registration(string Name, IServiceCollection Services) : IZabbixWireClientBuilder;
}
