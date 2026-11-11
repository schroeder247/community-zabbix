// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using Microsoft.Extensions.Configuration;

namespace Community.Zabbix.Hosting;

/// <summary>
/// Loads the plugins an agent lists, while its registration runs: before its container is built and its options
/// validated, so a plugin that cannot be loaded fails the start, as Zabbix's agent does not start without a module it
/// was told to load. Each plugin is checked before anything of it runs: its name, its folder's permissions, the
/// contract version it was built against, and its entry.
/// </summary>
internal static class ZabbixPluginLoader
{
    private const string Contract = "Community.Zabbix.Abstractions";

    internal static void Load(IZabbixItemsBuilder builder, ZabbixPluginOptions options)
    {
        if (options.Load.Count == 0)
            return;
        if (string.IsNullOrWhiteSpace(options.Path))
            throw new InvalidOperationException(
                $"The agent '{builder.Name}' lists plugins ({string.Join(", ", options.Load)}) but no Path to load them from.");
        string root = Path.GetFullPath(options.Path);
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (string name in options.Load)
        {
            if (!IsPluginName(name))
                throw new InvalidOperationException($"'{name}' is not a plugin name: a name is a folder under {root}, without a path.");
            if (!listed.Add(name))
                throw new InvalidOperationException($"The plugin '{name}' is listed twice.");
            Register(builder, root, name, options.Settings?.GetSection(name) ?? new ZabbixEmptyConfiguration(name));
        }
    }

    private static bool IsPluginName(string? name)
        => !string.IsNullOrWhiteSpace(name) && name is not ("." or "..") && name == Path.GetFileName(name)
            && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static void Register(IZabbixItemsBuilder builder, string root, string name, IConfiguration settings)
    {
        string folder = Path.Combine(root, name);
        string assemblyPath = Path.Combine(folder, name + ".dll");
        if (!File.Exists(assemblyPath))
            throw new InvalidOperationException(
                $"The plugin '{name}' is listed, but {assemblyPath} does not exist: a plugin is its project's dotnet publish output, in a folder named after it.");
        RefuseWritableByOthers(name, folder);

        Assembly assembly;
        try
        {
            assembly = new ZabbixPluginLoadContext(name, assemblyPath).LoadFromAssemblyPath(assemblyPath);
        }
        catch (Exception e) when (e is BadImageFormatException or FileLoadException)
        {
            throw new InvalidOperationException($"The plugin '{name}' could not be loaded from {assemblyPath}: {e.Message}", e);
        }
        RefuseLaterContract(name, assembly);

        ZabbixPluginEntry entry;
        try
        {
            entry = CreateEntry(name, assembly);
        }
        catch (Exception e) when (e is FileNotFoundException or FileLoadException or BadImageFormatException or TypeLoadException or CustomAttributeFormatException)
        {
            throw new InvalidOperationException($"The plugin '{name}' could not be loaded from {assemblyPath}: {e.Message}", e);
        }
        try
        {
            entry.Register(builder, settings);
        }
        catch (Exception e) when (e is not InvalidOperationException)
        {
            throw new InvalidOperationException($"The plugin '{name}' failed to register its items: {e.Message}", e);
        }
    }

    // A plugin runs with the agent's account: a file another account can write is code that account chooses, and so is a
    // folder it can replace, which a writable folder above lets it do unless that folder's sticky bit stops it. The rule
    // is the file mode's, so Windows, whose access lists say who may write, is left to them.
    private static void RefuseWritableByOthers(string name, string folder)
    {
        if (OperatingSystem.IsWindows())
            return;
        foreach (string path in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Prepend(folder))
            if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new InvalidOperationException(
                    $"The plugin '{name}' is not loaded: {path} is writable by accounts other than its owner (chmod -R go-w {folder}).");
        for (DirectoryInfo? above = Directory.GetParent(folder); above is not null; above = above.Parent)
        {
            UnixFileMode mode = File.GetUnixFileMode(above.FullName);
            if ((mode & UnixFileMode.StickyBit) == 0 && (mode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                throw new InvalidOperationException(
                    $"The plugin '{name}' is not loaded: {above.FullName}, a folder above it, is writable by accounts other than its owner, who could replace the plugin (chmod go-w {above.FullName}).");
        }
    }

    // Read from the plugin's references, before any of its code runs: a plugin built against a later major version of
    // the contract may call members this agent does not have.
    private static void RefuseLaterContract(string name, Assembly assembly)
    {
        Version agent = typeof(ZabbixPluginEntry).Assembly.GetName().Version ?? new Version();
        foreach (AssemblyName reference in assembly.GetReferencedAssemblies())
            if (reference.Name == Contract && reference.Version is { } built && built.Major > agent.Major)
                throw new InvalidOperationException(
                    $"The plugin '{name}' was built against {Contract} {built}, a later major version than the agent's {agent}.");
    }

    private static ZabbixPluginEntry CreateEntry(string name, Assembly assembly)
    {
        Type type = assembly.GetCustomAttribute<ZabbixPluginAttribute>()?.Entry
            ?? throw new InvalidOperationException(
                $"The plugin '{name}' names no entry: its assembly needs [assembly: ZabbixPlugin(typeof(…))], naming its ZabbixPluginEntry.");
        if (!typeof(ZabbixPluginEntry).IsAssignableFrom(type) || type.IsAbstract)
            throw new InvalidOperationException($"The plugin '{name}' names {type.FullName} as its entry, which is not a ZabbixPluginEntry it can create.");
        try
        {
            return Activator.CreateInstance(type) as ZabbixPluginEntry
                ?? throw new InvalidOperationException($"The plugin '{name}''s entry {type.FullName} could not be created.");
        }
        catch (MissingMethodException e)
        {
            throw new InvalidOperationException($"The plugin '{name}''s entry {type.FullName} has no public parameterless constructor.", e);
        }
        catch (TargetInvocationException e)
        {
            throw new InvalidOperationException($"The plugin '{name}''s entry {type.FullName} failed to construct: {e.InnerException?.Message}", e.InnerException ?? e);
        }
    }
}
