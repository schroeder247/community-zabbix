// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Runtime.Loader;

namespace Community.Zabbix.Hosting;

/// <summary>
/// The load context of one plugin, as Microsoft's plugin guidance builds it ("Create a .NET application with plugins"):
/// the plugin's dependencies resolve through its own <c>.deps.json</c>, so two plugins, or a plugin and the agent, can
/// depend on different versions of one package. The shared contracts resolve from the agent's context whatever the
/// plugin's folder holds, so the plugin's entry and providers are the agent's types. Not collectible: a context unloads
/// only when nothing references its types, which a running agent cannot promise.
/// </summary>
internal sealed class ZabbixPluginLoadContext(string name, string assemblyPath) : AssemblyLoadContext("Zabbix plugin " + name, isCollectible: false)
{
    // The context the item contract is loaded in: the default one in an application, the module's in a PowerShell session.
    private static readonly AssemblyLoadContext s_host = GetLoadContext(typeof(ZabbixPluginEntry).Assembly) ?? Default;

    private readonly AssemblyDependencyResolver _resolver = new(assemblyPath);

    /// <summary>
    /// Whether the assembly named <paramref name="simple"/> is a contract the agent and its plugins share: the SDK's own
    /// assemblies, and the Microsoft.Extensions abstractions the contract exposes (dependency injection, configuration,
    /// logging, options and primitives).
    /// </summary>
    /// <param name="simple">The assembly's simple name.</param>
    internal static bool IsShared(string simple)
        => simple.StartsWith("Community.Zabbix.", StringComparison.Ordinal)
            || simple is "Microsoft.Extensions.Options" or "Microsoft.Extensions.Primitives"
            || (simple.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal) && simple.EndsWith(".Abstractions", StringComparison.Ordinal));

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is { } simple && IsShared(simple) && FromHost(simple) is { } shared)
            return shared;
        string? path = _resolver.ResolveAssemblyToPath(assemblyName);
        return path is null ? null : LoadFromAssemblyPath(path);            // null: the default context's (the framework)
    }

    protected override nint LoadUnmanagedDll(string unmanagedDllName)
    {
        string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
        return path is null ? 0 : LoadUnmanagedDllFromPath(path);
    }

    // The agent's copy, by simple name, whatever version the plugin was built against (a later major version is refused
    // before anything of the plugin runs). A shared name the agent does not have is the plugin's own.
    private static Assembly? FromHost(string simple)
    {
        foreach (Assembly loaded in s_host.Assemblies)
            if (string.Equals(loaded.GetName().Name, simple, StringComparison.Ordinal))
                return loaded;
        try
        {
            return s_host.LoadFromAssemblyName(new AssemblyName(simple));
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException)
        {
            return null;
        }
    }
}
