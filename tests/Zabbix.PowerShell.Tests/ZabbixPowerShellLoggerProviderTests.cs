// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.PowerShell.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The one provider behind a connection's small unrouted queue and a proxy session's own ring-buffer logger: with no command running on this asynchronous flow, every level is buffered for a
/// <c>Get-Zbx&lt;Session&gt;Log</c> cmdlet to review, and Warning+ also waits in a small escalation queue for the next
/// command in the runspace to <see cref="ZabbixPowerShellLoggerProvider" />'s internal <c>Flush</c> onto — exactly like a
/// completion failure already does.
/// </summary>
public sealed class ZabbixPowerShellLoggerProviderTests
{
    [Fact]
    public void With_no_command_running_every_level_is_buffered_for_a_session_log_cmdlet_to_review()
    {
        var time = new FakeTimeProvider();
        using var provider = new ZabbixPowerShellLoggerProvider(time);
        ILogger logger = provider.CreateLogger("Community.Zabbix.Tests.Probe");

        logger.LogTrace("trace-line");
        logger.LogWarning("warning-line");

        IReadOnlyList<ZabbixSessionLogEntry> snapshot = provider.Snapshot();
        Assert.Contains(snapshot, entry => entry.Message == "trace-line" && entry.Level == LogLevel.Trace);
        Assert.Contains(snapshot, entry => entry.Message == "warning-line" && entry.Level == LogLevel.Warning);
    }

    [Fact]
    public void Clear_empties_the_buffer_a_Drain_switch_uses()
    {
        using var provider = new ZabbixPowerShellLoggerProvider();
        ILogger logger = provider.CreateLogger("Community.Zabbix.Tests.Probe");
        logger.LogInformation("info-line");
        Assert.NotEmpty(provider.Snapshot());

        provider.Clear();

        Assert.Empty(provider.Snapshot());
    }

    [Fact]
    public void A_buffered_Warning_is_not_delivered_until_the_next_Flush_and_then_it_is_gone()
    {
        using var provider = new ZabbixPowerShellLoggerProvider();
        ILogger logger = provider.CreateLogger("Community.Zabbix.Tests.Probe");
        logger.LogWarning(new EventId(1), "warning-line");
        using var pipeline = new ZabbixPipeline(verbose: true, debug: true, information: true, warning: true);

        provider.Flush(pipeline);
        pipeline.Complete();

        LogWrite write = Assert.IsType<LogWrite>(Assert.Single(pipeline.Drain(TestContext.Current.CancellationToken)));
        Assert.Equal("warning-line", write.Message);
        Assert.Equal(LogLevel.Warning, write.Level);
    }

    [Fact]
    public void A_buffered_Information_line_never_escalates_it_stays_for_the_log_cmdlet_only()
    {
        using var provider = new ZabbixPowerShellLoggerProvider();
        ILogger logger = provider.CreateLogger("Community.Zabbix.Tests.Probe");
        logger.LogInformation("info-line");
        using var pipeline = new ZabbixPipeline(verbose: true, debug: true, information: true, warning: true);

        provider.Flush(pipeline);
        pipeline.Complete();

        Assert.Empty(pipeline.Drain(TestContext.Current.CancellationToken));
        Assert.Contains(provider.Snapshot(), entry => entry.Message == "info-line");
    }

    [Fact]
    public void A_record_logged_while_a_command_s_pipeline_is_current_routes_live_and_is_never_buffered()
    {
        using var provider = new ZabbixPowerShellLoggerProvider();
        ILogger logger = provider.CreateLogger("Community.Zabbix.Tests.Probe");
        using var pipeline = new ZabbixPipeline(verbose: true, debug: true, information: true, warning: true);
        pipeline.Enter();

        logger.LogTrace("trace-line");
        pipeline.Complete();

        LogWrite write = Assert.IsType<LogWrite>(Assert.Single(pipeline.Drain(TestContext.Current.CancellationToken)));
        Assert.Equal("trace-line", write.Message);
        Assert.Empty(provider.Snapshot());
    }
}
