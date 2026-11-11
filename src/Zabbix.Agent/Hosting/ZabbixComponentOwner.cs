// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Hosting;

/// <summary>
/// A container's hold on an agent or a proxy it builds and hands out by name. The component is shared: a caller's dispose
/// leaves it running, and disposing this, which the container does when it's disposed, releases it.
/// </summary>
/// <typeparam name="TComponent">The agent or proxy.</typeparam>
internal sealed class ZabbixComponentOwner<TComponent> : IAsyncDisposable, IDisposable
    where TComponent : ZabbixComponent
{
    /// <summary>Takes the component the container built, and shares it.</summary>
    /// <param name="component">The component.</param>
    public ZabbixComponentOwner(TComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        component.Share();
        Component = component;
    }

    /// <summary>The shared component.</summary>
    public TComponent Component { get; }

    /// <summary>Releases the component, stopping it first when nothing has.</summary>
    /// <returns>A task that completes when the component is released.</returns>
    public ValueTask DisposeAsync() => Component.ReleaseAsync();

    /// <summary>Releases the component at once.</summary>
    public void Dispose() => Component.Release();
}
