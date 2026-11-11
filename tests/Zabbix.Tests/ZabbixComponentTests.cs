// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The lifetime of an agent or proxy (docs/adr/0010-agent-proxy-client-classes.md): constructed, set in any order, then
/// started; a start that fails stops what started; a stop is bounded and runs every phase; disposal drains; and a host runs
/// the same class phase by phase.
/// </summary>
public sealed class ZabbixComponentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_built_agent_delivers_what_the_same_agent_delivers_in_a_host()
    {
        var (hostPort, hostReceived) = MockTrapper.Collect(2);
        var (directPort, directReceived) = MockTrapper.Collect(2);
        using IHost host = await TestHost.StartAsync(services => services.AddZabbixPushAgent("edge").AddSource(_ => TwoValues()).AddServer(new DnsEndPoint("127.0.0.1", hostPort)), Ct);

        await using ZabbixPushAgent direct = ZabbixPushAgent.CreateBuilder("edge").AddSource(_ => TwoValues()).AddServer(new DnsEndPoint("127.0.0.1", directPort)).Build();
        await direct.StartAsync(Ct);

        Assert.Equal(2, await hostReceived.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.Equal(2, await directReceived.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        await host.StopAsync(Ct);
        await direct.StopAsync(Ct);
        Assert.Equal(host.Services.GetRequiredKeyedService<ZabbixPushAgent>("edge").Status.Delivered, direct.Status.Delivered);
    }

    [Fact]
    public async Task A_constructed_passive_agent_answers_a_client_named_by_host_and_port()
    {
        var options = new ZabbixPassiveAgentOptions { Hostname = "edge-01", Address = "127.0.0.1", Port = 0 };
        options.AllowedPeers.Add("127.0.0.1");
        await using var agent = new ZabbixPassiveAgent(options);
        agent.Items.Add("custom.answer", _ => "42");
        await agent.StartAsync(Ct);
        var bound = (IPEndPoint)agent.Listener!.LocalEndPoint!;
        using var client = new ZabbixGetter($"127.0.0.1:{bound.Port}");

        Assert.Equal("edge-01", (await client.GetAsync("agent.hostname", Ct)).Value);
        Assert.Equal("42", (await client.GetAsync("custom.answer", Ct)).Value);
    }

    [Fact]
    public async Task What_a_component_needs_can_be_set_in_any_order_after_it_is_constructed()
    {
        await using var agent = new ZabbixPassiveAgent();
        agent.Items.Add("custom.answer", _ => "42");
        agent.Options.Port = 0;
        agent.Options.AllowedPeers.Add("127.0.0.1");
        agent.Options.Hostname = "edge-01";
        agent.Options.Address = "127.0.0.1";

        await agent.StartAsync(Ct);

        Assert.Equal("edge-01", agent.Name);
        Assert.Equal("1", (await agent.Items.AnswerAsync(ZabbixItemKey.Parse("agent.ping", null), TimeSpan.FromSeconds(3), Ct)).Value);
    }

    [Fact]
    public async Task A_start_with_what_is_missing_says_so_and_can_be_called_again()
    {
        await using var agent = new ZabbixPushAgent();

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => agent.StartAsync(Ct));

        Assert.Contains("ServerActive", refused.Message, StringComparison.Ordinal);
        Assert.IsType<Microsoft.Extensions.Options.OptionsValidationException>(refused.InnerException);
        agent.Options.ServerActive = "127.0.0.1:9";
        await agent.StartAsync(Ct);                                                      // nothing started the first time
    }

    [Fact]
    public async Task A_proxy_says_it_needs_a_name()
    {
        await using var proxy = new ZabbixActiveProxy();

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.StartAsync(Ct));

        Assert.Contains("Options.Name", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_passive_proxy_says_it_needs_the_server_s_version()
    {
        await using var proxy = new ZabbixPassiveProxy(new ZabbixProxyOptions { Name = "proxy-01" });

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() => proxy.StartAsync(Ct));

        Assert.Contains("Version", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Start_and_stop_run_every_phase_in_order()
    {
        var events = new List<string>();
        await using var component = new Probe(events);

        await component.StartAsync(Ct);
        await component.StopAsync(Ct);

        Assert.Equal(["starting", "start", "started", "stopping", "stop", "stopped"], events);
    }

    [Fact]
    public async Task A_host_runs_each_phase_of_the_same_class()
    {
        var events = new List<string>();
        await using var component = new Probe(events);
        var hosted = new Hosting.ZabbixHostedComponent(component);

        await hosted.StartingAsync(Ct);
        await hosted.StartAsync(Ct);
        await hosted.StartedAsync(Ct);
        await hosted.StoppingAsync(Ct);
        await hosted.StopAsync(Ct);
        await hosted.StoppedAsync(Ct);

        Assert.Equal(["starting", "start", "started", "stopping", "stop", "stopped"], events);
    }

    [Fact]
    public async Task A_failed_start_stops_what_started_and_throws_the_start_s_failure()
    {
        var events = new List<string>();
        var logs = new FakeLogCollector();
        using ILoggerFactory loggers = LoggerFactory.Create(logging => logging.AddProvider(new FakeLoggerProvider(logs)).SetMinimumLevel(LogLevel.Debug));
        await using var component = new Probe(events, failAt: ["start", "stop"], loggers: loggers);

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() => component.StartAsync(Ct));
        await component.StopAsync(Ct);                                                   // not running: nothing more to stop

        Assert.Equal("failed at start", failure.Message);                                // the stop's failure is logged, not thrown
        Assert.Equal(["starting", "start", "stopping", "stop", "stopped"], events);
        FakeLogRecord rollback = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 3302);
        Assert.Equal(LogLevel.Error, rollback.Level);
        Assert.Equal("failed at stop", rollback.Exception?.Message);
    }

    [Fact]
    public async Task A_component_that_fails_to_stop_while_it_is_disposed_logs_the_failure_and_still_releases()
    {
        var logs = new FakeLogCollector();
        using ILoggerFactory loggers = LoggerFactory.Create(logging => logging.AddProvider(new FakeLoggerProvider(logs)).SetMinimumLevel(LogLevel.Debug));
        var component = new Probe([], failAt: ["stop"], loggers: loggers);
        await component.StartAsync(Ct);

        await component.DisposeAsync();                                                  // does not throw

        Assert.True(component.Released);
        FakeLogRecord stop = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 3305);
        Assert.Equal(LogLevel.Error, stop.Level);
    }

    [Fact]
    public async Task Every_phase_runs_when_another_fails_and_the_failures_are_thrown_together()
    {
        var events = new List<string>();
        await using var one = new Probe(events, failAt: ["stopping"]);
        await one.StartAsync(Ct);

        InvalidOperationException single = await Assert.ThrowsAsync<InvalidOperationException>(() => one.StopAsync(Ct));

        Assert.Equal("failed at stopping", single.Message);
        Assert.Contains("stop", events);
        Assert.Contains("stopped", events);

        await using var two = new Probe([], failAt: ["stopping", "stop"]);
        await two.StartAsync(Ct);

        AggregateException both = await Assert.ThrowsAsync<AggregateException>(() => two.StopAsync(Ct));

        Assert.Equal(["failed at stopping", "failed at stop"], both.InnerExceptions.Select(inner => inner.Message));
    }

    [Fact]
    public async Task A_stop_cancels_the_token_its_phases_hold_when_the_stop_timeout_passes()
    {
        var time = new FakeTimeProvider();
        await using var component = new Probe([], time: time, holdStop: true) { StopTimeout = TimeSpan.FromSeconds(5) };
        await component.StartAsync(Ct);

        Task stopping = component.StopAsync(Ct);
        Assert.False(stopping.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping.WaitAsync(TimeSpan.FromSeconds(10), Ct));
        Assert.True(component.StopCancelled);
    }

    [Fact]
    public async Task Disposal_stops_a_running_component_once_and_a_disposed_component_does_not_start()
    {
        var events = new List<string>();
        var component = new Probe(events);
        await component.StartAsync(Ct);

        await component.DisposeAsync();
        await component.DisposeAsync();

        Assert.Equal(["starting", "start", "started", "stopping", "stop", "stopped"], events);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => component.StartAsync(Ct));
    }

    [Fact]
    public async Task A_synchronous_disposal_releases_at_once_without_a_stop()
    {
        var events = new List<string>();
        var component = new Probe(events);
        await component.StartAsync(Ct);
        events.Clear();

#pragma warning disable CA1849 // the synchronous disposal is what this test is about
        component.Dispose();
#pragma warning restore CA1849

        Assert.Empty(events);
        Assert.True(component.Released);
    }

    [Fact]
    public async Task A_component_starts_once()
    {
        await using var component = new Probe([]);
        await component.StartAsync(Ct);

        await Assert.ThrowsAsync<InvalidOperationException>(() => component.StartAsync(Ct));
    }

    [Fact]
    public async Task Run_starts_waits_for_the_token_and_stops()
    {
        var events = new List<string>();
        await using var component = new Probe(events);
        using var stopping = new CancellationTokenSource();

        Task running = component.RunAsync(stopping.Token);
        Assert.False(running.IsCompleted);
        Assert.Contains("started", events);
        await stopping.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        Assert.Contains("stopped", events);
    }

    [Fact]
    public async Task Run_ends_when_the_component_is_stopped_elsewhere()
    {
        await using var component = new Probe([]);

        Task running = component.RunAsync(Ct);
        await component.StopAsync(Ct);

        await running.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task The_caller_s_logging_stays_the_caller_s()
    {
        using var loggers = new TrackedLoggerFactory();
        var agent = new ZabbixPushAgent(new ZabbixPushAgentOptions { ServerActive = "127.0.0.1:9" }, loggers, null, null);
        await agent.StartAsync(Ct);

        await agent.DisposeAsync();

        Assert.False(loggers.Disposed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public async Task A_stop_timeout_that_is_neither_positive_nor_infinite_is_refused(int seconds)
    {
        await using var component = new Probe([]);

        Assert.Throws<ArgumentOutOfRangeException>(() => component.StopTimeout = TimeSpan.FromSeconds(seconds));
        component.StopTimeout = Timeout.InfiniteTimeSpan;

        Assert.Equal(Timeout.InfiniteTimeSpan, component.StopTimeout);
    }

    [Fact]
    public async Task A_derived_agent_keeps_what_it_derives_from()
    {
        await using var agent = new DerivedAgent();

        Assert.Equal("web-01", agent.Options.Hostname);
        agent.Items.Add("app.version", _ => "2.4.1");
    }

    private sealed class DerivedAgent : ZabbixActiveAgent
    {
        public DerivedAgent()
            : base(new ZabbixActiveAgentOptions { ServerActive = "zbx-server-01", Hostname = "web-01" })
        {
        }
    }

    private static async IAsyncEnumerable<ZabbixValue> TwoValues([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ZabbixValue("web01", "a", "1", DateTimeOffset.UnixEpoch);
        yield return new ZabbixValue("web01", "b", "2", DateTimeOffset.UnixEpoch);
        await UntilCancelled(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static async Task UntilCancelled(CancellationToken cancellationToken)
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => cancelled.TrySetCanceled(cancellationToken)))
            await cancelled.Task;
    }

    /// <summary>Records each phase, and fails at the phases named after recording them.</summary>
    private sealed class Probe(List<string> events, string[]? failAt = null, ILoggerFactory? loggers = null, TimeProvider? time = null, bool holdStop = false)
        : ZabbixComponent("probe", loggers, time)
    {
        public bool Released { get; private set; }

        public bool StopCancelled { get; private set; }

        private protected override string DefaultName => "probe";

        protected override async ValueTask DisposeAsyncCore()
        {
            await base.DisposeAsyncCore();
            Released = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) Released = true;
            base.Dispose(disposing);
        }

        private protected override void CheckReady()
        {
        }

        private protected override Task OnStartingAsync(CancellationToken cancellationToken) => Note("starting");

        private protected override Task OnStartAsync(CancellationToken cancellationToken) => Note("start");

        private protected override Task OnStartedAsync(CancellationToken cancellationToken) => Note("started");

        private protected override Task OnStoppingAsync(CancellationToken cancellationToken) => Note("stopping");

        private protected override async Task OnStopAsync(CancellationToken cancellationToken)
        {
            await Note("stop");
            if (!holdStop) return;
            try
            {
                await UntilCancelled(cancellationToken);
            }
            finally
            {
                StopCancelled = cancellationToken.IsCancellationRequested;
            }
        }

        private protected override Task OnStoppedAsync(CancellationToken cancellationToken) => Note("stopped");

        private Task Note(string phase)
        {
            lock (events) events.Add(phase);
            return failAt?.Contains(phase) == true ? Task.FromException(new InvalidOperationException($"failed at {phase}")) : Task.CompletedTask;
        }
    }

    private sealed class TrackedLoggerFactory : ILoggerFactory
    {
        public bool Disposed { get; private set; }

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public ILogger CreateLogger(string categoryName) => NullLogger.Instance;

        public void Dispose() => Disposed = true;
    }
}
