// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix.Hosting;

/// <summary>
/// Runs a delegate that takes a service from an agent's container (<c>(IOrderQueue queue) =&gt; queue.Count</c>): each call in
/// a scope of its own, as a request is in a web host, so a scoped service works and a transient one is released after it.
/// </summary>
internal static class ZabbixServiceDelegates
{
    /// <summary>Refuses a service the container doesn't have when the agent composes, rather than at every read.</summary>
    /// <typeparam name="TService">The service the delegate takes.</typeparam>
    /// <param name="provider">The agent's container.</param>
    /// <param name="keyName">The item the delegate reads.</param>
    /// <exception cref="InvalidOperationException">The container has no <typeparamref name="TService"/>.</exception>
    public static void Require<TService>(IServiceProvider provider, string keyName)
        where TService : notnull
        => Require(provider, typeof(TService), keyName);

    /// <summary>Refuses a service the container doesn't have when the agent is built, rather than at every read.</summary>
    /// <param name="provider">The agent's container.</param>
    /// <param name="service">The service a delegate takes.</param>
    /// <param name="keyName">The item the delegate reads.</param>
    /// <exception cref="InvalidOperationException">The container has no <paramref name="service"/>.</exception>
    public static void Require(IServiceProvider provider, Type service, string keyName)
    {
        if (provider.GetService<IServiceProviderIsService>() is { } services && !services.IsService(service))
            throw new InvalidOperationException($"The item '{keyName}' reads the service {service.Name}, which the agent's container doesn't have: register it in the builder's Services.");
    }

    /// <summary>Reads a value from the service, in a scope of its own.</summary>
    /// <typeparam name="TService">The service.</typeparam>
    /// <typeparam name="T">The value.</typeparam>
    /// <param name="provider">The agent's container.</param>
    /// <param name="read">The delegate.</param>
    /// <returns>The value.</returns>
    public static T Read<TService, T>(IServiceProvider provider, Func<TService, T> read)
        where TService : notnull
    {
        using IServiceScope scope = provider.CreateScope();
        return read(scope.ServiceProvider.GetRequiredService<TService>());
    }

    /// <summary>Reads a value from the service asynchronously, in a scope of its own.</summary>
    /// <typeparam name="TService">The service.</typeparam>
    /// <typeparam name="T">The value.</typeparam>
    /// <param name="provider">The agent's container.</param>
    /// <param name="read">The delegate.</param>
    /// <param name="cancellationToken">The read's cancellation: a poll's stop, or a check's deadline.</param>
    /// <returns>The value.</returns>
    public static async ValueTask<T> ReadAsync<TService, T>(IServiceProvider provider, Func<TService, CancellationToken, ValueTask<T>> read, CancellationToken cancellationToken)
        where TService : notnull
    {
        AsyncServiceScope scope = provider.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
            return await read(scope.ServiceProvider.GetRequiredService<TService>(), cancellationToken).ConfigureAwait(false);
    }
}
