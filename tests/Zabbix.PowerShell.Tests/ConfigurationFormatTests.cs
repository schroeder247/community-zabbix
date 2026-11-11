// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// A configuration document file is in the format its extension names unless <c>-Format</c> names it, and the two
/// disagreeing is refused (<c>ZabbixConfigurationFormatMismatch</c>); otherwise <c>Export-ZbxConfiguration -Path x.yaml</c> would write
/// JSON into a file named YAML.
/// </summary>
public sealed class ConfigurationFormatTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("zbx-format-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Theory]
    [InlineData("export.yaml", "", "yaml")]
    [InlineData("export.YML", "", "yaml")]
    [InlineData("export.xml", "", "xml")]
    [InlineData("export.json", "", "json")]
    [InlineData("export.txt", "", "json")]
    [InlineData("export.txt", " -Format Xml", "xml")]
    [InlineData("export.yaml", " -Format Yaml", "yaml")]
    public async Task Export_writes_the_format_the_file_or_Format_names(string file, string format, string wire)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("configuration.export", parameters => JsonSerializer.Serialize(parameters.GetProperty("format").GetString() switch
        {
            "yaml" => "zabbix_export: {}",
            "xml" => "<zabbix_export/>",
            _ => "{\"zabbix_export\":{}}",
        }));
        using Shell shell = Shell.Open();
        shell.Connect(server);
        string path = Path.Combine(_directory, file);

        Run run = shell.Invoke($"Export-ZbxConfiguration -HostReference 1 -LiteralPath $path{format}", new Dictionary<string, object?> { ["path"] = path });

        Assert.Empty(run.Errors);
        Assert.Equal(wire, Assert.Single(server.ParamsOf("configuration.export")).GetProperty("format").GetString());
    }

    [Fact]
    public async Task Export_refuses_a_Format_the_file_s_extension_contradicts_and_writes_nothing()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);
        string path = Path.Combine(_directory, "export.yaml");

        Run run = shell.Invoke("Export-ZbxConfiguration -HostReference 1 -LiteralPath $path -Format Json", new Dictionary<string, object?> { ["path"] = path });

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.StartsWith("ZabbixConfigurationFormatMismatch,", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Contains("Yaml", error.Exception.Message, StringComparison.Ordinal);
        Assert.Empty(server.ParamsOf("configuration.export"));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData("doc.yml", "zabbix_export: {}", "", "yaml")]
    [InlineData("doc.xml", "<zabbix_export/>", "", "xml")]
    [InlineData("doc.txt", "<zabbix_export/>", "", "xml")]
    [InlineData("doc.txt", "{\"zabbix_export\":{}}", "", "json")]
    [InlineData("doc.txt", "zabbix_export: {}", " -Format Yaml", "yaml")]
    public async Task Import_and_Compare_read_the_format_the_file_or_Format_names(string file, string text, string format, string wire)
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("configuration.import", "true").On("configuration.importcompare", "{}");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        string path = Path.Combine(_directory, file);
        await File.WriteAllTextAsync(path, text, TestContext.Current.CancellationToken);

        Run run = shell.Invoke($"Import-ZbxConfiguration -LiteralPath $path -Rules @{{}} -Confirm:$false{format}; $null = Compare-ZbxConfiguration -LiteralPath $path -Rules @{{}}{format}", new Dictionary<string, object?> { ["path"] = path });

        Assert.Empty(run.Errors);
        Assert.Equal(wire, Assert.Single(server.ParamsOf("configuration.import")).GetProperty("format").GetString());
        Assert.Equal(wire, Assert.Single(server.ParamsOf("configuration.importcompare")).GetProperty("format").GetString());
    }

    [Fact]
    public async Task Import_refuses_a_file_whose_extension_contradicts_Format_and_imports_the_others()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("configuration.import", "true");
        using Shell shell = Shell.Open();
        shell.Connect(server);
        await File.WriteAllTextAsync(Path.Combine(_directory, "a.yaml"), "zabbix_export: {}", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_directory, "b.json"), "{\"zabbix_export\":{}}", TestContext.Current.CancellationToken);

        Run run = shell.Invoke("Import-ZbxConfiguration -Path (Join-Path $dir '*') -Format Json -Rules @{} -Confirm:$false", new Dictionary<string, object?> { ["dir"] = _directory });

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.StartsWith("ZabbixConfigurationFormatMismatch,", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Equal("json", Assert.Single(server.ParamsOf("configuration.import")).GetProperty("format").GetString());
    }

    [Fact]
    public void Test_reports_a_Format_the_file_s_extension_contradicts()
    {
        string path = Path.Combine(_directory, "doc.yaml");
        File.WriteAllText(path, "zabbix_export: {}");
        using Shell shell = Shell.Open();

        Run run = shell.Invoke("Test-ZbxConfiguration -LiteralPath $path; Test-ZbxConfiguration -LiteralPath $path -Format Json", new Dictionary<string, object?> { ["path"] = path });

        Assert.Empty(run.Errors);
        Assert.Equal([true, false], run.Output.Select(o => (bool)o.Properties["IsValid"].Value));
        Assert.Equal("yaml", run.Output[0].Properties["Format"].Value);
    }
}
