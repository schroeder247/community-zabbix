// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Text.Json;
using Community.Zabbix.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The pull plane: the depth you call at is the item.get filter; latest values are stamped with the host; history is read
/// in clock order across value types and never truncated; absence is answered one way; one bad key never poisons a node.
/// </summary>
public sealed class TreeTests
{
    private const long S = 1_700_000_100;

    private const string Items = """
        [
          {"itemid":"1","hostid":"10","key_":"system.cpu.load[all,avg1]","name":"CPU","delay":"30s","value_type":"0","lastvalue":"0.42","lastclock":"1700000000","lastns":"501","hosts":[{"hostid":"10","host":"web-01"}]},
          {"itemid":"2","hostid":"10","key_":"vfs.fs.size[/,used]","name":"Disk","delay":"1m","value_type":"3","lastvalue":"1024","lastclock":"1700000001","lastns":"0","hosts":[{"hostid":"10","host":"web-01"}]},
          {"itemid":"3","hostid":"11","key_":"agent.ping","name":"Ping","delay":"1m","value_type":"3","lastvalue":"","lastclock":"0","lastns":"0","hosts":[{"hostid":"11","host":"db-01"}]}
        ]
        """;

    private sealed record Tree(ZabbixClient Server, ScriptedApi Api, FakeLogCollector Logs, ServiceProvider Provider) : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }

    /// <summary>A tree over a scripted API; <paramref name="script"/> scripts it (the item list when omitted).</summary>
    private static Tree Build(Func<ScriptedApi, ScriptedApi>? script = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => (script ?? (scripted => scripted.On("item.get", Items)))(new ScriptedApi()));   // the container owns it
        services.AddFakeLogging().AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace));
        services.AddZabbixClient().Configure(options => { options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"); options.ApiToken = "tok"; });
        services.AddHttpClient(ZabbixHttp.GetClientName()).ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<ScriptedApi>());
        ServiceProvider provider = services.BuildValidated();
        return new Tree(provider.GetRequiredService<ZabbixClient>(), provider.GetRequiredService<ScriptedApi>(), provider.GetFakeLogCollector(), provider);
    }

    [Fact]
    public async Task The_depth_you_call_at_is_the_get_filter()
    {
        using Tree tree = Build();

        await tree.Server.GetLatestAsync(TestContext.Current.CancellationToken).CountAsync(cancellationToken: TestContext.Current.CancellationToken);
        await tree.Server.HostGroups["Linux servers"].GetLatestAsync(TestContext.Current.CancellationToken).CountAsync(cancellationToken: TestContext.Current.CancellationToken);
        await tree.Server.Hosts["web-01"].GetLatestAsync(TestContext.Current.CancellationToken).CountAsync(cancellationToken: TestContext.Current.CancellationToken);
        await tree.Server.Hosts["web-01"].Items["system.cpu.load[all,avg1]"].GetLatestAsync(TestContext.Current.CancellationToken).CountAsync(cancellationToken: TestContext.Current.CancellationToken);

        JsonElement[] calls = [.. tree.Api.ParamsOf("item.get")];
        Assert.Equal(4, calls.Length);
        Assert.False(calls[0].TryGetProperty("host", out _));
        Assert.False(calls[0].TryGetProperty("group", out _));
        Assert.Equal("Linux servers", calls[1].GetProperty("group").GetString());
        Assert.Equal("web-01", calls[2].GetProperty("host").GetString());
        Assert.Equal("system.cpu.load[all,avg1]", calls[3].GetProperty("filter").GetProperty("key_").GetString());
        Assert.Equal("web-01", calls[3].GetProperty("host").GetString());
    }

    [Fact]
    public async Task An_item_key_goes_on_the_wire_verbatim_quotes_and_all()
    {
        using Tree tree = Build();

        await tree.Server.Hosts["web-01"].Items["k[\"a\"]"].GetLatestAsync(TestContext.Current.CancellationToken).CountAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("k[\"a\"]", tree.Api.ParamsOf("item.get").Single().GetProperty("filter").GetProperty("key_").GetString());
    }

    [Fact]
    public async Task Latest_yields_one_value_per_collected_item_stamped_with_its_host()
    {
        using Tree tree = Build();

        List<ZabbixValue> values = await tree.Server.GetLatestAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, values.Count);                                                  // the never-collected item yields nothing
        Assert.Equal("web-01", values[0].Host);
        Assert.Equal(ZabbixItemKey.Parse("system.cpu.load[all,avg1]"), values[0].Key);
        Assert.Equal("0.42", values[0].Value);
        Assert.Equal(new ZabbixClock(1_700_000_000, 501), values[0].Clock);                 // to the nanosecond, not the tick
    }

    [Fact]
    public async Task A_host_enumerates_its_items_and_a_scope_its_hosts()
    {
        using Tree tree = Build(api => api.On("item.get", Items)
            .On("host.get", """[{"hostid":"10","host":"web-01","name":"Web 01","status":"0"},{"hostid":"11","host":"db-01","name":"DB 01","status":"0"}]""")
            .On("hostgroup.get", """[{"groupid":"2","name":"Linux servers"}]"""));

        List<ZabbixHostNode> hosts = await tree.Server.HostGroups["Linux servers"].Hosts.ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        List<ZabbixItemNode> items = await tree.Server.Hosts["web-01"].Items.ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["web-01", "db-01"], hosts.Select(host => host.Name));
        Assert.Equal("2", tree.Api.ParamsOf("host.get").Single().GetProperty("groupids")[0].GetString());
        Assert.Equal(3, items.Count);
        Assert.Equal(["web-01"], items.Select(item => item.Host.Name).Distinct());
    }

    [Fact]
    public async Task A_group_that_does_not_exist_throws_not_found_whether_enumerated_or_read()
    {
        using Tree tree = Build(api => api.On("hostgroup.get", "[]").On("item.get", "[]"));

        ZabbixNotFoundException enumerated = await Assert.ThrowsAsync<ZabbixNotFoundException>(async () => await tree.Server.HostGroups["nope"].Hosts.ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
        ZabbixNotFoundException read = await Assert.ThrowsAsync<ZabbixNotFoundException>(async () => await tree.Server.HostGroups["nope"].GetLatestAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(("hostgroup", "nope"), (enumerated.ObjectType, enumerated.Name));
        Assert.Equal(("hostgroup", "nope"), (read.ObjectType, read.Name));
        Assert.IsAssignableFrom<KeyNotFoundException>(read);                               // a catch on the BCL type still sees it
    }

    [Fact]
    public async Task A_host_that_does_not_exist_throws_not_found_on_its_first_read()
    {
        using Tree tree = Build(api => api.On("item.get", "[]").On("host.get", "[]"));

        ZabbixNotFoundException missing = await Assert.ThrowsAsync<ZabbixNotFoundException>(async () => await tree.Server.Hosts["typo"].GetLatestAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(("host", "typo"), (missing.ObjectType, missing.Name));
        Assert.Equal("typo", tree.Api.ParamsOf("host.get").Single().GetProperty("filter").GetProperty("host").GetString());
    }

    [Fact]
    public async Task A_host_that_exists_with_no_items_answers_with_nothing()
    {
        using Tree tree = Build(api => api.On("item.get", "[]").On("host.get", """[{"hostid":"10"}]"""));

        Assert.Empty(await tree.Server.Hosts["empty-01"].GetLatestAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("""[{"hostid":"10"}]""", true)]
    [InlineData("[]", false)]
    public async Task ExistsAsync_tests_a_host_without_throwing(string hosts, bool exists)
    {
        using Tree tree = Build(api => api.On("host.get", hosts));

        Assert.Equal(exists, await tree.Server.Hosts["web-01"].ExistsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ExistsAsync_tests_a_group_and_the_server_always_exists()
    {
        using Tree tree = Build(api => api.On("hostgroup.get", "[]"));

        Assert.False(await tree.Server.HostGroups["nope"].ExistsAsync(TestContext.Current.CancellationToken));
        Assert.True(await tree.Server.AllHosts.ExistsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task One_unreadable_key_is_left_out_and_logged_and_every_other_item_answers()
    {
        const string items = """
            [
              {"itemid":"1","hostid":"10","key_":"key[a]]","value_type":"0","lastvalue":"1","lastclock":"1700000000","lastns":"0","hosts":[{"hostid":"10","host":"web-01"}]},
              {"itemid":"2","hostid":"10","key_":"net.tcp.service[tcp,,[80,443]]","value_type":"3","lastvalue":"1","lastclock":"1700000000","lastns":"0","hosts":[{"hostid":"10","host":"web-01"}]}
            ]
            """;
        using Tree tree = Build(api => api.On("item.get", items));

        List<ZabbixValue> values = await tree.Server.Hosts["web-01"].GetLatestAsync(TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal("net.tcp.service[tcp,,[80,443]]", Assert.Single(values).Key.ToString());   // an array key Zabbix accepts is read
        Assert.Single(tree.Logs.GetSnapshot(), record => record.Id.Id == 5005);
    }

    [Fact]
    public async Task History_never_truncates_a_second_with_more_rows_than_a_page_and_merges_value_types_in_clock_order()
    {
        var table = new FakeHistory();
        for (int ns = 0; ns < 6000; ns++) table.Add("1", ZabbixItemValueType.Float, S, ns * 1000, $"f{ns}");     // 6,000 values in one second
        for (int n = 0; n < 10; n++)
        {
            table.Add("1", ZabbixItemValueType.Float, S - 1, n, $"before{n}");
            table.Add("1", ZabbixItemValueType.Float, S + 1, n, $"after{n}");
            table.Add("2", ZabbixItemValueType.Unsigned, S - 1 + n % 3, 500 + n, $"u{n}");
        }
        using Tree tree = Build(api => api.On("item.get", Items).On("history.get", table.Answer));

        List<ZabbixValue> history = await tree.Server.Hosts["web-01"].GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(S - 1), cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(6000 + 20 + 10, history.Count);
        Assert.Equal(history.Count, history.Select(value => (value.Key, value.Clock)).Distinct().Count());             // each row exactly once
        Assert.Equal(history.Select(value => value.Clock).Order(), history.Select(value => value.Clock));               // one time line across value types
        JsonElement whole = tree.Api.ParamsOf("history.get").Single(call => call.TryGetProperty("time_till", out _));
        Assert.Equal((S, S), (whole.GetProperty("time_from").GetInt64(), whole.GetProperty("time_till").GetInt64()));
        Assert.False(whole.TryGetProperty("limit", out _));                                                           // the saturated second, read whole
        Assert.Contains(tree.Logs.GetSnapshot(), record => record.Id.Id == 5006);
    }

    [Fact]
    public async Task History_reads_only_the_instants_asked_for_to_the_nanosecond()
    {
        var table = new FakeHistory()
            .Add("1", ZabbixItemValueType.Float, S, 100, "early")
            .Add("1", ZabbixItemValueType.Float, S, 900_000_000, "in")
            .Add("1", ZabbixItemValueType.Float, S + 2, 0, "late");
        using Tree tree = Build(api => api.On("item.get", Items).On("history.get", table.Answer));

        List<ZabbixValue> history = await tree.Server.Hosts["web-01"]
            .GetHistoryAsync(new ZabbixClock(S, 500).ToDateTimeOffset(), new ZabbixClock(S + 1, 0).ToDateTimeOffset(), TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["in"], history.Select(value => value.Value));
    }

    [Fact]
    public async Task A_second_that_shrank_between_the_page_and_its_whole_read_is_logged_as_possibly_incomplete()
    {
        int call = 0;
        string Page(JsonElement parameters)
        {
            if (parameters.GetProperty("history").GetInt32() != 0) return "[]";
            call++;
            int rows = call switch { 1 => 5000, 2 => 10, _ => 0 };
            return "[" + string.Join(",", Enumerable.Range(0, rows).Select(n => $$"""{"itemid":"1","clock":"{{S}}","ns":"{{n}}","value":"{{n}}"}""")) + "]";
        }
        using Tree tree = Build(api => api.On("item.get", Items).On("history.get", Page));

        List<ZabbixValue> history = await tree.Server.Hosts["web-01"].GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(S), cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(10, history.Count);
        Assert.Contains(tree.Logs.GetSnapshot(), record => record.Id.Id == 5007);
    }

    [Fact]
    public async Task History_rows_without_a_readable_clock_are_left_out_and_logged()
    {
        using Tree tree = Build(api => api.On("item.get", Items).On("history.get", parameters => parameters.GetProperty("history").GetInt32() == 0
            ? $$"""[{"itemid":"1","clock":"{{S}}","ns":"0","value":"ok"},{"itemid":"1","ns":"0","value":"no clock"}]"""
            : "[]"));

        List<ZabbixValue> history = await tree.Server.Hosts["web-01"].GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(S), cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["ok"], history.Select(value => value.Value));
        Assert.Contains(tree.Logs.GetSnapshot(), record => record.Id.Id == 5008);
    }

    [Fact]
    public async Task Every_history_page_is_a_span_tagged_with_its_value_type()
    {
        using var capture = new TelemetryCapture(ZabbixTelemetryNames.Tree);
        var table = new FakeHistory().Add("1", ZabbixItemValueType.Float, S, 0, "x");
        using Tree tree = Build(api => api.On("item.get", Items).On("history.get", table.Answer));

        await tree.Server.Hosts["web-01"].GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(S), cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        IReadOnlyList<Activity> pages = [.. capture.Spans.Where(span => span.OperationName == ZabbixTreeTelemetry.HistoryPageSpan)];
        Assert.Equal(2, pages.Count);                                                        // one per value type the host has
        Assert.Equal([0, 3], pages.Select(span => (int)span.GetTagItem("zabbix.tree.value_type")!).Order());
        Assert.All(pages, span => Assert.Equal(ActivityStatusCode.Ok, span.Status));
    }

    [Fact]
    public async Task A_key_is_found_however_the_item_on_the_server_quotes_its_parameters()
    {
        const string Quoted = """[{"itemid":"3","hostid":"11","key_":"net.if.in[\"eth0\"]","value_type":"3","lastvalue":"42","lastclock":"100","lastns":"0","hosts":[{"hostid":"11","host":"web-01"}]},{"itemid":"4","hostid":"11","key_":"net.if.in[\"eth1\"]","value_type":"3","lastvalue":"9","lastclock":"100","lastns":"0","hosts":[{"hostid":"11","host":"web-01"}]}]""";
        using Tree tree = Build(api => api.On("item.get", parameters => parameters.TryGetProperty("search", out _) ? Quoted : "[]"));

        ulong received = await tree.Server.Hosts["web-01"].GetLastAsync(ZabbixItemKeys.Network.BytesReceived("eth0"), TestContext.Current.CancellationToken);

        Assert.Equal(42UL, received);
    }

    [Fact]
    public async Task A_missing_item_on_a_present_host_throws_not_found_the_way_a_missing_host_does()
    {
        // Absence is answered one way at every depth: an item node whose key the host does not have is
        // not "an item that never collected".
        using Tree tree = Build(api => api.On("item.get", "[]").On("host.get", """[{"hostid":"10"}]"""));
        ZabbixItemNode item = tree.Server.Hosts["web-01"].Items["no.such.key"];

        ZabbixNotFoundException read = await Assert.ThrowsAsync<ZabbixNotFoundException>(async () => await item.GetLastValueAsync(TestContext.Current.CancellationToken));
        ZabbixNotFoundException history = await Assert.ThrowsAsync<ZabbixNotFoundException>(async () => await item.GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(S), cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(("item", "no.such.key"), (read.ObjectType, read.Name));
        Assert.Equal(("item", "no.such.key"), (history.ObjectType, history.Name));
        Assert.Contains("web-01", read.Message, StringComparison.Ordinal);
        Assert.False(await item.ExistsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task An_item_that_exists_and_never_collected_is_found_null_and_got_as_not_found()
    {
        const string never = """[{"itemid":"3","hostid":"11","key_":"agent.ping","value_type":"3","lastvalue":"","lastclock":"0","lastns":"0","hosts":[{"hostid":"11","host":"db-01"}]}]""";
        using Tree tree = Build(api => api.On("item.get", never));
        ZabbixItemNode ping = tree.Server.Hosts["db-01"].Items["agent.ping"];

        Assert.Null(await ping.FindLastValueAsync(TestContext.Current.CancellationToken));
        ZabbixNotFoundException got = await Assert.ThrowsAsync<ZabbixNotFoundException>(() => ping.GetLastValueAsync(TestContext.Current.CancellationToken));
        Assert.Equal(("item", "agent.ping"), (got.ObjectType, got.Name));
        Assert.Contains("never collected", got.Message, StringComparison.Ordinal);
        Assert.Empty(tree.Api.ParamsOf("host.get"));                                          // present: no second look
    }

    [Theory]
    [InlineData("8.0.0", true)]
    [InlineData("7.4.15", false)]
    [InlineData("7.0.31", false)]
    public async Task Json_and_binary_history_asks_8_0_for_the_whole_value_and_never_sends_the_parameter_to_7_x(string version, bool whole)
    {
        // 8.0's history.get cuts types 5 and 6 at 64 KiB unless maxValueSize is null (CHistory.php 8.0:120, a default of
        // 64 * ZBX_KIBIBYTE); 7.x refuses the parameter, and 8.0 refuses it for any other type (API_UNEXPECTED).
        const string items = """
            [
              {"itemid":"1","hostid":"10","key_":"cpu","value_type":"0","lastvalue":"1","lastclock":"1700000000","lastns":"0","hosts":[{"hostid":"10","host":"web-01"}]},
              {"itemid":"5","hostid":"10","key_":"blob","value_type":"5","lastvalue":"","lastclock":"0","lastns":"0","hosts":[{"hostid":"10","host":"web-01"}]},
              {"itemid":"6","hostid":"10","key_":"doc","value_type":"6","lastvalue":"","lastclock":"0","lastns":"0","hosts":[{"hostid":"10","host":"web-01"}]}
            ]
            """;
        var table = new FakeHistory()
            .Add("1", ZabbixItemValueType.Float, S, 0, "1.5")
            .Add("5", ZabbixItemValueType.Binary, S, 1, "AAEC")
            .Add("6", ZabbixItemValueType.Json, S, 2, "{}");
        using Tree tree = Build(api => api.On("apiinfo.version", $"\"{version}\"").On("item.get", items).On("history.get", table.Answer));

        List<ZabbixValue> history = await tree.Server.Hosts["web-01"].GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(S), cancellationToken: TestContext.Current.CancellationToken).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(["1.5", "AAEC", "{}"], history.Select(value => value.Value));
        foreach (JsonElement call in tree.Api.ParamsOf("history.get"))
        {
            int type = call.GetProperty("history").GetInt32();
            bool sent = call.TryGetProperty("maxValueSize", out JsonElement size);
            Assert.Equal(whole && type is 5 or 6, sent);
            if (sent) Assert.Equal(JsonValueKind.Null, size.ValueKind);
        }
    }

    [Fact]
    public async Task The_server_itself_answers_as_the_scope_of_every_host()
    {
        using Tree tree = Build();

        int count = await tree.Server.AllHosts.GetLatestAsync(TestContext.Current.CancellationToken).CountAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, count);
        Assert.Single(tree.Api.ParamsOf("item.get"));
    }
}
