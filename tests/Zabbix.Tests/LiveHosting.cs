// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Tests;

/// <summary>
/// A live scenario's components run the way an application runs them: a generic host, built from the registrations,
/// started and stopped through its own lifecycle (not by calling the hosted services one by one), logging into the
/// test's output so a failure shows what the component saw.
/// </summary>
internal static class LiveHosting
{
    /// <summary>
    /// Where a listener the server must reach binds (<c>ZABBIX_TEST_LISTEN_ADDRESS</c>): every address by default. Measured
    /// on rootless podman 5 (pasta and the kube-play bridge alike): a pod reaches <c>host.containers.internal</c>
    /// (169.254.1.2) only on a listener bound to <c>0.0.0.0</c>, never one bound to <c>127.0.0.1</c>, and the listener
    /// sees the host's LAN address as the peer, so the tests' peer lists are <c>0.0.0.0/0</c>, for the seconds a
    /// scenario runs.
    /// </summary>
    public static string ListenAddress { get; } = Environment.GetEnvironmentVariable("ZABBIX_TEST_LISTEN_ADDRESS") is { Length: > 0 } address ? address : "0.0.0.0";

    /// <summary>The components' log level in the test output (<c>ZABBIX_TEST_LOG_LEVEL</c>, <c>Information</c> by default).</summary>
    public static LogLevel Level { get; } = Enum.TryParse(Environment.GetEnvironmentVariable("ZABBIX_TEST_LOG_LEVEL"), ignoreCase: true, out LogLevel level) ? level : LogLevel.Information;

    /// <summary>Builds and starts a host with <paramref name="configure"/>'s registrations.</summary>
    public static async Task<IHost> StartAsync(Action<IServiceCollection> configure)
    {
        HostApplicationBuilder builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = Environments.Development });
        builder.Services.AddLogging(logging => logging.AddProvider(new TestOutputLoggerProvider()).SetMinimumLevel(Level));
        builder.Services.AddMetrics();
        configure(builder.Services);
        IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        return host;
    }

    /// <summary>Stops and disposes <paramref name="host"/>: the components' own drain runs.</summary>
    public static async Task ShutdownAsync(this IHost host)
    {
        await host.StopAsync(CancellationToken.None);
        host.Dispose();
    }

    private sealed class TestOutputLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new TestOutputLogger(categoryName[(categoryName.LastIndexOf('.') + 1)..]);

        public void Dispose()
        {
        }
    }

    private sealed class TestOutputLogger(string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= Level;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            try { TestContext.Current.TestOutputHelper?.WriteLine($"{logLevel} {category}[{eventId.Id}] {formatter(state, exception)}{(exception is null ? "" : " " + exception.GetType().Name + ": " + exception.Message)}"); }
            catch (InvalidOperationException) { }                                            // the test already finished
        }
    }
}
