// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The connection layer the listener modules carry once per platform (src/Zabbix.PowerShell.AspNetCoreTransport.targets):
/// each platform's folder holds that platform's ReadyToRun images, the module folder's root holds none of them, and the
/// process-wide resolver answers from the running platform's folder only, for what it holds at a version at least the
/// requested one. The resolver tests load into a collectible context of their own, so nothing reaches the test
/// process's default context (which binds the shared framework's own copies).
/// </summary>
public sealed class ZabbixModulePlatformAssembliesTests : IDisposable
{
    private const string Carried = "Microsoft.Extensions.Features";

    private static readonly string[] s_assemblies =
    [
        "Microsoft.AspNetCore.Connections.Abstractions", "Microsoft.AspNetCore.Hosting.Abstractions",
        "Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets", Carried,
    ];

    // The machine a platform's image names: the processor's, combined with the operating system's ReadyToRun value
    // (none for Windows).
    private static readonly Dictionary<string, ushort> s_processors = new(StringComparer.Ordinal)
    {
        ["x64"] = 0x8664,
        ["x86"] = 0x014C,
        ["arm64"] = 0xAA64,
        ["arm"] = 0x01C4,
    };

    private static readonly Dictionary<string, ushort> s_systems = new(StringComparer.Ordinal)
    {
        ["win"] = 0,
        ["linux"] = 0x7B79,
        ["osx"] = 0x4644,
    };

    private static readonly string[] s_platforms = ["linux-arm", "linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-arm64", "win-x64", "win-x86"];

    private readonly AssemblyLoadContext _context = new("zbx-platform", isCollectible: true);

    public static TheoryData<string> ListenerModules => [ModuleUnderTest.Agent.Name, ModuleUnderTest.Proxy.Name];

    [Theory]
    [MemberData(nameof(ListenerModules))]
    public void Each_platform_folder_of_a_listener_module_holds_that_platforms_images(string module)
    {
        string runtimes = Path.Combine(ModuleUnderTest.Named(module).BuildDirectory, "runtimes");

        Assert.Equal(s_platforms, Directory.GetDirectories(runtimes).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (string platform in s_platforms)
        {
            string folder = Path.Combine(runtimes, platform, "lib", "net10.0");
            string[] parts = platform.Split('-');
            ushort machine = (ushort)(s_processors[parts[1]] ^ s_systems[parts[0]]);

            Assert.Equal(s_assemblies, Directory.GetFiles(folder).Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal));
            foreach (string file in Directory.GetFiles(folder))
            {
                using var image = new PEReader(File.OpenRead(file));
                Assert.True((ushort)image.PEHeaders.CoffHeader.Machine == machine, $"{file}: machine 0x{(ushort)image.PEHeaders.CoffHeader.Machine:X4}, expected 0x{machine:X4}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(ListenerModules))]
    public void A_listener_module_folder_carries_no_platform_image_beside_its_assembly(string module)
    {
        // A copy at the root would be one platform's, refused on every other (BadImageFormatException).
        foreach (string name in s_assemblies)
            Assert.False(File.Exists(Path.Combine(ModuleUnderTest.Named(module).BuildDirectory, name + ".dll")), name);
    }

    [Fact]
    public void The_platform_this_process_runs_on_is_one_the_listener_modules_carry()
    {
        Assert.Contains(ZabbixModulePlatformAssemblies.Platform, s_platforms);
    }

    [Theory]
    [InlineData("win", Architecture.X64, "win-x64")]
    [InlineData("win", Architecture.X86, "win-x86")]
    [InlineData("linux", Architecture.Arm, "linux-arm")]
    [InlineData("linux", Architecture.Arm64, "linux-arm64")]
    [InlineData("osx", Architecture.Arm64, "osx-arm64")]
    [InlineData("linux", Architecture.S390x, null)]
    [InlineData(null, Architecture.X64, null)]
    public void A_platform_is_named_as_its_runtime_pack(string? system, Architecture processor, string? platform)
    {
        Assert.Equal(platform, ZabbixModulePlatformAssemblies.PlatformOf(system, processor));
    }

    [Fact]
    public void The_running_platforms_folder_serves_and_loads_every_carried_assembly()
    {
        string folder = PlatformFolder();
        var resolver = new ZabbixModulePlatformAssemblies(folder);

        foreach (string name in s_assemblies)
            Assert.Equal(Path.Combine(folder, name + ".dll"), resolver.Resolve(_context, new AssemblyName(name))?.Location);
    }

    [Fact]
    public void A_carried_assembly_requested_at_a_higher_version_than_the_carried_one_is_not_served()
    {
        string folder = PlatformFolder();
        Version carried = AssemblyName.GetAssemblyName(Path.Combine(folder, Carried + ".dll")).Version!;

        Assert.Null(new ZabbixModulePlatformAssemblies(folder).Resolve(_context, new AssemblyName($"{Carried}, Version={carried.Major + 1}.0.0.0")));
    }

    [Theory]
    [InlineData("Contoso.Unrelated")]
    [InlineData("Community.Zabbix.Proxy")]
    public void A_name_the_platforms_folder_does_not_hold_is_not_served(string name)
    {
        // The module assembly's own folder is not the platform's: what sits beside it is not answered here.
        Assert.Null(new ZabbixModulePlatformAssemblies(PlatformFolder()).Resolve(_context, new AssemblyName(name)));
    }

    [Fact]
    public void A_folder_that_does_not_exist_serves_nothing()
    {
        string missing = Path.Combine(ModuleUnderTest.Agent.BuildDirectory, "runtimes", "freebsd-x64", "lib", "net10.0");

        Assert.Null(new ZabbixModulePlatformAssemblies(missing).Resolve(_context, new AssemblyName(Carried)));
    }

    public void Dispose() => _context.Unload();

    private static string PlatformFolder()
        => Path.Combine(ModuleUnderTest.Agent.BuildDirectory, "runtimes", ZabbixModulePlatformAssemblies.Platform!, "lib", "net10.0");
}
