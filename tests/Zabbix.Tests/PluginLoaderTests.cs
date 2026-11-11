// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Community.Zabbix.Tests;

/// <summary>
/// Plugins loaded from a folder (docs/adr/0008-item-providers-and-plugins.md), compiled here from source: only listed
/// plugins load, each in a context of its own, so two plugins can carry two versions of one dependency; the contract
/// is the agent's whatever a plugin's folder holds; and a plugin that cannot be loaded fails the registration before
/// anything of it runs.
/// </summary>
public sealed class PluginLoaderTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("zbx-plugins-");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        // Windows keeps a loaded plugin's file open; the folder is a temp folder.
        try { _root.Delete(recursive: true); }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    [Fact]
    public async Task Two_plugins_carry_two_versions_of_one_dependency_and_each_answers_with_its_own()
    {
        Plugin("Alpha", "plugin.alpha", dependencyVersion: 1);
        Plugin("Beta", "plugin.beta", dependencyVersion: 2);
        var agent = new Agent();

        agent.AddPlugins(new ZabbixPluginOptions { Path = _root.FullName, Load = { "Alpha", "Beta" } });

        using ServiceProvider services = agent.Services.BuildServiceProvider();
        ZabbixItemProvider[] providers = [.. services.GetKeyedServices<ZabbixItemProvider>(new ZabbixItemsKey(agent.Name))];
        Assert.Equal("dependency 1", await Answer(providers[0], "plugin.alpha"));
        Assert.Equal("dependency 2", await Answer(providers[1], "plugin.beta"));
        Assert.Equal("Zabbix plugin Alpha", AssemblyLoadContext.GetLoadContext(providers[0].GetType().Assembly)!.Name);
    }

    [Fact]
    public void The_contract_is_the_agent_s_even_when_a_plugin_s_folder_carries_its_own_copy()
    {
        string folder = Plugin("Copied", "plugin.copied", dependencyVersion: 1);
        File.Copy(typeof(ZabbixItemProvider).Assembly.Location, Path.Combine(folder, "Community.Zabbix.Abstractions.dll"));
        OwnerOnly(folder);
        var agent = new Agent();

        agent.AddPlugins(new ZabbixPluginOptions { Path = _root.FullName, Load = { "Copied" } });

        using ServiceProvider services = agent.Services.BuildServiceProvider();
        ZabbixItemProvider provider = Assert.Single(services.GetKeyedServices<ZabbixItemProvider>(new ZabbixItemsKey(agent.Name)));
        Assert.Same(typeof(ZabbixItemProvider), provider.GetType().BaseType);
    }

    [Fact]
    public async Task A_passive_agent_answers_a_plugin_s_key_with_the_settings_from_its_section()
    {
        Plugin("Alpha", "plugin.alpha", dependencyVersion: 1);
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Plugins:Path"] = _root.FullName,
            ["Plugins:Load"] = "Alpha",                                          // a comma-separated list
            ["Plugins:Alpha:Prefix"] = "configured ",
        }).Build();

        using IHost host = await TestHost.StartAsync(services => services.AddZabbixPassiveAgent("edge")
            .Configure(o => { o.Address = "127.0.0.1"; o.Port = 0; o.AllowedPeers.Add("127.0.0.1"); })
            .AddPlugins(configuration.GetSection("Plugins")), Ct);
        var bound = (IPEndPoint)host.Services.GetRequiredKeyedService<ZabbixPassiveAgent>("edge").Listener!.LocalEndPoint!;
        using var getter = new ZabbixGetter($"127.0.0.1:{bound.Port}");

        Assert.Equal("configured dependency 1", (await getter.GetAsync("plugin.alpha", Ct)).Value);
    }

    [Fact]
    public void A_configuration_lists_its_plugins_as_an_array_too()
    {
        Plugin("Alpha", "plugin.alpha", dependencyVersion: 1);
        Plugin("Beta", "plugin.beta", dependencyVersion: 2);
        IConfiguration section = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Path"] = _root.FullName,
            ["Load:0"] = "Alpha",
            ["Load:1"] = "Beta",
        }).Build();
        var agent = new Agent();

        agent.AddPlugins(section);

        using ServiceProvider services = agent.Services.BuildServiceProvider();
        Assert.Equal(2, services.GetKeyedServices<ZabbixItemProvider>(new ZabbixItemsKey(agent.Name)).Count());
    }

    [Fact]
    public void A_plugin_nothing_lists_is_never_opened()
    {
        Plugin("Alpha", "plugin.alpha", dependencyVersion: 1);
        Plugin("Unlisted", "plugin.unlisted", dependencyVersion: 1);
        var agent = new Agent();

        agent.AddPlugins(new ZabbixPluginOptions { Path = _root.FullName, Load = { "Alpha" } });

        Assert.DoesNotContain(AssemblyLoadContext.All, context => context.Name == "Zabbix plugin Unlisted");
    }

    [Fact]
    public void A_listed_plugin_that_is_missing_fails_naming_where_it_was_looked_for()
    {
        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => new Agent().AddPlugins(new ZabbixPluginOptions { Path = _root.FullName, Load = { "Missing" } }));

        Assert.Contains(Path.Combine(_root.FullName, "Missing", "Missing.dll"), failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plugin_that_names_no_entry_fails()
    {
        Compile(Path.Combine(_root.FullName, "Bare"), "Bare", "namespace Bare; public static class Nothing { }", References());
        OwnerOnly(_root.FullName);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => new Agent().AddPlugins(new ZabbixPluginOptions { Path = _root.FullName, Load = { "Bare" } }));

        Assert.Contains("names no entry", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plugin_built_against_a_later_major_contract_is_refused_before_it_runs()
    {
        // A contract assembly of the same name at 99.0, referenced in place of the agent's.
        string future = Compile(Path.Combine(_root.FullName, ".contract"), "Community.Zabbix.Abstractions",
            "[assembly: System.Reflection.AssemblyVersion(\"99.0.0.0\")] namespace Community.Zabbix.Future; public sealed class Marker { }", References());
        Compile(Path.Combine(_root.FullName, "Future"), "Future", "namespace Future; public static class Uses { public static System.Type Type => typeof(Community.Zabbix.Future.Marker); }",
            [.. References().Where(reference => Path.GetFileName(reference.Display) != "Community.Zabbix.Abstractions.dll"), MetadataReference.CreateFromFile(future)]);
        OwnerOnly(_root.FullName);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => new Agent().AddPlugins(new ZabbixPluginOptions { Path = _root.FullName, Load = { "Future" } }));

        Assert.Contains("later major version", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plugin_whose_files_other_accounts_can_write_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows says who may write with access lists, not a file mode.");
        string folder = Plugin("Open", "plugin.open", dependencyVersion: 1);
        string assembly = Path.Combine(folder, "Open.dll");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(assembly, File.GetUnixFileMode(assembly) | UnixFileMode.GroupWrite);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => new Agent().AddPlugins(new ZabbixPluginOptions { Path = _root.FullName, Load = { "Open" } }));

        Assert.Contains("writable by accounts other than its owner", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plugin_under_a_folder_other_accounts_can_write_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows says who may write with access lists, not a file mode.");
        Plugin("Under", "plugin.under", dependencyVersion: 1);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_root.FullName, File.GetUnixFileMode(_root.FullName) | UnixFileMode.OtherWrite);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => new Agent().AddPlugins(new ZabbixPluginOptions { Path = _root.FullName, Load = { "Under" } }));

        Assert.Contains("a folder above it", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_plugin_whose_entry_needs_a_missing_dependency_fails_naming_the_plugin()
    {
        string folder = Path.Combine(_root.FullName, "Broken");
        string dependency = Compile(folder, "Dependency", "using Community.Zabbix; namespace Dependency; public abstract class Base : ZabbixPluginEntry { }", References());
        Compile(folder, "Broken", """
            using Microsoft.Extensions.Configuration;
            using Community.Zabbix;

            [assembly: ZabbixPlugin(typeof(Broken.Entry))]

            namespace Broken;

            public sealed class Entry : Dependency.Base
            {
                public override void Register(IZabbixItemsBuilder items, IConfiguration settings) { }
            }
            """, [.. References(), MetadataReference.CreateFromFile(dependency)]);
        File.Delete(dependency);
        OwnerOnly(folder);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(
            () => new Agent().AddPlugins(new ZabbixPluginOptions { Path = _root.FullName, Load = { "Broken" } }));

        Assert.Contains("The plugin 'Broken' could not be loaded", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("../Alpha", "not a plugin name")]
    [InlineData("Alpha,Alpha", "listed twice")]
    public void A_list_that_names_a_path_or_one_plugin_twice_is_refused(string load, string reason)
    {
        Plugin("Alpha", "plugin.alpha", dependencyVersion: 1);
        var options = new ZabbixPluginOptions { Path = _root.FullName };
        foreach (string name in load.Split(',')) options.Load.Add(name);

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() => new Agent().AddPlugins(options));

        Assert.Contains(reason, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_list_without_a_path_is_refused_and_an_empty_list_loads_nothing()
    {
        Assert.Throws<InvalidOperationException>(() => new Agent().AddPlugins(new ZabbixPluginOptions { Load = { "Alpha" } }));

        var agent = new Agent();
        agent.AddPlugins(new ZabbixPluginOptions());
        Assert.Empty(agent.Services);
    }

    private static async Task<string?> Answer(ZabbixItemProvider provider, string key)
        => (await provider.GetValueAsync(ZabbixItemKey.Parse(key, null), Ct)).Value;

    // A plugin folder as dotnet publish leaves it: the plugin's assembly and its dependency, Dependency.dll, at the
    // version given, which answers "dependency <version>".
    private string Plugin(string name, string key, int dependencyVersion)
    {
        string folder = Path.Combine(_root.FullName, name);
        string dependency = Compile(folder, "Dependency",
            $"[assembly: System.Reflection.AssemblyVersion(\"{dependencyVersion}.0.0.0\")] namespace Dependency; public static class Version {{ public static string Text => \"dependency {dependencyVersion}\"; }}",
            References());
        Compile(folder, name, $$"""
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.Configuration;
            using Community.Zabbix;
            using Community.Zabbix;
            using Community.Zabbix.Protocol;

            [assembly: ZabbixPlugin(typeof({{name}}.Entry))]

            namespace {{name}};

            public sealed class Entry : ZabbixPluginEntry
            {
                public override void Register(IZabbixItemsBuilder items, IConfiguration settings) => items.AddItems(new Keys(settings["Prefix"] ?? ""));
            }

            public sealed class Keys(string prefix) : ZabbixItemProvider
            {
                public override IReadOnlyList<ZabbixItemDefinition> Items { get; } = [new("{{key}}", "the dependency's version")];

                public override ValueTask<ZabbixAgentAnswer> GetValueAsync(ZabbixItemKey key, CancellationToken cancellationToken)
                    => ValueTask.FromResult(ZabbixAgentAnswer.Supported(key, prefix + Dependency.Version.Text));
            }
            """, [.. References(), MetadataReference.CreateFromFile(dependency)]);
        OwnerOnly(folder);
        return folder;
    }

    private static string Compile(string folder, string name, string source, IEnumerable<MetadataReference> references)
    {
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, name + ".dll");
        CSharpCompilation compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))],
            references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
        EmitResult result = compilation.Emit(path);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        return path;
    }

    // The test process's own assemblies: the framework, the SDK and Microsoft.Extensions, as a plugin is built against them.
    private static List<MetadataReference> References()
    {
        string platform = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        return [.. platform.Split(Path.PathSeparator)
            .Where(path => !Path.GetFileName(path).StartsWith("Dependency", StringComparison.Ordinal))
            .DistinctBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .Select(path => MetadataReference.CreateFromFile(path))];
    }

    // The modes a plugin folder is deployed with, whatever the process's umask: writable by its owner only.
    private static void OwnerOnly(string folder)
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (string path in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Prepend(folder))
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) & ~(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
    }

    private sealed class Agent : IZabbixItemsBuilder
    {
        public string Name => "edge";

        public IServiceCollection Services { get; } = new ServiceCollection();
    }
}
