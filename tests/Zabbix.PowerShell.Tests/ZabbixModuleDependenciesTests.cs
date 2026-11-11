// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Runtime.Loader;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The process-wide resolver the module Community.Zabbix lends its folder through: it sees every unresolved reference of every
/// module in the session, so it answers only for an assembly of its own closure, at a version at least the requested
/// one. Each test runs it over a copy of the module folder with a stray assembly added, and loads into a collectible
/// context of its own, so nothing reaches the test process's default context.
/// </summary>
public sealed class ZabbixModuleDependenciesTests : IDisposable
{
    private const string Shared = "Polly.Core";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"zbx-deps-{Guid.NewGuid():N}");
    private readonly AssemblyLoadContext _context = new("zbx-deps", isCollectible: true);
    private readonly ZabbixModuleDependencies _resolver;
    private readonly Version _shipped;

    public ZabbixModuleDependenciesTests()
    {
        // The module assembly, its .deps.json (the closure), one shared assembly of the closure, and an assembly the
        // closure does not name, dropped beside them (a sibling's assembly).
        Directory.CreateDirectory(_folder);
        string module = ModuleUnderTest.Zabbix.BuildDirectory;
        string assembly = ModuleUnderTest.Zabbix.AssemblyName;
        foreach (string file in new[] { assembly + ".dll", assembly + ".deps.json", Shared + ".dll" })
            File.Copy(Path.Combine(module, file), Path.Combine(_folder, file));
        File.Copy(ModuleUnderTest.Agent.Assembly.Location, Path.Combine(_folder, ModuleUnderTest.Agent.AssemblyName + ".dll"));
        _resolver = new ZabbixModuleDependencies(Path.Combine(_folder, assembly + ".dll"));
        _shipped = AssemblyName.GetAssemblyName(Path.Combine(_folder, Shared + ".dll")).Version!;
    }

    public static TheoryData<string> AtMostShipped => ["", ", Version=0.0.0.0", ", Version=shipped"];

    [Theory]
    [MemberData(nameof(AtMostShipped))]
    public void A_shared_assembly_requested_at_or_below_the_shipped_version_is_served_from_the_module_folder(string version)
    {
        var requested = new AssemblyName(Shared + version.Replace("shipped", _shipped.ToString(), StringComparison.Ordinal));

        Assembly? served = _resolver.Resolve(_context, requested);

        // By the folder's own unique name: the temporary path is spelled differently once loaded (macOS's /var is a
        // link to /private/var, Windows may shorten a folder to its 8.3 name).
        Assert.EndsWith(Path.Combine(Path.GetFileName(_folder), Shared + ".dll"), served?.Location, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shared_assembly_requested_at_a_higher_version_than_the_shipped_one_is_not_served()
    {
        var requested = new AssemblyName($"{Shared}, Version={_shipped.Major + 1}.0.0.0");

        Assert.Null(_resolver.Resolve(_context, requested));
    }

    [Fact]
    public void An_assembly_in_the_folder_that_the_module_closure_does_not_name_is_not_served()
    {
        Assert.Null(_resolver.Resolve(_context, new AssemblyName(ModuleUnderTest.Agent.AssemblyName)));
    }

    [Fact]
    public void A_name_the_folder_does_not_hold_is_not_served()
    {
        Assert.Null(_resolver.Resolve(_context, new AssemblyName("Contoso.Unrelated")));
    }

    public void Dispose()
    {
        _context.Unload();
        // The unloaded context's files may still be mapped until it is collected; the folder is a temp folder.
        try { Directory.Delete(_folder, recursive: true); }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }
}
