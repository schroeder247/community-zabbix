// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// An agent session loads plugins (<c>-Plugin</c>, <c>-PluginPath</c>, <c>-PluginSetting</c>) as an application's
/// <c>AddPlugins</c> does, and answers their keys beside its scriptblock handlers (docs/adr/0008-item-providers-and-plugins.md).
/// </summary>
public sealed class AgentPluginCommandsTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("zbx-ps-plugins-");

    public void Dispose()
    {
        // Windows keeps a loaded plugin's file open; the folder is a temp folder.
        try { _root.Delete(recursive: true); }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    [Fact]
    public void A_passive_session_answers_a_plugin_s_key_with_its_settings()
    {
        Plugin("Queue");
        using Shell shell = Shell.Open();
        try
        {
            Run started = shell.Invoke($"Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Plugin Queue -PluginPath '{_root.FullName}' " +
                "-PluginSetting @{ Queue = @{ Prefix = 'ps-' } } -Confirm:$false");
            Assert.Empty(started.Errors);

            Run answered = shell.Invoke("Test-ZbxAgentItem 'plugin.queue[orders]' -Session probe");

            Assert.Empty(answered.Errors);
            Assert.Equal("ps-orders", Assert.Single(answered.Output).Properties["Value"].Value);
        }
        finally
        {
            shell.Invoke("Stop-ZbxPassiveAgent probe -Confirm:$false");
        }
    }

    [Fact]
    public void A_plugin_without_a_path_is_an_error_and_starts_nothing()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Plugin Queue -Confirm:$false; Get-ZbxPassiveAgentStatus");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.StartsWith("ZabbixPluginPathMissing,", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Empty(run.Output);
    }

    [Fact]
    public void A_listed_plugin_that_is_missing_fails_the_start_and_starts_nothing()
    {
        using Shell shell = Shell.Open();

        Run run = shell.Invoke($"Start-ZbxPassiveAgent probe web-01 127.0.0.1 -Port 0 -Plugin Missing -PluginPath '{_root.FullName}' -Confirm:$false; Get-ZbxPassiveAgentStatus");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.Contains("Missing.dll", error.Exception.Message, StringComparison.Ordinal);
        Assert.Empty(run.Output);
    }

    // A plugin as dotnet publish leaves it: its assembly, whose provider answers plugin.queue[x] with its Prefix
    // setting and x.
    private void Plugin(string name)
    {
        string folder = Path.Combine(_root.FullName, name);
        Directory.CreateDirectory(folder);
        string source = $$"""
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Microsoft.Extensions.Configuration;
            using Community.Zabbix;
            using Community.Zabbix.Hosting;
            using Community.Zabbix.Protocol;

            [assembly: ZabbixPlugin(typeof({{name}}.Entry))]

            namespace {{name}};

            public sealed class Entry : ZabbixPluginEntry
            {
                public override void Register(IZabbixItemsBuilder items, IConfiguration settings) => items.AddItems(new Keys(settings["Prefix"] ?? ""));
            }

            public sealed class Keys(string prefix) : ZabbixItemProvider
            {
                public override IReadOnlyList<ZabbixItemDefinition> Items { get; } = [new("plugin.queue", "the prefix and the first parameter")];

                public override ValueTask<ZabbixAgentAnswer> GetValueAsync(ZabbixItemKey key, CancellationToken cancellationToken)
                    => ValueTask.FromResult(ZabbixAgentAnswer.Supported(key, prefix + key[0]));
            }
            """;
        string platform = (string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!;
        MetadataReference[] references = [.. platform.Split(Path.PathSeparator).DistinctBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(path => MetadataReference.CreateFromFile(path))];
        EmitResult result = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview))], references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable)).Emit(Path.Combine(folder, name + ".dll"));
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        if (OperatingSystem.IsWindows()) return;
        foreach (string path in Directory.EnumerateFileSystemEntries(folder, "*", SearchOption.AllDirectories).Prepend(folder))
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) & ~(UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
    }
}
