// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Lends this module's folder to the modules that require it (docs/adr/0003-powershell-module-split.md).
/// Community.Zabbix.Agent and Community.Zabbix.Proxy are installed beside this module, not inside it, and carry only what this module's
/// folder does not (Zabbix.Sdk, Microsoft.Extensions.*, Polly… are here, once). The runtime looks for an assembly a
/// module's assembly references in that assembly's own folder only (Learn, "Resolving PowerShell module assembly
/// dependency conflicts"; without this, <c>Start-ZbxPassiveAgent</c> fails on
/// <c>Microsoft.Extensions.Hosting.Abstractions</c>, which only this folder holds), so a reference from a sibling module
/// to a shared assembly is answered here, through the load context's own extension point for an assembly it could not
/// find (<see cref="AssemblyLoadContext.Resolving"/>).
/// </summary>
/// <remarks>
/// The handler is process-wide: it sees every unresolved reference of every module in the session, not only the
/// siblings'. It therefore answers only for what this module ships, and only when what it ships is new enough:
/// <list type="bullet">
/// <item>The set is this module's own dependency closure, read from the <c>.deps.json</c> the build writes beside the
/// module assembly by <see cref="AssemblyDependencyResolver"/> (the runtime's component resolver, the one Learn's
/// plugin and module-isolation guidance uses), so it is derived, never listed by hand: a file in the folder that the
/// closure does not name is not lent.</item>
/// <item>The file's own assembly version must be at least the one requested (the default context's own binding rule):
/// a module built against a newer copy than this folder holds gets no answer, so its load fails at the reference with
/// the requested version named, rather than binding to an older copy and failing later on a missing member.</item>
/// </list>
/// </remarks>
internal sealed class ZabbixModuleDependencies
{
    private static readonly ZabbixModuleDependencies s_module = new(typeof(ZabbixModuleDependencies).Assembly.Location);

    private readonly AssemblyDependencyResolver _closure;

    // Registered once per process by the type initializer: the handler holds only the module's closure, the load
    // context the module's.
    static ZabbixModuleDependencies()
        => (AssemblyLoadContext.GetLoadContext(typeof(ZabbixModuleDependencies).Assembly) ?? AssemblyLoadContext.Default).Resolving += s_module.Resolve;

    /// <summary>Reads the dependency closure of the module assembly at <paramref name="moduleAssembly"/>.</summary>
    /// <param name="moduleAssembly">The module assembly's path; its <c>.deps.json</c> beside it names the closure.</param>
    internal ZabbixModuleDependencies(string moduleAssembly) => _closure = new AssemblyDependencyResolver(moduleAssembly);

    /// <summary>Makes sure the resolver is registered (the type initializer runs once).</summary>
    internal static void Register()
    {
        // Touching the type runs its initializer; nothing else to do.
    }

    /// <summary>
    /// Refuses the import when the module's load context already holds an older release of an assembly this module
    /// ships under the same assembly version.
    /// </summary>
    /// <remarks>
    /// The runtime binds a reference to any loaded copy whose assembly version is at least the requested one, so a
    /// package that keeps one assembly version across its minor releases (BouncyCastle.Cryptography is 2.0.0.0 for every
    /// 2.x) binds silently to an older release another module loaded first, and fails later on a member that release
    /// lacks. The file version tells the releases apart: a loaded copy whose file version is older in its major or minor
    /// part than the copy in this module's folder stops the import, naming both, while a newer or a patch-level
    /// difference is accepted.
    /// </remarks>
    /// <exception cref="FileLoadException">An assembly in the closure is loaded at an older release.</exception>
    internal static void Verify()
        => s_module.Verify(AssemblyLoadContext.GetLoadContext(typeof(ZabbixModuleDependencies).Assembly) ?? AssemblyLoadContext.Default);

    /// <summary>Refuses an older release of a closure assembly loaded into <paramref name="context"/>.</summary>
    /// <param name="context">The load context the module's references bind in.</param>
    /// <exception cref="FileLoadException">An assembly in the closure is loaded at an older release.</exception>
    internal void Verify(AssemblyLoadContext context)
    {
        foreach (Assembly loaded in context.Assemblies)
        {
            AssemblyName name = loaded.GetName();
            if (_closure.ResolveAssemblyToPath(name) is not { } path
                || string.Equals(Path.GetFullPath(path), loaded.Location, StringComparison.Ordinal)
                || AssemblyName.GetAssemblyName(path).Version != name.Version
                || !Version.TryParse(FileVersionInfo.GetVersionInfo(path).FileVersion, out Version? shipped)
                || !Version.TryParse(loaded.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version, out Version? held)
                || (held.Major, held.Minor).CompareTo((shipped.Major, shipped.Minor)) >= 0)
            {
                continue;
            }

            throw new FileLoadException(string.Format(CultureInfo.InvariantCulture,
                "The module needs {0} {1} or newer, but the session already loaded {0} {2} under the same assembly version {3}. Start a new session and import Community.Zabbix before the module that loads the older copy.",
                name.Name, shipped.ToString(3), held.ToString(3), name.Version), name.FullName);
        }
    }

    /// <summary>
    /// The assembly <paramref name="name"/> loaded into <paramref name="context"/> from this module's closure, when the
    /// closure names it at a version at least the requested one; otherwise <see langword="null"/>, so the load fails as
    /// it would without this module.
    /// </summary>
    /// <param name="context">The load context that could not resolve <paramref name="name"/>.</param>
    /// <param name="name">The reference that could not be resolved.</param>
    internal Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        => _closure.ResolveAssemblyToPath(name) is { } path && AssemblyName.GetAssemblyName(path).Version >= name.Version
            ? context.LoadFromAssemblyPath(path)
            : null;
}
