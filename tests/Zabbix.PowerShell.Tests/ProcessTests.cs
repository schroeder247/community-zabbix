// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Community.Zabbix.PowerShell.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The module as pwsh loads it: a fresh process importing the built module folder (where assembly conflicts show), and
/// the telemetry decision made at import.
/// </summary>
public sealed class ProcessTests
{
    // The first pwsh on PATH, as a shell finds it (Windows names it pwsh.exe); the Linux package's path when PATH has none.
    private static readonly string Pwsh = (Environment.GetEnvironmentVariable("PATH") ?? "")
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(folder => Path.Combine(folder, OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh"))
        .FirstOrDefault(File.Exists) ?? "/usr/bin/pwsh";

    /// <summary>
    /// The three modules staged as they ship (tools/Stage-ZbxModules.ps1, which the release runs): one folder each, the
    /// siblings without the files Community.Zabbix already carries, found by name through PSModulePath — once per test run.
    /// </summary>
    private static readonly Lazy<Task<string>> Staged = new(StageAsync);

    private static async Task<string> StageAsync()
    {
        string destination = Path.Combine(Path.GetTempPath(), $"zbx-modules-{Guid.NewGuid():N}");
        // 83 MB a run: removed when the test process ends, so runs do not fill the temporary file system
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        };
        (int exit, string output) = await PwshAsync($"""
            & '{Path.Combine(ModuleUnderTest.RepositoryRoot, "tools", "Stage-ZbxModules.ps1")}' -Configuration {ModuleUnderTest.Configuration} -Destination '{destination}' | Out-Null
            """, environment: []);
        if (exit != 0) throw new InvalidOperationException("staging the modules failed: " + output);
        return destination;
    }

    // The script's first line: the staged modules first on PSModulePath, so Import-Module <name> finds them.
    private static async Task<string> ModulePathAsync()
        => $"$env:PSModulePath = '{await Staged.Value}' + [IO.Path]::PathSeparator + $env:PSModulePath";

    [Fact]
    public async Task The_module_imports_in_a_fresh_pwsh_process()
    {
        // And, without an OTLP endpoint, never loads the OpenTelemetry SDK: an unrequested dependency cannot conflict.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        (int exit, string output) = await PwshAsync($"""
            $PSStyle.OutputRendering = 'PlainText'
            {await ModulePathAsync()}
            Import-Module Community.Zabbix -ErrorAction Stop
            (Get-Command -Module Community.Zabbix).Count
            [System.Runtime.Loader.AssemblyLoadContext]::GetLoadContext([Community.Zabbix.PowerShell.ZabbixCmdlet].Assembly).Name
            Connect-ZbxServer -Uri '{server.Endpoint}' -ErrorAction Stop
            Get-ZbxApiVersion | ForEach-Object ToString
            @([AppDomain]::CurrentDomain.GetAssemblies().GetName().Name -like 'OpenTelemetry*').Count
            @([AppDomain]::CurrentDomain.GetAssemblies().GetName().Name -match '^Community\.Zabbix\.(Agent|Proxy)$|EntityFrameworkCore').Count
            """, environment: []);

        Assert.True(exit == 0, output);
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["267", "Default", "7.0.31", "0", "0"], lines);   // 267 cmdlets, no alias; no host, listener or EF assembly
    }

    [Theory]
    [InlineData("Community.Zabbix")]
    [InlineData("Community.Zabbix.Agent")]
    [InlineData("Community.Zabbix.Proxy")]
    public async Task Every_staged_module_folder_carries_the_third_party_notices(string module)
    {
        // Each module is published alone, so each carries its own licence (with its disclaimer of warranty) and the
        // credits and the licences of the assemblies it holds.
        foreach (string file in (string[])["LICENSE", "THIRD-PARTY-NOTICES.md"])
        {
            string staged = Path.Combine(await Staged.Value, module, file);

            Assert.True(File.Exists(staged), staged);
            Assert.Equal(
                await File.ReadAllTextAsync(Path.Combine(ModuleUnderTest.RepositoryRoot, file), TestContext.Current.CancellationToken),
                await File.ReadAllTextAsync(staged, TestContext.Current.CancellationToken));
        }
    }

    [Theory]
    [InlineData("Community.Zabbix", "about_Community.Zabbix")]
    [InlineData("Community.Zabbix.Agent", "about_Community.Zabbix.Agent")]
    public async Task Get_Help_finds_a_staged_module_s_own_about_topic(string module, string topic)
    {
        // Each module ships its own about_ topics in its staged folder's en-US: the agent module's is not one of the
        // Community.Zabbix folder's, so staging keeps it.
        (int exit, string output) = await PwshAsync($"""
            $PSStyle.OutputRendering = 'PlainText'
            $ErrorActionPreference = 'Stop'
            {await ModulePathAsync()}
            Import-Module {module}
            Test-Path (Join-Path (Get-Module {module}).ModuleBase 'en-US/{topic}.help.txt')
            (Get-Help {topic} | Out-String) -match 'SHORT DESCRIPTION'
            """, environment: []);

        Assert.True(exit == 0, output);
        Assert.Equal(["True", "True"], output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    [Theory]
    [InlineData("Community.Zabbix.Agent", "Start-ZbxPassiveAgent -Name zbx -Port 0 -Confirm:$false; Stop-ZbxPassiveAgent -Name zbx -Confirm:$false", 20)]
    [InlineData("Community.Zabbix.Proxy", "New-ZbxProxyData -ProxyName zbx -Session ([guid]::NewGuid().ToString('N')) -History (New-ZbxHistoryDataEntry -ItemId 1 -Value 1) | Out-Null", 23)]
    public async Task A_sibling_module_imports_Zabbix_first_and_runs_from_its_own_folder(string module, string command, int cmdlets)
    {
        // ADR 0003: RequiredModules loads Community.Zabbix; the sibling's folder holds only what Community.Zabbix's does not, and what it
        // references from there resolves (ZabbixModuleDependencies).
        (int exit, string output) = await PwshAsync($"""
            $PSStyle.OutputRendering = 'PlainText'
            $ErrorActionPreference = 'Stop'
            {await ModulePathAsync()}
            Import-Module {module}
            (Get-Module).Name -join ','
            (Get-Command -Module {module} -CommandType Cmdlet).Count
            {command}
            'done'
            """, environment: []);

        Assert.True(exit == 0, output);
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("Community.Zabbix", lines[0].Split(','));
        Assert.Contains(module, lines[0].Split(','));
        Assert.Equal([cmdlets.ToString(System.Globalization.CultureInfo.InvariantCulture), "done"], lines[1..]);
    }

    /// <summary>
    /// <c>Microsoft.Extensions.Logging.Abstractions</c> as .NET 8's Microsoft.Extensions shipped it (assembly version
    /// 8.0.0.0): what another module built on .NET 8 carries and may load into the session first.
    /// </summary>
    private static readonly string s_olderLoggingAbstractions = typeof(ProcessTests).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false).Cast<System.Reflection.AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "OlderLoggingAbstractions").Value!;

    [Fact]
    public async Task Import_Module_Zabbix_fails_naming_the_version_when_another_module_loaded_an_older_shared_assembly_first()
    {
        // The dependency conflict the README's "Assembly isolation" names: the session's default load context
        // already holds 8.0.0.0, and the module's reference to 10.0.0.0 cannot bind to it or load beside it. This pins the
        // limitation (docs/KNOWN-LIMITATIONS.md); isolating the module in its own load context is what would turn it green.
        (int exit, string output) = await PwshAsync($$"""
            $PSStyle.OutputRendering = 'PlainText'
            {{await ModulePathAsync()}}
            Add-Type -Path '{{s_olderLoggingAbstractions}}'
            try { Import-Module Community.Zabbix -ErrorAction Stop; 'imported' } catch { $_.Exception.GetType().FullName; $_.Exception.Message }
            """, environment: []);

        Assert.True(exit == 0, output);
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal("System.IO.FileLoadException", lines[0]);
        Assert.Contains("Microsoft.Extensions.Logging.Abstractions, Version=10.0.0.0", lines[1], StringComparison.Ordinal);
    }

    private static readonly string s_olderBouncyCastle = typeof(ProcessTests).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyMetadataAttribute), false).Cast<System.Reflection.AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "OlderBouncyCastle").Value!;

    [Fact]
    public async Task Import_Module_Zabbix_refuses_an_older_BouncyCastle_release_loaded_first_under_the_same_assembly_version()
    {
        // BouncyCastle.Cryptography is 2.0.0.0 for every 2.x, so the runtime would bind the module to the older release
        // without a word; the import names both releases instead.
        (int exit, string output) = await PwshAsync($$"""
            $PSStyle.OutputRendering = 'PlainText'
            {{await ModulePathAsync()}}
            Add-Type -Path '{{s_olderBouncyCastle}}'
            try { Import-Module Community.Zabbix -ErrorAction Stop; 'imported' } catch { $_.Exception.GetType().FullName; $_.Exception.Message }
            """, environment: []);

        Assert.True(exit == 0, output);
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal("System.IO.FileLoadException", lines[0]);
        Assert.Contains("BouncyCastle.Cryptography 2.7.0", lines[1], StringComparison.Ordinal);
        Assert.Contains("already loaded BouncyCastle.Cryptography 2.4.0", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_module_built_against_an_older_shared_assembly_binds_to_Zabbix_s_copy_when_Zabbix_loaded_first()
    {
        // The reverse order, for a reference: the default context answers a request for 8.0.0.0 with the 10.0.0.0 it
        // already holds. The consumer is compiled in a process of its own, so that compiling it loads nothing here.
        string consumer = Path.Combine(Path.GetTempPath(), $"zbx-older-consumer-{Guid.NewGuid():N}.dll");
        try
        {
            (int built, string compiler) = await PwshAsync($$"""
                Add-Type -IgnoreWarnings -OutputAssembly '{{consumer}}' -ReferencedAssemblies '{{s_olderLoggingAbstractions}}' -TypeDefinition 'public static class OlderConsumer { public static string Probe() { return typeof(Microsoft.Extensions.Logging.Abstractions.NullLogger).Assembly.GetName().Version.ToString(); } }' 3>$null
                """, environment: []);
            Assert.True(built == 0 && File.Exists(consumer), compiler);

            (int exit, string output) = await PwshAsync($$"""
                $PSStyle.OutputRendering = 'PlainText'
                $ErrorActionPreference = 'Stop'
                {{await ModulePathAsync()}}
                Import-Module Community.Zabbix
                Add-Type -Path '{{consumer}}'
                ([OlderConsumer].Assembly.GetReferencedAssemblies() | Where-Object Name -eq 'Microsoft.Extensions.Logging.Abstractions').Version.ToString()
                [OlderConsumer]::Probe()
                """, environment: []);

            Assert.True(exit == 0, output);
            string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.Equal(["8.0.0.0", "10.0.0.0"], lines);
        }
        finally
        {
            File.Delete(consumer);
        }
    }

    [Fact]
    public async Task Loading_an_older_shared_assembly_by_path_after_Zabbix_fails_as_already_loaded()
    {
        // What a module that lists its own copy in RequiredAssemblies does at its import: a load by path, which the default
        // context refuses once a copy of that name is loaded, whatever the versions. The module's copy is loaded first by
        // a command that logs (the getter builds its loggers before it connects; port 1 refuses at once), and the list of
        // loaded assemblies is printed, so the scenario never depends on when the import itself happens to load it.
        (int exit, string output) = await PwshAsync($$"""
            $PSStyle.OutputRendering = 'PlainText'
            {{await ModulePathAsync()}}
            Import-Module Community.Zabbix -ErrorAction Stop
            Get-ZbxAgentItem -Agent 127.0.0.1:1 -Key agent.ping -Timeout 1 -ErrorAction SilentlyContinue -WarningAction SilentlyContinue | Out-Null
            ([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.GetName().Name -eq 'Microsoft.Extensions.Logging.Abstractions' } | ForEach-Object { $_.GetName().Version }) -join ','
            try { Add-Type -Path '{{s_olderLoggingAbstractions}}' -ErrorAction Stop; 'loaded' } catch { $_.Exception.GetType().FullName; $_.Exception.Message }
            """, environment: []);

        Assert.True(exit == 0, output);
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal("10.0.0.0", lines[0]);
        Assert.Equal("System.IO.FileLoadException", lines[1]);
        Assert.Contains("Version=8.0.0.0", lines[2], StringComparison.Ordinal);
        Assert.Contains("Assembly with same name is already loaded", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Both_sibling_modules_import_into_one_session_and_each_runs_its_commands()
    {
        (int exit, string output) = await PwshAsync($$"""
            $PSStyle.OutputRendering = 'PlainText'
            $ErrorActionPreference = 'Stop'
            {{await ModulePathAsync()}}
            Import-Module Community.Zabbix.Agent
            Import-Module Community.Zabbix.Proxy
            ((Get-Module).Name | Sort-Object) -join ','
            Start-ZbxPassiveAgent -Name zbx -Port 0 -Confirm:$false | Out-Null; Stop-ZbxPassiveAgent -Name zbx -Confirm:$false
            New-ZbxProxyData -ProxyName zbx -Session ([guid]::NewGuid().ToString('N')) -History (New-ZbxHistoryDataEntry -ItemId 1 -Value 1) | Out-Null
            'done'
            """, environment: []);

        Assert.True(exit == 0, output);
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["Community.Zabbix,Community.Zabbix.Agent,Community.Zabbix.Proxy", "done"], lines.Where(line => !line.StartsWith("Microsoft.PowerShell", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Remove_Module_Zabbix_refuses_while_a_sibling_is_imported_and_leaves_both_working()
    {
        // ADR 0003: PowerShell's own rule for a required module; nothing in ZabbixModule.OnRemove runs.
        (int exit, string output) = await PwshAsync($$"""
            $PSStyle.OutputRendering = 'PlainText'
            {{await ModulePathAsync()}}
            Import-Module Community.Zabbix.Agent -ErrorAction Stop
            Start-ZbxPassiveAgent -Name zbx -Port 0 -Confirm:$false -ErrorAction Stop | Out-Null
            try { Remove-Module Community.Zabbix -ErrorAction Stop; 'removed' } catch { $_.FullyQualifiedErrorId }
            ((Get-Module).Name | Where-Object { $_ -like 'Community.Zabbix*' } | Sort-Object) -join ','
            Stop-ZbxPassiveAgent -Name zbx -Confirm:$false -ErrorAction Stop
            'stopped'
            """, environment: []);

        Assert.True(exit == 0, output);
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["Modules_ModuleIsRequired,Microsoft.PowerShell.Commands.RemoveModuleCommand", "Community.Zabbix,Community.Zabbix.Agent", "stopped"], lines);
    }

    [Fact]
    public async Task Remove_Module_Zabbix_Force_with_a_sibling_imported_closes_the_context_and_Zabbix_imports_again()
    {
        // -Force overrides the rule: Community.Zabbix's OnRemove closes the whole context, so the sibling's session is stopped with
        // it; the sibling stays imported, and importing Community.Zabbix again gives it a fresh context.
        (int exit, string output) = await PwshAsync($$"""
            $PSStyle.OutputRendering = 'PlainText'
            {{await ModulePathAsync()}}
            Import-Module Community.Zabbix.Agent -ErrorAction Stop
            Start-ZbxPassiveAgent -Name zbx -Port 0 -Confirm:$false -ErrorAction Stop | Out-Null
            Remove-Module Community.Zabbix -Force -ErrorAction Stop
            ((Get-Module).Name | Where-Object { $_ -like 'Community.Zabbix*' } | Sort-Object) -join ','
            try { Stop-ZbxPassiveAgent -Name zbx -Confirm:$false -ErrorAction Stop; 'still running' } catch { $_.Exception.GetType().Name }
            Import-Module Community.Zabbix -ErrorAction Stop
            ((Get-Module).Name | Where-Object { $_ -like 'Community.Zabbix*' } | Sort-Object) -join ','
            """, environment: []);

        Assert.True(exit == 0, output);
        string[] lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["Community.Zabbix.Agent", "ItemNotFoundException", "Community.Zabbix,Community.Zabbix.Agent"], lines);
    }

    [Fact]
    public async Task Otlp_export_follows_the_endpoint_set_at_import()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        string script = $"""
            {await ModulePathAsync()}
            Import-Module Community.Zabbix -ErrorAction Stop
            Connect-ZbxServer -Uri '{server.Endpoint}' -ApiToken (ConvertTo-SecureString 't' -AsPlainText -Force) -ErrorAction Stop
            Get-ZbxApiVersion | Out-Null
            Disconnect-ZbxServer
            'done'
            """;

        (int silentExit, string silent) = await PwshAsync(script, environment: []);
        Assert.True(silentExit == 0, silent);
        Assert.Empty(server.OtlpPaths);

        (int exit, string output) = await PwshAsync(script, environment: new()
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = server.BaseAddress.ToString().TrimEnd('/'),
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
        });

        // Disconnect-ZbxServer disposed the connection's container, which flushes every exporter before the process exits.
        Assert.True(exit == 0, output);
        Assert.Contains("traces", server.OtlpPaths);
        Assert.Contains("metrics", server.OtlpPaths);
        Assert.Contains("logs", server.OtlpPaths);
    }

    [Fact]
    public async Task Two_connections_export_each_span_once()
    {
        // A connection must not build its own OpenTelemetry SDK, whose tracer provider listens process-wide: with two
        // connections open every span would be exported twice. One process-wide set of exporters is leased per connection.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        string script = $"""
            {await ModulePathAsync()}
            Import-Module Community.Zabbix -ErrorAction Stop
            Connect-ZbxServer -Uri '{server.Endpoint}' -ApiToken (ConvertTo-SecureString 't' -AsPlainText -Force) -Name a -ErrorAction Stop
            Connect-ZbxServer -Uri '{server.Endpoint}' -ApiToken (ConvertTo-SecureString 't' -AsPlainText -Force) -Name b -NoDefault -ErrorAction Stop
            Get-ZbxApiVersion -Connection a | Out-Null
            Disconnect-ZbxServer -Name b
            Disconnect-ZbxServer -Name a
            'done'
            """;

        (int exit, string output) = await PwshAsync(script, environment: new()
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = server.BaseAddress.ToString().TrimEnd('/'),
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
        });

        Assert.True(exit == 0, output);
        byte[] name = System.Text.Encoding.UTF8.GetBytes("Get-ZbxApiVersion");
        int exported = server.OtlpBodies.Where(entry => entry.Signal == "traces").Sum(entry => Occurrences(entry.Body, name));
        Assert.True(exported >= 1, "the command's span was not exported");
        Assert.True(exported <= 2, $"the command's span name was exported {exported} times");   // the span's name and its command tag, once
    }

    [Fact]
    public async Task Otlp_export_carries_a_command_with_no_connection()
    {
        // A connection-less command (Get-ZbxAgentItem, Send-ZbxData -Server) holds its own lease on the exporters, so its
        // logs and spans are exported with no Connect-ZbxServer in the process.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        string script = $"""
            {await ModulePathAsync()}
            Import-Module Community.Zabbix -ErrorAction Stop
            Get-ZbxAgentItem -Agent 127.0.0.1:1 -Key agent.ping -Timeout 1 -ErrorAction SilentlyContinue
            'done'
            """;

        (int exit, string output) = await PwshAsync(script, environment: new()
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = server.BaseAddress.ToString().TrimEnd('/'),
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
        });

        Assert.True(exit == 0, output);
        Assert.Contains("traces", server.OtlpPaths);
        byte[] narrative = System.Text.Encoding.UTF8.GetBytes("could not be asked");
        Assert.Contains(server.OtlpBodies, entry => entry.Signal == "logs" && Occurrences(entry.Body, narrative) > 0);
    }

    [Fact]
    public async Task Otlp_log_export_starts_at_information_unless_configured()
    {
        // The exported level is configuration (Logging__LogLevel__Default), not code: the per-call Debug events
        // ("JSON-RPC apiinfo.version #1 ← result in …") stay home by default.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        string script = $"""
            {await ModulePathAsync()}
            Import-Module Community.Zabbix -ErrorAction Stop
            Connect-ZbxServer -Uri '{server.Endpoint}' -ApiToken (ConvertTo-SecureString 't' -AsPlainText -Force) -ErrorAction Stop
            Get-ZbxApiVersion | Out-Null
            Disconnect-ZbxServer
            'done'
            """;
        var environment = new Dictionary<string, string>
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = server.BaseAddress.ToString().TrimEnd('/'),
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
        };
        byte[] debug = System.Text.Encoding.UTF8.GetBytes("result in");

        (int exit, string output) = await PwshAsync(script, environment);
        Assert.True(exit == 0, output);
        Assert.DoesNotContain(server.OtlpBodies, entry => entry.Signal == "logs" && Occurrences(entry.Body, debug) > 0);

        environment["Logging__LogLevel__Default"] = "Debug";
        (exit, output) = await PwshAsync(script, environment);
        Assert.True(exit == 0, output);
        Assert.Contains(server.OtlpBodies, entry => entry.Signal == "logs" && Occurrences(entry.Body, debug) > 0);
    }

    private static int Occurrences(byte[] body, byte[] value)
    {
        int count = 0;
        for (int at = body.AsSpan().IndexOf(value); at >= 0; at = body.AsSpan(at + 1).IndexOf(value) is int next and >= 0 ? at + 1 + next : -1) count++;
        return count;
    }

    [Fact]
    public async Task Flushing_a_connections_exporters_ends_when_an_exporter_never_answers()
    {
        // Flush bounds each provider's ForceFlush(): its default timeout is Timeout.Infinite, and it runs on the thread
        // that drops a connection's last lease (the pipeline thread of Disconnect-ZbxServer, or a closing runspace), so
        // an OTLP collector that accepted the connection and never answered would hold the prompt for ever.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(new UnansweredExport()));
        using ServiceProvider provider = services.BuildServiceProvider();
        // Not disposed itself: its Dispose flushes once more (another FlushDeadline against this exporter); the container
        // it wraps is the test's, disposed above.
#pragma warning disable CA2000
        var exporters = new ZabbixModuleTelemetry.Exporters(provider);
#pragma warning restore CA2000

        await Task.Run(exporters.Flush, TestContext.Current.CancellationToken)
            .WaitAsync(ZabbixModuleTelemetry.s_flushDeadline * 4, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_three_providers_flush_within_one_shared_deadline()
    {
        // The flushes share one deadline: each provider is given what the previous one left, so a collector that never
        // answers holds Disconnect-ZbxServer for one deadline, not three.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddProcessor(new UnansweredExport()))
            .WithMetrics(metrics => metrics.AddReader(new UnansweredReader()))
            .WithLogging(logging => logging.AddProcessor(new UnansweredLogExport()));
        using ServiceProvider provider = services.BuildServiceProvider();
        TimeSpan deadline = TimeSpan.FromSeconds(2);
#pragma warning disable CA2000 // the container it wraps is the test's, disposed above; its own Dispose would flush again
        var exporters = new ZabbixModuleTelemetry.Exporters(provider, deadline);
#pragma warning restore CA2000

        var elapsed = Stopwatch.StartNew();
        await Task.Run(exporters.Flush, TestContext.Current.CancellationToken).WaitAsync(deadline * 4, TestContext.Current.CancellationToken);

        Assert.True(elapsed.Elapsed < deadline * 2, $"the flush took {elapsed.Elapsed}, more than one deadline ({deadline}) and its slack");
    }

    // A metric collection that never completes, as UnansweredExport's flush.
    private sealed class UnansweredReader : MetricReader
    {
        protected override bool OnCollect(int timeoutMilliseconds)
        {
#pragma warning disable RS0030 // the double holds the flush for exactly the time the flush allows: time is what it fakes
            Thread.Sleep(timeoutMilliseconds);
#pragma warning restore RS0030
            return false;
        }
    }

    // A log export that never completes, as UnansweredExport's flush.
    private sealed class UnansweredLogExport : OpenTelemetry.BaseProcessor<LogRecord>
    {
        protected override bool OnForceFlush(int timeoutMilliseconds)
        {
#pragma warning disable RS0030 // the double holds the flush for exactly the time the flush allows: time is what it fakes
            Thread.Sleep(timeoutMilliseconds);
#pragma warning restore RS0030
            return false;
        }
    }

    // An export that never completes: it holds a flush for exactly as long as the flush allows (Infinite: for ever).
    private sealed class UnansweredExport : OpenTelemetry.BaseProcessor<Activity>
    {
        protected override bool OnForceFlush(int timeoutMilliseconds)
        {
#pragma warning disable RS0030 // the double holds the flush for exactly the time the flush allows: time is what it fakes
            Thread.Sleep(timeoutMilliseconds);
#pragma warning restore RS0030
            return false;
        }
    }

    [Fact]
    public async Task A_command_leaves_a_span_and_a_duration_sample()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);
        ZabbixConnection connection = shell.Context.Current!;
        var spans = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ZabbixPowerShellTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span => { lock (spans) spans.Add(span); },
        };
        ActivitySource.AddActivityListener(listener);
        using var durations = new MetricCollector<double>(connection.Services.GetRequiredService<IMeterFactory>(),
            ZabbixPowerShellTelemetry.SourceName, ZabbixPowerShellTelemetry.CommandDuration);

        server.On("host.get", "[]");
        shell.Invoke("Get-ZbxHost");
        server.On("host.get", FakeZabbix.Error(-32602, "Invalid params.", "Incorrect method."));
        shell.Invoke("Get-ZbxHost");

        // The listener is process-wide and tests run in parallel: this test's spans are the ones on its server's port.
        Activity[] mine = [.. spans.ToArray().Where(span => Equals(span.GetTagItem("server.port"), server.Endpoint.Port) && span.OperationName == "Get-ZbxHost")];
        Activity ok = Assert.Single(mine, span => span.Status == ActivityStatusCode.Ok);
        Assert.Equal("Get-ZbxHost", ok.GetTagItem(ZabbixPowerShellTelemetry.CommandTag));
        Assert.NotNull(Assert.Single(mine, span => span.Status == ActivityStatusCode.Error).GetTagItem("error.type"));
        IReadOnlyList<CollectedMeasurement<double>> samples = durations.GetMeasurementSnapshot();
        Assert.Equal(2, samples.Count);
        Assert.False(samples[0].Tags.ContainsKey("error.type"));
        Assert.True(samples[1].Tags.ContainsKey("error.type"));
    }

    [Fact]
    public async Task Otlp_export_fires_around_a_session_s_own_lifecycle_spans()
    {
        // A session (Start-ZbxActiveProxy, an agent/watch/discovery session) owns its own ZabbixPowerShellTelemetry
        // instance rather than the connection's, but StartSessionPhase's span is on the same static, process-wide
        // ActivitySource (ZabbixPowerShellTelemetry.SourceName) all the same, so the module's OTLP export (which
        // subscribes to the "Zabbix.*" wildcard) carries it exactly like a command's own
        // — a fresh process, like the sibling test above, so the OTEL_* env vars this reads are isolated from every
        // other test. The session's target (port 1) never needs to be reachable: StartSessionPhase/FinishSessionPhase
        // run synchronously in Start-ZbxActiveProxy, before the session's own background pull ever gets a reply.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        string script = $"""
            {await ModulePathAsync()}
            Import-Module Community.Zabbix.Proxy -ErrorAction Stop
            Connect-ZbxServer -Uri '{server.Endpoint}' -ApiToken (ConvertTo-SecureString 't' -AsPlainText -Force) -ErrorAction Stop
            Start-ZbxActiveProxy probe zbx-proxy '127.0.0.1:1' -Confirm:$false -ErrorAction Stop | Out-Null
            Stop-ZbxActiveProxy probe -Confirm:$false
            Disconnect-ZbxServer
            'done'
            """;

        (int exit, string output) = await PwshAsync(script, environment: new()
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = server.BaseAddress.ToString().TrimEnd('/'),
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
        });

        Assert.True(exit == 0, output);
        Assert.Contains("traces", server.OtlpPaths);
    }

    private static async Task<(int Exit, string Output)> PwshAsync(string script, Dictionary<string, string> environment)
    {
        var start = new ProcessStartInfo(Pwsh, ["-NoProfile", "-NonInteractive", "-Command", "-"])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.Environment.Remove("OTEL_EXPORTER_OTLP_ENDPOINT");
        start.Environment.Remove("PSExecutionPolicyPreference");   // pwsh's own default, as a user's session has it
        start.Environment["TERM"] = "dumb";
        foreach ((string name, string value) in environment) start.Environment[name] = value;
        using Process process = Process.Start(start)!;
        await process.StandardInput.WriteAsync(script);
        process.StandardInput.Close();
        Task<string> output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            await process.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            // Not left running past its test: the test fails naming the script.
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"pwsh did not exit within 2 minutes: {script}");
        }
        return (process.ExitCode, await output + await error);
    }
}
