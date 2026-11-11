// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Hosting;

/// <summary>
/// The one <c>Build()</c> of the components' standalone builders: the builder's container, built and validated as a host
/// validates at start, the component its registration made, and the component made the owner of that container.
/// </summary>
internal static class ZabbixComponentBuilding
{
    /// <summary>
    /// Builds <paramref name="services"/> and resolves the component registered in it under <paramref name="name"/>. The
    /// component owns the container from then on, and starts what the container hosts after itself.
    /// </summary>
    /// <typeparam name="TComponent">The component.</typeparam>
    /// <param name="services">The builder's own container, which can't change after this.</param>
    /// <param name="name">The component's name, the key it's registered under.</param>
    /// <param name="kind">What the component is, for the message: <c>push agent</c>.</param>
    /// <returns>The component.</returns>
    /// <exception cref="InvalidOperationException">
    /// The options don't validate, or a service registered in the container can't be built: the message says what to set.
    /// </exception>
    public static TComponent Build<TComponent>(ServiceCollection services, string name, string kind)
        where TComponent : ZabbixComponent
    {
        services.MakeReadOnly();
        ServiceProvider container = BuildContainer(services, name, kind);
        bool owned = false;
        try
        {
            TComponent component = Resolve<TComponent>(container, name, kind);
            // What the container hosts beside the component, as a host would start it: a trapper, a library's runner.
            IHostedService[] companions = [.. container.GetServices<IHostedService>().Where(hosted => hosted is not ZabbixHostedComponent)];
            component.OwnContainer(container, companions);
            owned = true;
            return component;
        }
        finally
        {
            if (!owned) Release(container);
        }
    }

    // The container checks every registration it can construct, as a host does in development: a provider whose constructor
    // needs a service nobody registered fails here, and that's the builder's state too.
    private static ServiceProvider BuildContainer(ServiceCollection services, string name, string kind)
    {
        try
        {
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
        }
        catch (AggregateException unbuildable)
        {
            throw new InvalidOperationException($"The {kind} '{name}' can't be built as it's set: {unbuildable.Message}", unbuildable);
        }
    }

    // The registration's options validate here, every one a host checks at start: what fails is the builder's state.
    private static TComponent Resolve<TComponent>(ServiceProvider container, string name, string kind)
        where TComponent : ZabbixComponent
    {
        try
        {
            container.GetService<IStartupValidator>()?.Validate();
            return container.GetRequiredKeyedService<TComponent>(name);
        }
        catch (OptionsValidationException invalid)
        {
            throw new InvalidOperationException($"The {kind} '{name}' can't be built as it's set: {invalid.Message}", invalid);
        }
        catch (AggregateException invalid) when (invalid.InnerExceptions.All(failure => failure is OptionsValidationException))
        {
            string message = string.Join("; ", invalid.InnerExceptions.Select(failure => failure.Message));
            throw new InvalidOperationException($"The {kind} '{name}' can't be built as it's set: {message}", invalid);
        }
    }

    // A failed build releases what the container built; a provider disposable asynchronously only finishes on its own,
    // as a store's last commit does, and the build's own failure is the one the caller gets.
    private static void Release(ServiceProvider container)
    {
        ValueTask releasing = container.DisposeAsync();
        if (!releasing.IsCompletedSuccessfully) _ = releasing.AsTask();
    }
}
