// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>
/// Builds a <see cref="ZabbixWireClient"/> with more than a server to set, without a host:
/// <see cref="ZabbixWireClient.CreateBuilder"/> returns one. The verbs of <see cref="ZabbixWireClientBuilderExtensions"/>
/// set the client's options, and <see cref="Build"/> validates them and returns the client. A client with only a server
/// needs no builder: <c>new ZabbixWireClient("zbx-server-01")</c>.
/// </summary>
/// <remarks>
/// The builder holds a container of its own, <see cref="Services"/>, and <see cref="Build"/> registers the client there
/// with <see cref="ZabbixWireClientServiceCollectionExtensions.AddZabbixWireClient(IServiceCollection, string)"/>, the
/// registration an application with a host makes. The client it returns owns that container and disposes it.
/// </remarks>
/// <example>
/// <code language="csharp">
/// using ZabbixWireClient wire = ZabbixWireClient.CreateBuilder()
///     .AddServer("zbx-server-01")
///     .AddServer("zbx-server-02")
///     .WithPsk("web-01", key)
///     .WithTimeout(TimeSpan.FromSeconds(5))
///     .Build();
/// </code>
/// </example>
public sealed class ZabbixWireClientBuilder : IZabbixWireClientBuilder
{
    private readonly ServiceCollection _services = [];
    private bool _built;

    /// <summary>A builder with nothing set. The client it builds reads from agents only, until a server is added.</summary>
    public ZabbixWireClientBuilder()
    {
    }

    /// <summary>The client's name in <see cref="Services"/>: <see cref="Options.DefaultName"/>, since it's the one client there.</summary>
    public string Name => Options.DefaultName;

    /// <summary>
    /// The builder's own container. The verbs write the client's options here, and the application can register what the
    /// client resolves from it, such as its logging.
    /// </summary>
    public IServiceCollection Services => _services;

    /// <summary>Makes the client log through <paramref name="loggers"/>. Without it, the client logs nowhere. A later call replaces it.</summary>
    /// <param name="loggers">The application's logger factory. The application keeps owning it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loggers"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixWireClientBuilder UseLogging(ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        _services.Replace(ServiceDescriptor.Singleton(loggers));
        return this;
    }

    /// <summary>Makes the client's deadlines and times run on <paramref name="time"/>, not the system's clock. A later call replaces it.</summary>
    /// <param name="time">The clock the client's deadlines run on, and that dates the values it sends.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixWireClientBuilder UseTimeProvider(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _services.Replace(ServiceDescriptor.Singleton(time));
        return this;
    }

    /// <summary>
    /// Builds the client: checks its options with the rules a host checks at start, and returns it. The client owns the
    /// builder's container and disposes it. <see cref="Build"/> runs once, and <see cref="Services"/> can't change after it.
    /// </summary>
    /// <returns>The client, which you must dispose.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Build"/> ran before, or the options don't validate: the message says what to set, and the
    /// <see cref="OptionsValidationException"/> inside holds each failure.
    /// </exception>
    public ZabbixWireClient Build()
    {
        if (_built) throw new InvalidOperationException("This builder has built its client. Create another builder for another client.");
        _built = true;
        _services.AddZabbixWireClient(Name);
        _services.MakeReadOnly();

        ServiceProvider container = _services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        bool owned = false;
        try
        {
            ZabbixWireClient client = Resolve(container);
            client.OwnContainer(container);
            owned = true;
            return client;
        }
        finally
        {
            if (!owned) container.Dispose();
        }
    }

    // The registration's factory reads the named options, which validate there: what fails is this builder's state.
    private ZabbixWireClient Resolve(ServiceProvider container)
    {
        try
        {
            return container.GetRequiredKeyedService<ZabbixWireClient>(Name);
        }
        catch (OptionsValidationException invalid)
        {
            throw new InvalidOperationException($"The wire client can't be built as it's set: {invalid.Message}", invalid);
        }
    }
}
