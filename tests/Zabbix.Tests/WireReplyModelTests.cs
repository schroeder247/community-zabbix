// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The wire client's replies read into their models, as 7.0, 7.4 and 8.0 servers and proxies write them. Each reply is
/// built the way the pinned sources' builders write it (tools/zabbix-sources.lock): <c>status_stats_export</c> in
/// <c>trapper.c</c>, <c>zbx_zabbix_stats_get</c> in <c>stats.c</c> with each component's registered parts,
/// <c>trapper_item_test</c> and <c>zbx_trapper_preproc_test_run</c>, <c>zbx_report_test</c> and
/// <c>trapper_process_alert_send</c>.
/// </summary>
public sealed class WireReplyModelTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>A client whose exchange answers <paramref name="reply"/> and keeps what was sent.</summary>
    private static ZabbixWireClient Answering(string reply, List<JsonElement>? sent = null, TimeProvider? time = null)
        => new((request, _) =>
        {
            sent?.Add(JsonDocument.Parse(request).RootElement.Clone());
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(reply));
        }, new ZabbixWireClientOptions(), null, time);

    // ----- status.get ----------------------------------------------------------------------------------------------------

    /// <summary>status_stats_export: the sections and their entries are the same in 7.0, 7.4 and 8.0; the version differs.</summary>
    private static string Status(string version) => """
        {"response":"success","data":{
        "template stats":[{"count":312}],
        "host stats":[{"attributes":{"proxyid":0,"status":0},"count":5},{"attributes":{"proxyid":10501,"status":0},"count":2},
          {"attributes":{"proxyid":0,"status":1},"count":1},{"attributes":{"proxyid":10501,"status":1},"count":0}],
        "item stats":[{"attributes":{"proxyid":0,"status":0,"state":0},"count":420},{"attributes":{"proxyid":10501,"status":0,"state":0},"count":90},
          {"attributes":{"proxyid":0,"status":0,"state":1},"count":7},{"attributes":{"proxyid":10501,"status":0,"state":1},"count":2},
          {"attributes":{"proxyid":0,"status":1},"count":11},{"attributes":{"proxyid":10501,"status":1},"count":3}],
        "trigger stats":[{"attributes":{"status":0,"value":0},"count":150},{"attributes":{"status":0,"value":1},"count":4},{"attributes":{"status":1},"count":9}],
        "user stats":[{"attributes":{"status":0},"count":1},{"attributes":{"status":1},"count":2}],
        "required performance":[{"attributes":{"proxyid":0},"count":"6.733333"},{"attributes":{"proxyid":10501},"count":"1.500000"}],
        "server stats":{"version":"
        """ + version + "\"}}}";

    [Theory]
    [InlineData("7.0.31", 7, 0, 31)]
    [InlineData("7.4.15", 7, 4, 15)]
    [InlineData("8.0.0rc2", 8, 0, 0)]
    public async Task A_super_admin_s_status_reads_the_installation_s_counts_and_each_proxy_s(string version, int major, int minor, int patch)
    {
        await using ZabbixWireClient wire = Answering(Status(version));

        ZabbixServerStatus status = await wire.GetStatusAsync(Cancel);

        Assert.Equal(new Version(major, minor, patch), status.Version);
        Assert.Equal(312L, status.TemplateCount);
        Assert.Equal((5L, 1L), (status.EnabledHostCount!.Value, status.DisabledHostCount!.Value));
        Assert.Equal((420L, 7L, 11L), (status.EnabledItemCount!.Value, status.UnsupportedItemCount!.Value, status.DisabledItemCount!.Value));
        Assert.Equal((150L, 4L, 9L), (status.OkTriggerCount!.Value, status.ProblemTriggerCount!.Value, status.DisabledTriggerCount!.Value));
        Assert.Equal((1L, 2L), (status.OnlineUserCount!.Value, status.OfflineUserCount!.Value));
        Assert.Equal(6.733333, status.RequiredPerformance!.Value, 6);
        ZabbixProxyLoad proxy = Assert.Single(status.Proxies);
        Assert.Equal(new ZabbixProxyLoad
        {
            ProxyId = 10501,
            EnabledHostCount = 2,
            DisabledHostCount = 0,
            EnabledItemCount = 90,
            UnsupportedItemCount = 2,
            DisabledItemCount = 3,
            RequiredPerformance = 1.5,
        }, proxy);
    }

    [Fact]
    public async Task Any_other_session_s_status_has_no_counts()
    {
        // recv_getstatus answers an empty data object unless the session is a super admin's.
        await using ZabbixWireClient wire = Answering("""{"response":"success","data":{}}""");

        ZabbixServerStatus status = await wire.GetStatusAsync(Cancel);

        Assert.Equal(new ZabbixServerStatus(), status);
        Assert.Null(status.EnabledHostCount);
        Assert.Empty(status.Proxies);
    }

    [Fact]
    public async Task A_status_without_the_database_s_counts_leaves_them_null_and_keeps_the_rest()
    {
        // A section whose database read failed is left out (templates_res, users_res in status_stats_export).
        await using ZabbixWireClient wire = Answering("""{"response":"success","data":{"host stats":[{"attributes":{"proxyid":0,"status":0},"count":3}],"server stats":{"version":"7.4.15"}}}""");

        ZabbixServerStatus status = await wire.GetStatusAsync(Cancel);

        Assert.Equal((null, null, 3L, 0L), (status.TemplateCount, status.OnlineUserCount, status.EnabledHostCount, status.DisabledHostCount));
    }

    // ----- zabbix.stats --------------------------------------------------------------------------------------------------

    /// <summary>
    /// zbx_zabbix_stats_get on a 7.0 server: the counts, then the parts the server registers (the preprocessing manager's,
    /// discovery's and the server's own: queues, triggers, the value and trend caches, HA nodes, proxies, the
    /// values-per-second limit), the configuration cache, the version, the write cache with its trend part, and the
    /// processes.
    /// </summary>
    private const string Server70Statistics = """
        {"response":"success","data":{"boottime":1731300000,"uptime":7200,"hosts":12,"items":2300,"items_unsupported":17,
        "requiredperformance":6.733333,"preprocessing_queue":0,"preprocessing":{"queued":{"count":0,"size":0},"direct":{"count":0,"size":0},"queue":0},
        "discovery_queue":1,"lld_queue":0,"connector_queue":0,"triggers":163,
        "vcache":{"buffer":{"total":8388608,"free":8126464,"pfree":96.875,"used":262144,"pused":3.125},"cache":{"requests":5000,"hits":4900,"misses":100,"mode":0}},
        "tcache":{"hits":0,"misses":0,"all":0,"phits":0,"pmisses":0,"items":0,"requests":0,"pitems":0},
        "ha":[],"proxy":[],"vps":{"status":0,"written_total":120000,"limit":0},
        "rcache":{"total":33554432,"free":31457280,"pfree":93.75,"used":2097152,"pused":6.25},"version":"7.0.31",
        "wcache":{"values":{"all":120000,"float":60000,"uint":50000,"str":4000,"log":1000,"text":5000,"bin":0,"not supported":17},
        "history":{"pfree":99.2,"free":16643072,"total":16777216,"used":134144,"pused":0.8},
        "index":{"pfree":99.5,"free":4173824,"total":4194304,"used":20480,"pused":0.5},
        "trend":{"pfree":98.4,"free":4128768,"total":4194304,"used":65536,"pused":1.6}},
        "process":{"poller":{"busy":{"avg":2.5,"max":4.1,"min":0.9},"idle":{"avg":97.5,"max":99.1,"min":95.9},"count":5},
        "trapper":{"busy":{"avg":0.3,"max":0.6,"min":0},"idle":{"avg":99.7,"max":100,"min":99.4},"count":5}}}}
        """;

    [Fact]
    public async Task A_7_0_server_s_statistics_read_the_counts_the_caches_the_queues_and_the_processes()
    {
        var sent = new List<JsonElement>();
        await using ZabbixWireClient wire = Answering(Server70Statistics, sent);

        ZabbixServerStatistics stats = await wire.GetStatisticsAsync(Cancel);

        Assert.Equal("zabbix.stats", sent[0].GetProperty("request").GetString());
        Assert.False(sent[0].TryGetProperty("sid", out _));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1731300000), stats.BootTime);
        Assert.Equal(TimeSpan.FromHours(2), stats.Uptime);
        Assert.Equal(new Version(7, 0, 31), stats.Version);
        Assert.Equal((12L, 2300L, 17L), (stats.HostCount!.Value, stats.ItemCount!.Value, stats.UnsupportedItemCount!.Value));
        Assert.Equal(new ZabbixCacheUsage { TotalBytes = 33554432, FreeBytes = 31457280, UsedBytes = 2097152, FreePercent = 93.75, UsedPercent = 6.25 }, stats.ConfigurationCache);
        Assert.Equal((120000L, 60000L, 50000L, 4000L, 17L), (stats.WriteCache!.Values!.TotalCount!.Value, stats.WriteCache.Values.FloatCount!.Value,
            stats.WriteCache.Values.UnsignedCount!.Value, stats.WriteCache.Values.CharacterCount!.Value, stats.WriteCache.Values.NotSupportedCount!.Value));
        Assert.Null(stats.WriteCache.Values.JsonCount);
        Assert.Equal(1.6, stats.WriteCache.Trend!.UsedPercent);
        Assert.Equal((4900L, ZabbixValueCacheMode.Normal), (stats.ValueCache!.Cache!.HitCount!.Value, stats.ValueCache.Cache.Mode!.Value));
        Assert.Equal((0L, 1L, 0L, 163L), (stats.PreprocessingQueue!.Value, stats.DiscoveryQueue!.Value, stats.LowLevelDiscoveryQueue!.Value, stats.TriggerCount!.Value));
        Assert.Equal(["poller", "trapper"], stats.Processes!.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(new ZabbixProcessUtilization { Busy = new() { Average = 2.5, Max = 4.1, Min = 0.9 }, Idle = new() { Average = 97.5, Max = 99.1, Min = 95.9 }, Count = 5 }, stats.Processes["poller"]);
        Assert.Null(stats.VMwareCache);
        Assert.Equal(["ha", "preprocessing", "proxy", "tcache", "vps"], ((IZabbixAdditionalFields)stats).AdditionalFields!.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task An_8_0_server_s_statistics_read_the_json_values_and_keep_the_trend_cache_s_new_field()
    {
        // 8.0 counts JSON values in the write cache (stats.c 8.0:184), adds paccessed to tcache (stats_server.c 8.0:78), and
        // writes vps from the values-per-second monitor's own part (vps_monitor.c).
        string reply = Server70Statistics
            .Replace("\"bin\":0,", "\"bin\":0,\"json\":250,", StringComparison.Ordinal)
            .Replace("\"pitems\":0}", "\"pitems\":0,\"paccessed\":0}", StringComparison.Ordinal)
            .Replace("\"version\":\"7.0.31\"", "\"version\":\"8.0.0rc2\"", StringComparison.Ordinal);
        await using ZabbixWireClient wire = Answering(reply);

        ZabbixServerStatistics stats = await wire.GetStatisticsAsync(Cancel);

        Assert.Equal(250L, stats.WriteCache!.Values!.JsonCount);
        Assert.Equal(new Version(8, 0, 0), stats.Version);
        Assert.True(((IZabbixAdditionalFields)stats).AdditionalFields!["tcache"].TryGetProperty("paccessed", out _));
    }

    [Fact]
    public async Task A_7_4_proxy_s_statistics_have_no_server_parts_and_keep_the_proxy_s_own()
    {
        // A proxy registers its own part (zbx_stats_ext_get_data_proxy: name, passive, the encryption it accepts or
        // connects with), has no trend cache, and reports its buffer (stats.c 7.4: the proxy buffer section).
        const string reply = """
            {"response":"success","data":{"boottime":1731300000,"uptime":60,"hosts":3,"items":40,"items_unsupported":0,"requiredperformance":0.5,
            "preprocessing_queue":0,"preprocessing":{"queued":{"count":0,"size":0},"direct":{"count":0,"size":0},"queue":0},"discovery_queue":0,
            "name":"proxy-dmz","passive":false,"unencrypted":true,"psk":false,"cert":false,
            "rcache":{"total":8388608,"free":8000000,"pfree":95.37,"used":388608,"pused":4.63},"version":"7.4.15",
            "wcache":{"values":{"all":10,"float":10,"uint":0,"str":0,"log":0,"text":0,"bin":0,"not supported":0},
            "history":{"pfree":100,"free":16777216,"total":16777216,"used":0,"pused":0},"index":{"pfree":100,"free":4194304,"total":4194304,"used":0,"pused":0}},
            "proxy buffer":{"memory":{"pfree":100,"free":16777216,"total":16777216,"used":0,"pused":0},"state":1,"state change":2},
            "process":{"data sender":{"busy":{"avg":0.1,"max":0.1,"min":0.1},"idle":{"avg":99.9,"max":99.9,"min":99.9},"count":1}}}}
            """;
        await using ZabbixWireClient wire = Answering(reply);

        ZabbixServerStatistics stats = await wire.GetStatisticsAsync(Cancel);

        Assert.Null(stats.ValueCache);
        Assert.Null(stats.WriteCache!.Trend);
        Assert.Null(stats.LowLevelDiscoveryQueue);
        Assert.Equal(1, stats.Processes!["data sender"].Count);
        IDictionary<string, JsonElement> rest = ((IZabbixAdditionalFields)stats).AdditionalFields!;
        Assert.Equal("proxy-dmz", rest["name"].GetString());
        Assert.Equal(1, rest["proxy buffer"].GetProperty("state").GetInt32());
    }

    [Fact]
    public async Task Statistics_of_the_wrong_kind_are_invalid_data_not_a_default()
    {
        await using ZabbixWireClient wire = Answering("""{"response":"success","data":{"uptime":"a while"}}""");

        await Assert.ThrowsAsync<InvalidDataException>(() => wire.GetStatisticsAsync(Cancel));
    }

    // ----- item.test -----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_item_test_sends_the_request_as_the_trapper_reads_it_and_reads_the_value_and_each_step()
    {
        // trapper_item_test reads data.item, data.host and data.options; the reply's item is zbx_trapper_item_test_add_value's
        // and its preprocessing zbx_trapper_preproc_test_run's, which write the same fields in 7.0, 7.4 and 8.0.
        var sent = new List<JsonElement>();
        await using ZabbixWireClient wire = Answering("""
            {"response":"success","data":{"item":{"result":"5.5","eol":"LF"},"preprocessing":{"steps":[
            {"result":"11"},{"action":3,"error":{"value":"Cannot convert value."},"failed":"Out of range."}],
            "error":"Out of range."}}}
            """, sent);
        var request = new ZabbixItemTestRequest(new ZabbixItemTestItem
        {
            ItemType = 0,
            Key = ZabbixItemKey.Parse("system.cpu.load[all,avg1]"),
            ValueType = 0,
            Timeout = ZabbixDuration.Parse("5s"),
            Steps = [new() { StepType = 1, Parameters = "2" }, new() { StepType = 13, Parameters = "0\n10", ErrorHandler = 3, ErrorHandlerParameters = "Out of range." }],
        })
        {
            Host = new() { HostId = 10084, Interface = new() { Address = "10.0.0.2", UseIP = true, Port = 10050 } },
            Conditions = new() { SkipValueTypeCheck = false, State = 0 },
        };

        ZabbixItemTestResult result = await wire.TestItemAsync(request, Cancel);

        JsonElement data = sent[0].GetProperty("data");
        Assert.Equal("""{"type":0,"key":"system.cpu.load[all,avg1]","value_type":0,"timeout":"5s","steps":[{"type":1,"params":"2"},{"type":13,"params":"0\n10","error_handler":3,"error_handler_params":"Out of range."}]}""", data.GetProperty("item").GetRawText());
        Assert.Equal("""{"useip":"1","address":"10.0.0.2","port":"10050"}""", data.GetProperty("host").GetProperty("interface").GetRawText());
        Assert.Equal("""{"single":false,"state":0}""", data.GetProperty("options").GetRawText());
        Assert.Equal(("5.5", "LF"), (result.Item!.Result, result.Item.EndOfLine));
        Assert.Equal("11", result.Preprocessing!.Steps![0].Result);
        Assert.Equal((3, "Cannot convert value.", "Out of range."), (result.Preprocessing.Steps[1].Action!.Value, result.Preprocessing.Steps[1].Error!.Message, result.Preprocessing.Steps[1].Failed));
        Assert.Equal("Out of range.", result.Preprocessing.Error);
        Assert.Null(result.Preprocessing.Result);
    }

    [Fact]
    public async Task A_long_value_reads_as_cut_short_with_its_size_and_a_failed_collection_as_its_error()
    {
        // ZBX_JSON_TEST_DATA_MAX_SIZE: the server cuts a long text and says how long it was.
        await using ZabbixWireClient wire = Answering("""
            {"response":"success","data":{"item":{"error":"Cannot connect.","eol":"CRLF"},"preprocessing":{"steps":[],"result":"x","truncated":true,"original_size":524289}}}
            """);

        ZabbixItemTestResult result = await wire.TestItemAsync(new ZabbixItemTestRequest(new ZabbixItemTestItem { Key = ZabbixItemKey.Parse("agent.ping"), ValueType = 3 }), Cancel);

        Assert.Equal(("Cannot connect.", "CRLF"), (result.Item!.Error, result.Item.EndOfLine));
        Assert.Equal((true, 524289L), (result.Preprocessing!.IsTruncated!.Value, result.Preprocessing.OriginalSize!.Value));
    }

    [Fact]
    public async Task A_previous_value_s_age_is_written_as_the_trapper_reads_a_timestamp()
    {
        // trapper_parse_preproc_test: "now", or "now-" and a time suffix.
        var sent = new List<JsonElement>();
        await using ZabbixWireClient wire = Answering("""{"response":"success","data":{"preprocessing":{"steps":[],"result":"1"}}}""", sent);

        await wire.TestItemAsync(new ZabbixItemTestRequest(new ZabbixItemTestItem { Value = "2", ValueType = 3 })
        {
            Conditions = new() { PreviousValue = new() { Value = "1", Age = TimeSpan.FromMinutes(5) } },
        }, Cancel);

        Assert.Equal("""{"value":"1","timestamp":"now-300s"}""", sent[0].GetProperty("data").GetProperty("options").GetProperty("history").GetRawText());
        var read = JsonSerializer.Deserialize("""{"value":"1","timestamp":"now-1h"}""", TypeInfo<ZabbixItemTestPreviousValue>());
        Assert.Equal(TimeSpan.FromHours(1), read!.Age);
        Assert.Equal(TimeSpan.Zero, JsonSerializer.Deserialize("""{"timestamp":"now"}""", TypeInfo<ZabbixItemTestPreviousValue>())!.Age);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("""{"timestamp":"yesterday"}""", TypeInfo<ZabbixItemTestPreviousValue>()));
    }

    [Fact]
    public async Task A_request_field_the_record_does_not_model_is_sent_by_its_wire_name_and_one_it_models_is_refused_there()
    {
        var sent = new List<JsonElement>();
        await using ZabbixWireClient wire = Answering("""{"response":"success","data":{"item":{"result":"1"}}}""", sent);
        using JsonDocument limit = JsonDocument.Parse("\"1h\"");
        var item = new ZabbixItemTestItem { ItemType = 29, AdditionalFields = new Dictionary<string, JsonElement> { ["lookback_limit"] = limit.RootElement.Clone() } };

        await wire.TestItemAsync(new ZabbixItemTestRequest(item), Cancel);

        Assert.Equal("1h", sent[0].GetProperty("data").GetProperty("item").GetProperty("lookback_limit").GetString());
        var twice = new ZabbixItemTestItem { ItemType = 0, AdditionalFields = new Dictionary<string, JsonElement> { ["type"] = limit.RootElement.Clone() } };
        await Assert.ThrowsAsync<ArgumentException>(() => wire.TestItemAsync(new ZabbixItemTestRequest(twice), Cancel));
    }

    [Fact]
    public async Task A_test_request_never_prints_its_secrets()
    {
        var request = new ZabbixItemTestRequest(new ZabbixItemTestItem { Password = "hunter2", SslKeyPassword = "hunter3" })
        {
            Host = new() { IpmiPassword = "hunter4", TlsPsk = "hunter5", Interface = new() { Details = new() { AuthenticationPassphrase = "hunter6" } } },
        };

        Assert.DoesNotContain("hunter", request.ToString(), StringComparison.Ordinal);
        Assert.Contains("Password = ***", request.ToString(), StringComparison.Ordinal);
    }

    // ----- report.test and alert.send ------------------------------------------------------------------------------------

    [Fact]
    public async Task A_report_test_takes_the_client_s_clock_when_the_request_names_no_time_and_reads_each_recipient()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_731_300_000));
        var sent = new List<JsonElement>();
        await using ZabbixWireClient wire = Answering("""{"response":"success","data":{"recipients":[{"status":1,"recipient":"Admin"},{"status":0,"recipient":"guest","info":"No media defined for user."}]}}""", sent, time);

        ZabbixReportTestResult result = await wire.TestReportAsync(new ZabbixReportTestRequest("Weekly", 1, 1) { Period = 1, Parameters = new Dictionary<string, string> { ["subject"] = "Weekly", ["body"] = "See attached." } }, Cancel);

        Assert.Equal(1_731_300_000, sent[0].GetProperty("data").GetProperty("now").GetInt64());
        Assert.Equal(1, sent[0].GetProperty("data").GetProperty("period").GetInt32());
        Assert.Equal([("Admin", 1, null), ("guest", 0, "No media defined for user.")], result.Recipients!.Select(r => (r.Recipient, r.Status!.Value, r.Info)));
    }

    [Fact]
    public async Task An_alert_test_reads_a_webhook_s_value_and_log_and_an_e_mail_s_empty_answer()
    {
        // trapper_process_alert_send: data is the webhook's value, debug the JavaScript engine's log (embed.c, zabbix.c).
        await using ZabbixWireClient webhook = Answering("""{"response":"success","data":"{\"tags\":{\"__zbx_ticket\":\"42\"}}","debug":{"logs":[{"level":4,"ms":2,"message":"[Webhook] sending"},{"level":4,"ms":180,"message":"[Webhook] received 201"}],"ms":181}}""");
        await using ZabbixWireClient email = Answering("""{"response":"success"}""");

        ZabbixAlertSendResult hooked = await webhook.SendAlertAsync(9, "ops", "s", "m", cancellationToken: Cancel);
        ZabbixAlertSendResult mailed = await email.SendAlertAsync(1, "ops@example.com", "s", "m", cancellationToken: Cancel);

        Assert.Equal("""{"tags":{"__zbx_ticket":"42"}}""", hooked.Value);
        Assert.Equal(TimeSpan.FromMilliseconds(181), hooked.Log!.Elapsed);
        Assert.Equal([(4, TimeSpan.FromMilliseconds(2), "[Webhook] sending"), (4, TimeSpan.FromMilliseconds(180), "[Webhook] received 201")],
            hooked.Log.Entries!.Select(e => (e.Level!.Value, e.Elapsed!.Value, e.Message)));
        Assert.Equal(new ZabbixAlertSendResult(), mailed);
    }

    // ----- the raw path and the contract ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_request_the_client_has_no_method_for_goes_as_given_and_comes_back_as_the_server_sent_it()
    {
        var sent = new List<JsonElement>();
        await using ZabbixWireClient wire = Answering("""{"response":"success","data":{"anything":[1,2]}}""", sent);
        using JsonDocument data = JsonDocument.Parse("""{"x":1}""");

        ZabbixServerReply reply = await wire.SendRequestAsync("acme.request", data.RootElement, Cancel);

        Assert.Equal(("acme.request", "{\"x\":1}"), (sent[0].GetProperty("request").GetString(), sent[0].GetProperty("data").GetRawText()));
        Assert.Equal("[1,2]", reply.Data!.Value.GetProperty("anything").GetRawText());
    }

    [Fact]
    public void The_wire_s_contract_reads_and_writes_its_records_as_the_client_does()
    {
        var request = new ZabbixItemTestRequest(new ZabbixItemTestItem { Key = ZabbixItemKey.Parse("agent.ping"), FollowRedirects = true });

        string written = JsonSerializer.Serialize(request, TypeInfo<ZabbixItemTestRequest>());

        Assert.Equal("""{"item":{"key":"agent.ping","follow_redirects":"1"}}""", written);
        Assert.Equal(request, JsonSerializer.Deserialize(written, TypeInfo<ZabbixItemTestRequest>()));
        Assert.Same(ZabbixWireJson.TypeInfoResolver, ZabbixWireJson.Options.TypeInfoResolver);
    }

    private static JsonTypeInfo<T> TypeInfo<T>() => (JsonTypeInfo<T>)ZabbixWireJson.Options.GetTypeInfo(typeof(T));
}
