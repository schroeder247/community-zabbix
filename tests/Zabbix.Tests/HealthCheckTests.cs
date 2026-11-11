// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using Community.Zabbix.Hosting;
using Community.Zabbix.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// An agent's health is the host's health-check concept: store depth against the cap, what the send loop holds, the age
/// of the last delivery (or of the start, when nothing was ever delivered), the last rejection, and a faulting loop.
/// </summary>
public sealed class HealthCheckTests
{
    private static readonly HealthCheckContext Context = new();

    [Fact]
    public async Task Healthy_then_degraded_as_the_store_fills_then_unhealthy_when_nothing_leaves()
    {
        var time = new FakeTimeProvider();
        var status = new ZabbixAgentStatus("edge");
        await using var store = new ZabbixMemoryStore<ZabbixValue>(new ZabbixStorePolicy<ZabbixValue> { MaxItems = 10 });
        status.Attach(store);
        status.Started(time.GetUtcNow());
        var check = new ZabbixAgentHealthCheck(status, time) { StaleAfter = TimeSpan.FromMinutes(1) };

        status.Report(new ZabbixSendProgress(ZabbixSendOutcome.Delivered, 5, time.GetUtcNow()));
        HealthCheckResult healthy = await check.CheckHealthAsync(Context, TestContext.Current.CancellationToken);
        for (int i = 0; i < 9; i++) store.TryWrite(new ZabbixValue("h", "k", "1", time.GetUtcNow()));
        HealthCheckResult degraded = await check.CheckHealthAsync(Context, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(2));
        HealthCheckResult unhealthy = await check.CheckHealthAsync(Context, TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, healthy.Status);
        Assert.Equal(HealthStatus.Degraded, degraded.Status);
        Assert.Equal(HealthStatus.Unhealthy, unhealthy.Status);
        Assert.Equal(9, unhealthy.Data["stored"]);
    }

    [Fact]
    public async Task The_status_names_the_store_cap_with_the_policy_word_MaxItems()
    {
        var status = new ZabbixAgentStatus("edge");
        await using var store = new ZabbixMemoryStore<ZabbixValue>(new ZabbixStorePolicy<ZabbixValue> { MaxItems = 10 });
        status.Attach(store);
        var check = new ZabbixAgentHealthCheck(status, new FakeTimeProvider());

        HealthCheckResult result = await check.CheckHealthAsync(Context, TestContext.Current.CancellationToken);

        Assert.Equal(10, status.MaxItems);
        Assert.Equal(10, result.Data["maxItems"]);
        Assert.DoesNotContain(typeof(ZabbixAgentStatus).GetProperties(), property => property.Name.EndsWith("Capacity", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_agent_that_never_delivered_is_degraded_after_the_grace_from_its_start()
    {
        // An agent that has delivered nothing must not stay Healthy for ever.
        var time = new FakeTimeProvider();
        var status = new ZabbixAgentStatus("edge");
        status.Started(time.GetUtcNow());
        var check = new ZabbixAgentHealthCheck(status, time) { StaleAfter = TimeSpan.FromMinutes(5) };

        HealthCheckResult starting = await check.CheckHealthAsync(Context, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(6));
        HealthCheckResult silent = await check.CheckHealthAsync(Context, TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, starting.Status);
        Assert.Equal(HealthStatus.Degraded, silent.Status);
    }

    [Fact]
    public async Task Values_the_send_loop_holds_count_as_waiting_so_an_hour_of_holding_is_unhealthy()
    {
        // 50 values held for an hour by a loop retrying against a dead server must not report Healthy.
        var time = new FakeTimeProvider();
        var status = new ZabbixAgentStatus("edge");
        status.Started(time.GetUtcNow());
        var check = new ZabbixAgentHealthCheck(status, time);

        status.Report(new ZabbixSendProgress(ZabbixSendOutcome.Retried, 50, time.GetUtcNow()));
        HealthCheckResult retrying = await check.CheckHealthAsync(Context, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(1));
        HealthCheckResult stale = await check.CheckHealthAsync(Context, TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Degraded, retrying.Status);
        Assert.Equal(HealthStatus.Unhealthy, stale.Status);
        Assert.Equal(50, stale.Data["held"]);
    }

    [Fact]
    public async Task A_rejection_does_not_count_as_a_delivery_and_shows_as_degraded()
    {
        // A rejected batch does not move LastDelivered: otherwise an agent whose every batch is refused would stay Healthy.
        var time = new FakeTimeProvider();
        var status = new ZabbixAgentStatus("edge");
        status.Started(time.GetUtcNow());
        var check = new ZabbixAgentHealthCheck(status, time);
        DateTimeOffset delivered = time.GetUtcNow();
        status.Report(new ZabbixSendProgress(ZabbixSendOutcome.Delivered, 1, delivered));
        time.Advance(TimeSpan.FromSeconds(10));

        status.Report(new ZabbixSendProgress(ZabbixSendOutcome.Rejected, 3, time.GetUtcNow()));
        HealthCheckResult result = await check.CheckHealthAsync(Context, TestContext.Current.CancellationToken);

        Assert.Equal(delivered, status.LastDelivered);
        Assert.Equal(3, status.Rejected);
        Assert.Equal(HealthStatus.Degraded, result.Status);
    }

    [Fact]
    public async Task A_faulting_loop_makes_the_agent_unhealthy_until_its_next_good_pass()
    {
        var status = new ZabbixAgentStatus("edge");
        var check = new ZabbixAgentHealthCheck(status, new FakeTimeProvider());

        status.Faulted("refresh", new InvalidOperationException("a defect"));
        HealthCheckResult faulting = await check.CheckHealthAsync(Context, TestContext.Current.CancellationToken);
        status.Recovered("refresh");
        HealthCheckResult recovered = await check.CheckHealthAsync(Context, TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, faulting.Status);
        Assert.IsType<InvalidOperationException>(faulting.Exception);
        Assert.Equal(HealthStatus.Healthy, recovered.Status);
    }

    [Fact]
    public async Task With_health_check_registers_the_agent_on_the_host_s_health_checks()
    {
        var services = new ServiceCollection();
        services.AddZabbixPushAgent("edge").AddServer(new DnsEndPoint("127.0.0.1", 10051)).AddHealthCheck(o => o.StaleAfter = TimeSpan.FromSeconds(30));
        using ServiceProvider provider = services.BuildValidated();

        HealthReport report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

        HealthReportEntry entry = report.Entries["zabbix-agent-edge"];
        Assert.Equal(HealthStatus.Healthy, entry.Status);
        Assert.Contains("zabbix", entry.Tags);
    }

    [Fact]
    public async Task With_health_check_registers_the_active_agent_on_the_host_s_health_checks()
    {
        var services = new ServiceCollection();
        services.AddZabbixActiveAgent("edge").AddServer(new DnsEndPoint("127.0.0.1", 10051)).AddHealthCheck();
        using ServiceProvider provider = services.BuildValidated();

        HealthReport report = await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Healthy, report.Entries["zabbix-active-agent-edge"].Status);
    }

    [Fact]
    public async Task With_health_check_registers_the_passive_agent_degraded_before_it_starts_healthy_while_it_listens_and_unhealthy_after()
    {
        var services = new ServiceCollection();
        services.AddZabbixPassiveAgent("edge").AllowPeer(IPAddress.Loopback).Configure(o => { o.Address = "127.0.0.1"; o.Port = 0; }).AddHealthCheck();
        using ServiceProvider provider = services.BuildValidated();
        HealthCheckService checks = provider.GetRequiredService<HealthCheckService>();
        ZabbixPassiveAgent agent = provider.GetRequiredKeyedService<ZabbixPassiveAgent>("edge");

        HealthReportEntry before = (await checks.CheckHealthAsync(TestContext.Current.CancellationToken)).Entries["zabbix-passive-agent-edge"];
        await agent.StartAsync(TestContext.Current.CancellationToken);
        HealthReportEntry listening = (await checks.CheckHealthAsync(TestContext.Current.CancellationToken)).Entries["zabbix-passive-agent-edge"];
        await agent.StopAsync(TestContext.Current.CancellationToken);
        HealthReportEntry stopped = (await checks.CheckHealthAsync(TestContext.Current.CancellationToken)).Entries["zabbix-passive-agent-edge"];

        Assert.Equal((HealthStatus.Degraded, HealthStatus.Healthy, HealthStatus.Unhealthy), (before.Status, listening.Status, stopped.Status));
        Assert.Equal(["agent", "zabbix"], listening.Tags.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Each_agent_s_builder_adds_its_health_check_on_its_own_container_too()
    {
        static TBuilder Checked<TBuilder>(TBuilder builder) where TBuilder : IZabbixActiveAgentBuilder => builder.AddServer("127.0.0.1:10051").AddHealthCheck();
        var services = new ServiceCollection();
        Checked(services.AddZabbixActiveAgent("edge"));
        using ServiceProvider provider = services.BuildValidated();
        ZabbixActiveAgentBuilder builder = Checked(ZabbixActiveAgent.CreateBuilder("edge"));
        await using ZabbixActiveAgent agent = builder.Build();

        Assert.Contains("zabbix-active-agent-edge", (await provider.GetRequiredService<HealthCheckService>().CheckHealthAsync(TestContext.Current.CancellationToken)).Entries.Keys);
        Assert.Single(builder.Services, descriptor => descriptor.ServiceType == typeof(HealthCheckService));     // a publisher the application registers there reports it
        Assert.Throws<ArgumentNullException>(() => ZabbixPushAgent.CreateBuilder("edge").AddHealthCheck(null!));
    }

    [Fact]
    public void A_health_check_s_thresholds_are_checked_where_they_are_set()
        => Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() =>
            ZabbixPushAgent.CreateBuilder("edge").AddHealthCheck(o => o.StaleAfter = TimeSpan.Zero));

    [Fact]
    public void Every_named_host_is_resolved_by_its_name()
    {
        var services = new ServiceCollection();
        services.AddZabbixPushAgent("edge").AddServer("zbx1").AddServer("zbx2");
        services.AddZabbixActiveAgent("roamer").AddServer("zbx1");
        services.AddZabbixActiveProxy("proxy").AddServer("zbx1").AddTrapper(0).AllowPeer(System.Net.IPAddress.Loopback);
        using ServiceProvider provider = services.BuildValidated();

        Assert.Equal("edge", Assert.Single(provider.GetRequiredKeyedService<IReadOnlyList<ZabbixAgentStatus>>("edge")).Name);   // one per destination once started
        Assert.Equal("roamer", Assert.Single(provider.GetRequiredKeyedService<IReadOnlyList<ZabbixAgentStatus>>("roamer")).Name);
        Assert.NotNull(provider.GetRequiredKeyedService<ZabbixActiveProxy>("proxy"));
    }
}
