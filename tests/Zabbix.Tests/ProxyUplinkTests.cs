// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using Community.Zabbix.Protocol;
using Community.Zabbix.Proxy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The active proxy's side of the server conversation: the configuration pulled with the revisions held and merged; a
/// refusal is a failure with the server's words, and a refusal naming the server's version is answered by announcing it;
/// history pushed with the caller's ids, <c>more</c> and <c>proxy_delay</c>; a probe without <c>history data</c>.
/// </summary>
public sealed class ProxyUplinkTests
{
    /// <summary>An exchange that records each request and answers from a queue.</summary>
    private sealed class Scripted(params string[] replies)
    {
        private readonly Queue<string> _replies = new(replies);

        public List<JsonElement> Sent { get; } = [];

        public ZabbixExchangeCallback Exchange => (request, _) =>
        {
            Sent.Add(JsonDocument.Parse(request).RootElement.Clone());
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(_replies.Dequeue()));
        };
    }

    private const string Session = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task A_config_pull_sends_the_revisions_held_and_merges_the_reply()
    {
        var server = new Scripted(ProxyConfigFixtures.Delta80);
        var uplink = new ZabbixProxyUplink(server.Exchange, "zbx-proxy-probe", new ZabbixComponentVersion(8, 0, 0), session: Session);
        using JsonDocument full = JsonDocument.Parse(ProxyConfigFixtures.Full80);
        ZabbixProxyConfig held = ZabbixProxyConfig.Empty.Merge(full.RootElement);

        ZabbixProxyConfig config = await uplink.GetConfigAsync(held, TestContext.Current.CancellationToken);

        JsonElement request = Assert.Single(server.Sent);
        Assert.Equal("proxy config", request.GetProperty("request").GetString());
        Assert.Equal("zbx-proxy-probe", request.GetProperty("host").GetString());
        Assert.Equal("8.0.0", request.GetProperty("version").GetString());
        Assert.Equal(Session, request.GetProperty("session").GetString());
        Assert.Equal(1793, request.GetProperty("config_revision").GetInt64());
        Assert.False(request.TryGetProperty("hostmap_revision", out _));                          // not in a proxy group
        Assert.Equal(2, config.Items.Count);
    }

    [Fact]
    public async Task A_refused_config_is_a_failure_logged_at_warning_with_the_server_s_words_on_the_exception_only()
    {
        var server = new Scripted("""{"response":"failed","info":"cannot collect configuration data"}""");
        var logger = new FakeLogger<ZabbixProxyUplink>();
        var uplink = new ZabbixProxyUplink(server.Exchange, "proxy-01", logger: logger, session: Session);

        ZabbixProxyRefusedException refused = await Assert.ThrowsAsync<ZabbixProxyRefusedException>(() => uplink.GetConfigAsync(ZabbixProxyConfig.Empty, TestContext.Current.CancellationToken));

        Assert.Equal("cannot collect configuration data", refused.Info);
        FakeLogRecord record = Assert.Single(logger.Collector.GetSnapshot(), record => record.Id.Id == 7002);
        Assert.Equal(LogLevel.Warning, record.Level);
        // The server's words echo item keys and names: classified peer text, erased from the log by the fallback redactor.
        Assert.DoesNotContain("cannot collect configuration data", record.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(logger.Collector.GetSnapshot(), record => record.Id.Id == 7000);   // never "revision 0, 0 items"
    }

    [Fact]
    public async Task A_server_that_closes_without_answering_config_is_a_refusal_that_says_what_that_means()
    {
        // zbx_send_proxyconfig logs and sends nothing for a proxy it does not know, so the uplink must say more than
        // "the connection ended before a whole frame arrived".
        var logger = new FakeLogger<ZabbixProxyUplink>();
        var uplink = new ZabbixProxyUplink((_, cancellationToken) => new ValueTask<ReadOnlyMemory<byte>>(ZabbixHeader.ReadAsync(Stream.Null, cancellationToken)),
            "proxy-new", logger: logger, session: Session);

        ZabbixProxyRefusedException refused = await Assert.ThrowsAsync<ZabbixProxyRefusedException>(() => uplink.GetConfigAsync(ZabbixProxyConfig.Empty, TestContext.Current.CancellationToken));

        Assert.True(ZabbixHeader.IsNoReply(refused.InnerException!));
        Assert.Contains("does not know proxy 'proxy-new'", refused.Message, StringComparison.Ordinal);
        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Collector.GetSnapshot(), record => record.Id.Id == 7002).Level);
    }

    [Fact]
    public async Task A_refusal_naming_the_server_s_version_is_answered_by_announcing_it_and_asking_again()
    {
        var server = new Scripted("""{"response":"failed","info":"proxy and server major versions do not match","version":"8.0.0rc1"}""", ProxyConfigFixtures.Full80);
        var uplink = new ZabbixProxyUplink(server.Exchange, "proxy-01", new ZabbixComponentVersion(7, 4, 0), session: Session);

        ZabbixProxyConfig config = await uplink.GetConfigAsync(ZabbixProxyConfig.Empty, TestContext.Current.CancellationToken);

        Assert.Equal(["7.4.0", "8.0.0"], server.Sent.Select(request => request.GetProperty("version").GetString()));
        Assert.Equal(new ZabbixComponentVersion(8, 0, 0), uplink.Version);
        Assert.Single(config.Items);
    }

    [Fact]
    public async Task History_is_pushed_with_the_ids_given_more_and_proxy_delay()
    {
        var server = new Scripted("""{"upload":"enabled","response":"success"}""", """{"upload":"disabled","response":"failed"}""");
        var uplink = new ZabbixProxyUplink(server.Exchange, "proxy-01", session: Session);

        ZabbixProxyResponse first = await uplink.UploadAsync(new ZabbixProxyDataPage(History: [new ZabbixProxyHistoryItem(41, 100, new ZabbixClock(1_700_000_000, 500), "0.5")]), more: true, proxyDelay: 12, TestContext.Current.CancellationToken);
        ZabbixProxyResponse second = await uplink.UploadAsync(new ZabbixProxyDataPage(History: [new ZabbixProxyHistoryItem(42, 101, new ZabbixClock(1_700_000_001, 0), "1")]), more: false, proxyDelay: 0, TestContext.Current.CancellationToken);

        JsonElement page = server.Sent[0];
        Assert.Equal("proxy data", page.GetProperty("request").GetString());
        Assert.Equal(Session, page.GetProperty("session").GetString());
        Assert.Equal(1, page.GetProperty("more").GetInt32());
        Assert.Equal(12, page.GetProperty("proxy_delay").GetInt64());
        Assert.Equal(41, page.GetProperty("history data")[0].GetProperty("id").GetInt64());
        Assert.False(server.Sent[1].TryGetProperty("more", out _));
        Assert.False(server.Sent[1].TryGetProperty("proxy_delay", out _));
        Assert.True(first.IsSuccess);
        Assert.True(second.UploadDisabled);
        Assert.False(second.IsSuccess);
    }

    [Fact]
    public async Task A_probe_carries_no_history_data_key()
    {
        var server = new Scripted("""{"upload":"disabled","response":"success"}""");
        var uplink = new ZabbixProxyUplink(server.Exchange, "proxy-01", session: Session);

        ZabbixProxyResponse probe = await uplink.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(server.Sent).TryGetProperty("history data", out _));
        Assert.True(probe.UploadDisabled);
    }

    [Fact]
    public async Task A_page_carries_discovery_autoregistration_availability_and_task_results_under_their_own_wire_keys()
    {
        var server = new Scripted("""{"upload":"enabled","response":"success"}""");
        var uplink = new ZabbixProxyUplink(server.Exchange, "proxy-01", session: Session);
        var page = new ZabbixProxyDataPage(
            Discovery: [new ZabbixProxyDiscoveryItem(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), DruleId: 5, DcheckId: 6, Ip: "10.0.0.1", Dns: null, Port: 22, Value: "ssh", Status: 0)],
            Autoregistration: [new ZabbixProxyAutoregistrationItem(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), "new-host", Ip: "10.0.0.2", Dns: null, Port: 10050, HostMetadata: "linux", Flags: 0, TlsAccepted: 1)],
            Availability: [new ZabbixProxyAvailabilityItem(InterfaceId: 7, Available: 1, Error: null)],
            Tasks: [new ZabbixProxyTaskResult(Type: ZabbixProxyTaskType.RemoteCommandResult, Clock: DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), Ttl: 0, Status: 0, Info: "done", ParentTaskId: 9)]);

        ZabbixProxyResponse response = await uplink.UploadAsync(page, more: false, proxyDelay: 0, TestContext.Current.CancellationToken);

        JsonElement sent = Assert.Single(server.Sent);
        Assert.False(sent.TryGetProperty("history data", out _));
        Assert.Equal(5, sent.GetProperty("discovery data")[0].GetProperty("drule").GetInt64());
        Assert.Equal("new-host", sent.GetProperty("auto registration")[0].GetProperty("host").GetString());
        Assert.Equal(7, sent.GetProperty("interface availability")[0].GetProperty("interfaceid").GetInt64());
        Assert.Equal(9, sent.GetProperty("tasks")[0].GetProperty("parent_taskid").GetInt64());
        Assert.True(response.IsSuccess);
    }

    /// <summary>
    /// A discovered host is written as upstream's proxy writes it (<c>discoverer.c</c> <c>process_services</c>, then
    /// <c>zbx_discovery_update_interface_proxy</c> → <c>zbx_pb_discovery_write_host</c>): each service row, then the row with
    /// <c>dcheck</c> 0, port 0 and value "" that closes the host, up when any service is and down otherwise (also with no
    /// services at all). A service cannot claim <c>dcheck</c> 0, which the server reads as that closing row.
    /// </summary>
    [Fact]
    public void A_discovered_host_s_rows_are_its_services_then_the_dcheck_0_row_that_closes_it()
    {
        var clock = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        var up = new ZabbixProxyDiscoveredHost(5, "10.0.0.1", clock, [new ZabbixProxyDiscoveredService(6, 22, IsUp: false), new ZabbixProxyDiscoveredService(7, 80, IsUp: true, "http")]);
        var down = new ZabbixProxyDiscoveredHost(5, "10.0.0.2", clock, []);

        IReadOnlyList<ZabbixProxyDiscoveryItem> rows = up.ToRows();

        Assert.Equal(
            [new ZabbixProxyDiscoveryItem(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), 5, 6, "10.0.0.1", "", 22, "", 1), new ZabbixProxyDiscoveryItem(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), 5, 7, "10.0.0.1", "", 80, "http", 0), new ZabbixProxyDiscoveryItem(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), 5, 0, "10.0.0.1", "", 0, "", 0)],
            rows);
        Assert.Equal([new ZabbixProxyDiscoveryItem(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), 5, 0, "10.0.0.2", "", 0, "", 1)], down.ToRows());
        Assert.Contains("\"dcheck\":0", JsonSerializer.Serialize(rows[^1], ZabbixProxyJson.Default.ZabbixProxyDiscoveryItem), StringComparison.Ordinal);
        Assert.Equal("DcheckId", Assert.Throws<ArgumentOutOfRangeException>(() => new ZabbixProxyDiscoveredService(0, 22, IsUp: true)).ParamName);
        Assert.Equal("DruleId", Assert.Throws<ArgumentOutOfRangeException>(() => new ZabbixProxyDiscoveredHost(0, "10.0.0.1", clock, [])).ParamName);
        Assert.Throws<ArgumentException>(() => new ZabbixProxyDiscoveredHost(5, " ", clock, []));
    }

    [Fact]
    public void A_new_session_is_the_32_hex_digits_the_server_requires()
    {
        var uplink = new ZabbixProxyUplink((_, _) => default, "proxy-01");

        Assert.Matches("^[0-9a-f]{32}$", uplink.Session);
    }

    [Fact]
    public async Task A_config_pull_and_a_page_each_leave_a_span_and_a_duration_sample_tagged_with_the_outcome()
    {
        using ServiceProvider provider = new ServiceCollection().AddMetrics().BuildServiceProvider();
        IMeterFactory meters = provider.GetRequiredService<IMeterFactory>();
        using var durations = new MetricCollector<double>(meters, ZabbixProxyTelemetry.Name, ZabbixProxyTelemetry.DurationInstrument);
        string proxy = "proxy-" + Guid.NewGuid().ToString("N");                                 // the source is process-wide: keep only this test's spans
        var spans = new System.Collections.Concurrent.ConcurrentQueue<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ZabbixProxyTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = span => { if (Equals(span.GetTagItem("zabbix.proxy.name"), proxy)) spans.Enqueue(span); },
        };
        ActivitySource.AddActivityListener(listener);
        using var telemetry = new ZabbixProxyTelemetry(meters, TimeProvider.System);
        var server = new Scripted("""{"response":"failed","info":"no"}""", """{"upload":"enabled","response":"success"}""");
        var uplink = new ZabbixProxyUplink(server.Exchange, proxy, null, null, null, Session, telemetry);

        await Assert.ThrowsAsync<ZabbixProxyRefusedException>(() => uplink.GetConfigAsync(ZabbixProxyConfig.Empty, TestContext.Current.CancellationToken));
        await uplink.UploadAsync(new ZabbixProxyDataPage(History: [new ZabbixProxyHistoryItem(1, 100, new ZabbixClock(0, 0), "1")]), more: false, proxyDelay: 0, TestContext.Current.CancellationToken);

        Activity[] seen = [.. spans];
        Assert.Equal([ZabbixProxyTelemetry.ConfigSpan, ZabbixProxyTelemetry.DataSpan], seen.Select(span => span.OperationName));
        Assert.Equal("refused", seen[0].GetTagItem("error.type"));
        Assert.Equal(ActivityStatusCode.Ok, seen[1].Status);
        Assert.Collection(durations.GetMeasurementSnapshot(),
            m => { Assert.Equal("config", m.Tags["zabbix.proxy.operation"]); Assert.Equal("refused", m.Tags["error.type"]); },
            m => { Assert.Equal("data", m.Tags["zabbix.proxy.operation"]); Assert.False(m.Tags.ContainsKey("error.type")); });
    }
}
