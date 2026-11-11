// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.PowerShell.Diagnostics;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// The module logs exactly as an application on the SDK's policy does: a connection's container and the loggers of a
/// session or a connection-less command render a classified event the way an application with the logger's defaults and
/// the SDK's redactors (<c>SetZabbixRedactors</c>) renders it
/// (a bare key keeps its name, a key's parameters are masked, a peer's text reads <c>[redacted]</c>). A composition that
/// drifts from the SDK's fails here, not in an operator's log.
/// </summary>
public sealed class ModuleRedactionTests
{
    private const string Expected = "Agent agent:10050 refused the request for item key \"system.uptime\": [redacted]";
    private const string ExpectedMasked = "Agent agent:10050 refused the request for item key \"web.page.get[***]\": [redacted]";
    private const string PeerInfo = "Unsupported item key \"web.page.get[intranet,/admin,user:hunter2]\".";

    private static void Log(ILoggerFactory loggers)
    {
        ILogger logger = loggers.CreateLogger("Community.Zabbix.Tests.Probe");
        WireLog.GetterRefused(logger, "agent:10050", ZabbixItemKey.Parse("system.uptime"), PeerInfo);
        WireLog.GetterRefused(logger, "agent:10050", ZabbixItemKey.Parse("web.page.get[intranet,/admin,user:hunter2]"), PeerInfo);
    }

    private static string[] Messages(ZabbixPowerShellLoggerProvider logs)
        => [.. logs.Snapshot().Where(entry => entry.Category == "Community.Zabbix.Tests.Probe").Select(entry => entry.Message)];

    [Fact]
    public void The_sdk_s_policy_alone_renders_the_event_as_the_module_must()
    {
        using var logs = new ZabbixPowerShellLoggerProvider();
        using ILoggerFactory loggers = LoggerFactory.Create(logging =>
        {
            logging.ClearProviders().SetMinimumLevel(LogLevel.Trace).EnableRedaction().AddProvider(logs);
            logging.Services.AddRedaction(redaction => redaction.SetZabbixRedactors());
        });

        Log(loggers);

        Assert.Equal([Expected, ExpectedMasked], Messages(logs));
    }

    [Fact]
    public void A_connection_s_container_renders_the_event_as_the_sdk_s_policy_does()
    {
        using ZabbixConnection connection = ZabbixConnection.Open("probe", new Uri("http://127.0.0.1:9/api_jsonrpc.php"), new ZabbixApiTokenCredential("t"),
            exportTelemetry: false, allowUnencryptedCredentials: true);

        Log(connection.Services.GetRequiredService<ILoggerFactory>());

        Assert.Equal([Expected, ExpectedMasked], Messages(connection.Logs));
    }

    [Fact]
    public async Task A_session_s_container_renders_the_event_as_the_sdk_s_policy_does()
    {
        await using Sessions.ZabbixSessionHost session = await Sessions.ZabbixSessionHost.StartAsync("Probe", "probe", TimeProvider.System,
            (_, _) => new Sessions.ZabbixSessionComponent(_ => Task.CompletedTask, _ => Task.CompletedTask, () => ValueTask.CompletedTask), TestContext.Current.CancellationToken);

        Log(session.Loggers);

        Assert.Equal([Expected, ExpectedMasked], Messages(session.Logs));
    }

    [Fact]
    public void A_command_s_loggers_render_the_event_as_the_sdk_s_policy_does()
    {
        using var logs = new ZabbixPowerShellLoggerProvider();
        using ILoggerFactory loggers = ZabbixModuleTelemetry.CreateLoggers(logs);

        Log(loggers);

        Assert.Equal([Expected, ExpectedMasked], Messages(logs));
    }
}
