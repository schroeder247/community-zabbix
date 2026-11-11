// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>One <c>ILoggerProvider</c> to the host's streams, written from the pipeline thread.</summary>
public sealed class LoggingTests
{
    [Fact]
    public async Task Each_log_level_reaches_its_own_stream()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("$DebugPreference = 'Continue'; Invoke-ZbxTestLog -Verbose -InformationAction Continue");

        Assert.Contains("trace-line", run.Verbose);
        Assert.Contains("debug-line", run.Debug);
        InformationRecord information = Assert.Single(run.Information, record => Equals(record.MessageData, "info-line"));
        Assert.Contains("Zabbix", information.Tags);
        Assert.Contains("Community.Zabbix.Tests.Probe", information.Tags);
        Assert.Contains("warning-line", run.Warnings);
        ErrorRecord error = Assert.Single(run.Errors);
        Assert.StartsWith("ZabbixLog.ErrorLine,", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Equal(ErrorCategory.InvalidResult, error.CategoryInfo.Category);
        Assert.Equal("error-line", error.ErrorDetails.Message);
    }

    [Fact]
    public async Task Levels_the_preferences_hide_are_not_written()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Invoke-ZbxTestLog -WarningAction SilentlyContinue");

        Assert.Empty(run.Verbose);
        Assert.Empty(run.Debug);
        Assert.Empty(run.Warnings);
        Assert.Single(run.Errors);
    }

    [Fact]
    public async Task A_failure_that_was_logged_and_thrown_is_one_error_record()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Invoke-ZbxTestLog -Fail");

        ErrorRecord error = Assert.Single(run.Errors);
        Assert.StartsWith("ZabbixWire.InvalidFrame,", error.FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.IsType<InvalidDataException>(error.Exception);
    }

    [Fact]
    public async Task A_resilience_attempt_log_never_becomes_the_terminating_error_the_thrown_failure_does()
    {
        // With -ErrorAction Stop the first error record terminates, so Polly's Error-level attempt record
        // ("Execution attempt. ... Attempt: '3'") must not be written before the operation's own exception.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("try { Invoke-ZbxTestLog -Resilience -ErrorAction Stop } catch { $_.Exception.Message }");
        Run verbose = shell.Invoke("try { Invoke-ZbxTestLog -Resilience -ErrorAction Stop -Verbose } catch { $_.Exception.Message }");

        Assert.Equal("the operation's own failure", Assert.Single(run.Output).BaseObject);
        Assert.Empty(run.Warnings);                                         // a try is verbose, not a warning
        Assert.Contains(verbose.Verbose, line => line.StartsWith("Execution attempt.", StringComparison.Ordinal));
        Assert.Empty(verbose.Warnings);
    }

    [Fact]
    public async Task Connecting_reports_the_server_on_the_information_stream()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();

        Run run = shell.Connect(server, "-InformationAction Continue");

        Assert.Contains(run.Information, record => record.Tags.Contains("Connected") && $"{record.MessageData}".Contains("7.0.31", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sdk_logs_of_two_runspaces_reach_their_own_commands()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell a = Shell.Open();
        using Shell b = Shell.Open();
        a.Connect(server);
        b.Connect(server);

        Task<Run> first = Task.Run(() => a.Invoke("$DebugPreference = 'Continue'; Invoke-ZbxTestLog"));
        Task<Run> second = Task.Run(() => b.Invoke("Invoke-ZbxTestLog"));
        Run[] runs = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);

        Assert.Equal(["debug-line"], runs[0].Debug);
        Assert.Empty(runs[1].Debug);
    }
}
