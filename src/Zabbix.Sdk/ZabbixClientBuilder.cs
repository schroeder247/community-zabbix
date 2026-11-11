// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>
/// Builds a <see cref="ZabbixClient"/> with more than an address and a credential to set, without a host:
/// <see cref="ZabbixClient.CreateBuilder"/> returns one. The verbs of <see cref="ZabbixBuilderExtensions"/> set the client up,
/// and <see cref="Build"/> validates its options and returns it. A client with only an address and a token needs no builder:
/// <c>new ZabbixClient("https://zabbix.example/", token)</c>.
/// </summary>
/// <remarks>
/// The builder holds a container of its own, <see cref="Services"/>, where it registers the client with
/// <see cref="ZabbixServiceCollectionExtensions.AddZabbix(IServiceCollection, string)"/>, the registration an application with
/// a host makes, so the client has the same HTTP pipeline and resilience either way. The client it returns owns that container
/// and disposes it.
/// </remarks>
/// <example>
/// <code language="csharp">
/// await using ZabbixClient zabbix = ZabbixClient.CreateBuilder()
///     .WithEndpoint("https://zabbix.example/")
///     .WithToken(token)
///     .WithMaxConnections(20)
///     .ConfigureResilience(resilience =&gt; resilience.Retry.MaxRetryAttempts = 5)
///     .UseLogging(loggers)
///     .Build();
/// </code>
/// </example>
public sealed class ZabbixClientBuilder : IZabbixBuilder
{
    private readonly ServiceCollection _services = [];
    private bool _built;

    /// <summary>A builder with nothing set. The client it builds needs an endpoint, which <c>WithEndpoint</c> or <c>Bind</c> sets.</summary>
    public ZabbixClientBuilder() => _services.AddZabbix(Name);

    /// <summary>The client's name in <see cref="Services"/>: <see cref="Options.DefaultName"/>, since it's the one client there.</summary>
    public string Name => Options.DefaultName;

    /// <summary>
    /// The builder's own container. The verbs write the client's registrations here, and the application can register what
    /// the client resolves from it, such as a service a credential reads.
    /// </summary>
    public IServiceCollection Services => _services;

    /// <summary>
    /// Makes the client log through <paramref name="loggers"/>. Without it, the client logs nowhere; its traces and metrics still
    /// flow. A later call replaces it.
    /// </summary>
    /// <param name="loggers">The application's logger factory. The application keeps owning it.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="loggers"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixClientBuilder UseLogging(ILoggerFactory loggers)
    {
        ArgumentNullException.ThrowIfNull(loggers);
        _services.Replace(ServiceDescriptor.Singleton(loggers));
        return this;
    }

    /// <summary>
    /// Makes the client read <paramref name="time"/>, not the system's clock: its deadlines, session renewals and watchers run on
    /// it. A later call replaces it.
    /// </summary>
    /// <param name="time">The clock the client reads.</param>
    /// <returns>This builder.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="time"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException"><see cref="Build"/> ran.</exception>
    public ZabbixClientBuilder UseTimeProvider(TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        _services.Replace(ServiceDescriptor.Singleton(time));
        return this;
    }

    /// <summary>
    /// Builds the client: checks its options with the rules a host checks at start, and returns it. The client owns the builder's
    /// container and disposes it. <see cref="Build"/> runs once, and <see cref="Services"/> can't change after it.
    /// </summary>
    /// <returns>The client, which you must dispose.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="Build"/> ran before, the options don't validate, or a service <see cref="Services"/> registered can't be
    /// built: the message says what to set, and the exception inside, an <see cref="OptionsValidationException"/> for the
    /// options, holds each failure.
    /// </exception>
    public ZabbixClient Build()
    {
        if (_built) throw new InvalidOperationException("This builder has built its client. Create another builder for another client.");
        _built = true;
        ServiceProvider container;
        try
        {
            container = Compose();
        }
        catch (Exception unbuilt) when (unbuilt is OptionsValidationException or AggregateException)
        {
            throw Unbuilt(unbuilt);
        }

        bool owned = false;
        try
        {
            ZabbixClient client = container.GetRequiredKeyedService<ZabbixClient>(Name);
            client.OwnContainer(container);
            owned = true;
            return client;
        }
        catch (OptionsValidationException invalid)
        {
            throw Unbuilt(invalid);
        }
        finally
        {
            if (!owned) container.Dispose();
        }
    }

    /// <summary>
    /// The container a constructor builds its client from: the same registration and the same verbs as a builder's, checked as
    /// <see cref="Build"/> checks them, with the options' failures thrown as they are.
    /// </summary>
    /// <param name="endpoint">The frontend's address or the JSON-RPC endpoint.</param>
    /// <param name="credential">The client's credential.</param>
    /// <returns>The container, which the client owns.</returns>
    /// <exception cref="OptionsValidationException">The options don't validate.</exception>
    internal static ServiceProvider Compose(Uri endpoint, ZabbixCredential credential)
        => new ZabbixClientBuilder().WithEndpoint(endpoint).WithCredential(credential).Compose();

    // The builder's container, checked as a host checks at start: every registration it can construct, and the options it
    // validates on start. A failure releases what the container built.
    private ServiceProvider Compose()
    {
        _services.MakeReadOnly();
        ServiceProvider container = _services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        try
        {
            container.GetService<IStartupValidator>()?.Validate();
            return container;
        }
        catch
        {
            container.Dispose();
            throw;
        }
    }

    // What fails here is the builder's state: the message says what to set, and the failure inside holds each one.
    private static InvalidOperationException Unbuilt(Exception failure)
    {
        string reasons = failure is AggregateException { InnerExceptions: var inner } ? string.Join("; ", inner.Select(each => each.Message)) : failure.Message;
        return new InvalidOperationException($"The client can't be built as it's set: {reasons}", failure);
    }
}
