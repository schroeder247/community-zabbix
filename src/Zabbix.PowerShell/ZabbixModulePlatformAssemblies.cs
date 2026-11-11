// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Frozen;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Answers a module's references to the assemblies its folder carries once per platform: the ASP.NET Core connection
/// layer the listener modules (Community.Zabbix.Agent, Community.Zabbix.Proxy) take from the shared framework's runtime
/// packs (src/Zabbix.PowerShell.AspNetCoreTransport.targets). A runtime pack's assemblies are ReadyToRun images for one
/// operating system and processor, and the runtime refuses one built for another (<see cref="BadImageFormatException"/>),
/// so the folder holds each platform's copy at <c>runtimes/&lt;platform&gt;/lib/net10.0/</c> and a reference is answered
/// from the folder of the platform the process runs on (<see cref="Platform"/>), through the load context's own
/// extension point for an assembly it could not find (<see cref="AssemblyLoadContext.Resolving"/>).
/// </summary>
/// <remarks>
/// Like <see cref="ZabbixModuleDependencies"/>, the handler is process-wide, so it answers only for what the module ships:
/// an assembly whose file is in the platform's folder (each file read once by its own assembly name, so a reference's
/// name never becomes a path), at an assembly version at least the requested one. On a platform the module carries no
/// folder for, nothing is answered, and the reference fails as it would without the handler. Each module registers it
/// from its type initializer, so once per process however often the module is imported.
/// </remarks>
internal sealed class ZabbixModulePlatformAssemblies
{
    private readonly FrozenDictionary<string, string> _files;

    /// <summary>Reads the assemblies of one platform's folder; a file that is not an assembly is left out.</summary>
    /// <param name="folder">The platform's folder, <c>runtimes/&lt;platform&gt;/lib/net10.0/</c> of a module folder.</param>
    internal ZabbixModulePlatformAssemblies(string folder)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(folder))
        {
            foreach (string file in Directory.EnumerateFiles(folder, "*.dll"))
            {
                try
                {
                    files[AssemblyName.GetAssemblyName(file).Name!] = file;
                }
                catch (BadImageFormatException)
                {
                }
            }
        }
        _files = files.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The platform the process runs on, as the runtime packs name it (<c>win-x64</c>, <c>linux-arm64</c>,
    /// <c>osx-arm64</c>, …), or <see langword="null"/> on an operating system pwsh is not released for. Alpine's pwsh
    /// (linux-musl-x64) is <c>linux-x64</c>: the image format names the operating system and processor only.
    /// </summary>
    internal static string? Platform { get; } = PlatformOf(
        OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux" : null,
        RuntimeInformation.ProcessArchitecture);

    /// <summary>The runtime pack name of an operating system and processor.</summary>
    /// <param name="system">The operating system's part of the name (<c>win</c>, <c>linux</c>, <c>osx</c>).</param>
    /// <param name="processor">The process's processor architecture.</param>
    internal static string? PlatformOf(string? system, Architecture processor) => system is null ? null : processor switch
    {
        Architecture.X64 => system + "-x64",
        Architecture.X86 => system + "-x86",
        Architecture.Arm64 => system + "-arm64",
        Architecture.Arm => system + "-arm",
        _ => null,
    };

    /// <summary>
    /// Registers the handler that answers <paramref name="module"/>'s references from its folder for the running
    /// platform, in the module's load context.
    /// </summary>
    /// <param name="module">The module assembly, in the module folder that carries <c>runtimes/</c>.</param>
    internal static void Register(Assembly module)
    {
        if (Platform is null || Path.GetDirectoryName(module.Location) is not { Length: > 0 } directory) return;
        var assemblies = new ZabbixModulePlatformAssemblies(Path.Combine(directory, "runtimes", Platform, "lib", "net10.0"));
        (AssemblyLoadContext.GetLoadContext(module) ?? AssemblyLoadContext.Default).Resolving += assemblies.Resolve;
    }

    /// <summary>
    /// The assembly <paramref name="name"/> loaded into <paramref name="context"/> from the platform's folder, when the
    /// folder holds it at a version at least the requested one; otherwise <see langword="null"/>, so the load fails as it
    /// would without this module.
    /// </summary>
    /// <param name="context">The load context that could not resolve <paramref name="name"/>.</param>
    /// <param name="name">The reference that could not be resolved.</param>
    internal Assembly? Resolve(AssemblyLoadContext context, AssemblyName name)
        => name.Name is { } simple && _files.TryGetValue(simple, out string? path) && AssemblyName.GetAssemblyName(path).Version >= name.Version
            ? context.LoadFromAssemblyPath(path)
            : null;
}
