// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Text;
using System.Text.Json;
using Community.Zabbix.Protocol;

namespace Community.Zabbix.Tests;

/// <summary>
/// The measured facts, re-measured. Every server fact the design rests on (what the Zabbix source says and the
/// server behaviour the SDK relies on) is a named probe here, run against every version, so a new Zabbix release
/// that changes one fails the fact rather than the code that relied on it. Each probe's summary states the
/// fact it pins. Facts about .NET, OpenSSL or the source alone are not server facts and live with their own probes.
/// </summary>
public sealed class LiveServerFactTests
{
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    /// <summary>The matrix is what it says: each label's server reports that major.minor (the API reports 8.0.0rc1 as <c>8.0.0</c>).</summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task Each_server_reports_the_version_its_label_names(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "fact-version");

        Version reported = await scope.VersionAsync();

        Assert.Equal(version, reported.ToString(2));
    }

    /// <summary>API error text is always English, and a refused login is -32500 with upstream's exact text (<c>Classify</c> keys on it).</summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_refused_login_is_minus_32500_with_the_english_text_the_classifier_matches(string version)
    {
        LiveTarget target = LiveMatrix.Require(version);
        using var anonymous = new ZabbixClient(target.Endpoint, ZabbixCredential.Anonymous);

        // An unknown user, never Admin: failed attempts against a real user block it for everyone.
        ZabbixApiException refused = await Assert.ThrowsAsync<ZabbixApiException>(() => anonymous.RpcAsync("user.login", new { username = LiveMatrix.Name("nobody"), password = "x" }));

        Assert.Equal(-32500, refused.Code);
        Assert.Equal("Incorrect user name or password or account is temporarily blocked.", refused.Detail);
        Assert.Equal(ZabbixApiErrorKind.LoginFailed, refused.Kind);
    }

    /// <summary>"No permissions for system access." is -32602 (a user whose group is disabled), and the classifier still calls it a failed login.</summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_user_of_a_disabled_group_is_refused_system_access_as_minus_32602(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "fact-noaccess");
        string group = (await scope.Server.RpcAsync("usergroup.create", new { name = scope.Name("group"), users_status = 1 })).Id("usrgrpids");
        scope.Delete("usergroup", group);
        string password = "Zbx-" + Guid.NewGuid().ToString("N");
        JsonElement role = await scope.Server.RpcAsync("role.get", new { output = new[] { "roleid" }, filter = new { name = new[] { "User role" } } });
        string user = (await scope.Server.RpcAsync("user.create", new { username = scope.Name("user"), passwd = password, roleid = role[0].GetProperty("roleid").GetString(), usrgrps = new[] { new { usrgrpid = group } } })).Id("userids");
        scope.Delete("user", user);
        using var anonymous = new ZabbixClient(scope.Target.Endpoint, ZabbixCredential.Anonymous);

        ZabbixApiException refused = await Assert.ThrowsAsync<ZabbixApiException>(() => anonymous.RpcAsync("user.login", new { username = scope.Name("user"), password }));

        Assert.Equal(-32602, refused.Code);
        Assert.Equal("No permissions for system access.", refused.Detail);
        Assert.Equal(ZabbixApiErrorKind.LoginFailed, refused.Kind);
    }

    /// <summary>Enums sent as JSON numbers are accepted (the SDK writes numbers), and read back as the API's strings.</summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task Enums_written_as_json_numbers_are_accepted(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "fact-enum");
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "enum", [], r => r with { Status = ZabbixHostStatus.Disabled, InventoryMode = ZabbixHostInventoryMode.Manual });

        JsonElement read = await scope.Server.RpcAsync("host.get", new { hostids = new[] { host.HostId }, output = new[] { "status", "inventory_mode" } });

        Assert.Equal("1", read[0].GetProperty("status").GetString());
        Assert.Equal("0", read[0].GetProperty("inventory_mode").GetString());
    }

    /// <summary>A JSON boolean in <c>filter</c> is silently no match on 7.x and refused on 8.0 — never emit one.</summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_boolean_filter_value_matches_nothing_on_7_and_is_refused_on_8(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "fact-boolfilter");
        await LiveFixtures.HostAsync(scope, "boolfilter", []);
        var parameters = new { output = new[] { "hostid" }, filter = new { status = true }, search = new { host = scope.Run } };

        if (version == "8.0")
        {
            ZabbixApiException refused = await Assert.ThrowsAsync<ZabbixApiException>(() => scope.Server.RpcAsync("host.get", parameters));
            Assert.Equal("Invalid parameter \"/filter/status\": an array is expected.", refused.Detail);
        }
        else
        {
            Assert.Equal(0, (await scope.Server.RpcAsync("host.get", parameters)).GetArrayLength());   // status 0 exists: the true is "no match"
        }
    }

    /// <summary>
    /// <c>searchByAny</c> ORs across the fields of <c>filter</c> (and of <c>search</c>), but <c>filter</c> and
    /// <c>search</c> are still ANDed with each other (measured: a filter hit and a search hit give nothing together, with or
    /// without the flag); <c>search</c> takes a list of patterns per field.
    /// </summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task SearchByAny_ors_across_fields_but_filter_and_search_stay_anded_and_search_takes_a_list(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "fact-searchbyany");
        LiveFixtures.LiveHost a = await LiveFixtures.HostAsync(scope, "alpha", []);
        LiveFixtures.LiveHost b = await LiveFixtures.HostAsync(scope, "bravo", [], r => r with { VisibleName = scope.Name("bravo-visible") });
        LiveFixtures.LiveHost c = await LiveFixtures.HostAsync(scope, "charlie", []);

        JsonElement fieldsAny = await scope.Server.RpcAsync("host.get", new { output = new[] { "host" }, filter = new { host = new[] { a.Host }, name = new[] { scope.Name("bravo-visible") } }, searchByAny = true });
        JsonElement fieldsAll = await scope.Server.RpcAsync("host.get", new { output = new[] { "host" }, filter = new { host = new[] { a.Host }, name = new[] { scope.Name("bravo-visible") } } });
        JsonElement filterAndSearch = await scope.Server.RpcAsync("host.get", new { output = new[] { "host" }, filter = new { host = new[] { a.Host } }, search = new { host = scope.Run + "-bravo" }, searchByAny = true });
        JsonElement list = await scope.Server.RpcAsync("host.get", new { output = new[] { "host" }, search = new { host = new[] { scope.Run + "-bravo", scope.Run + "-charlie" } }, searchByAny = true });

        Assert.Equal([a.Host, b.Host], Names(fieldsAny, "host"));
        Assert.Empty(Names(fieldsAll, "host"));
        Assert.Empty(Names(filterAndSearch, "host"));
        Assert.Equal([b.Host, c.Host], Names(list, "host"));
    }

    /// <summary>With <c>searchWildcardsEnabled</c> a pattern without <c>*</c> matches only the exact value, not a substring.</summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task SearchWildcardsEnabled_without_a_star_matches_only_the_exact_value(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "fact-wildcards");
        await LiveFixtures.HostAsync(scope, "wild", []);

        JsonElement substring = await scope.Server.RpcAsync("host.get", new { output = new[] { "host" }, search = new { host = scope.Run } });
        JsonElement flagged = await scope.Server.RpcAsync("host.get", new { output = new[] { "host" }, search = new { host = scope.Run }, searchWildcardsEnabled = true });
        JsonElement starred = await scope.Server.RpcAsync("host.get", new { output = new[] { "host" }, search = new { host = scope.Run + "*" }, searchWildcardsEnabled = true });

        Assert.Single(Names(substring, "host"));
        Assert.Empty(Names(flagged, "host"));
        Assert.Single(Names(starred, "host"));
    }

    /// <summary><c>time_from</c> is inclusive and whole-second; <c>sortfield: [clock, ns]</c> is accepted and orders within a second.</summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task History_time_from_is_an_inclusive_second_and_clock_ns_sorts_within_it(string version)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "fact-timefrom");
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "timefrom", [("zbxtest.u", ZabbixItemValueType.Unsigned)]);
        long second = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        using ZabbixSender sender = new ZabbixSender([trapper]);
        await LiveFixtures.SendWhenKnownAsync(sender, [
            new ZabbixValue(host.Host, "zbxtest.u", "1", new ZabbixClock(second - 1, 999_999_999)),
            new ZabbixValue(host.Host, "zbxtest.u", "3", new ZabbixClock(second, 20)),
            new ZabbixValue(host.Host, "zbxtest.u", "2", new ZabbixClock(second, 10))]);
        await LiveFixtures.HistoryAsync(scope.Server, host["zbxtest.u"], 3);

        IReadOnlyList<ZabbixHistoryRecord> from = await scope.Server.History.GetAsync(new ZabbixHistoryGetParameters
        {
            History = ZabbixItemValueType.Unsigned,
            ItemIds = [host["zbxtest.u"].ItemId],
            TimeFrom = DateTimeOffset.FromUnixTimeSeconds(second),
            Sort = [ZabbixHistoryRecordFields.Clock, ZabbixHistoryRecordFields.ClockNanoseconds],
            Output = ZabbixOutput.Extend,
        }, Cancel);

        Assert.Equal(["2", "3"], from.Select(r => r.Value));
        Assert.Equal([10, 20], from.Select(r => r.ClockExact!.Value.Nanoseconds));
    }

    /// <summary><c>maxValueSize</c> is refused by 7.x; 8.0 cuts JSON (and binary) values at 64 KiB unless it is <c>null</c>.</summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task History_value_size_is_cut_at_64_KiB_on_8_0_unless_maxValueSize_is_null_and_7_x_refuses_the_parameter(string version)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "fact-maxvaluesize");
        if (version != "8.0")
        {
            ZabbixApiException refused = await Assert.ThrowsAsync<ZabbixApiException>(() => scope.Server.RpcAsync("history.get", new { history = 4, itemids = new[] { "1" }, maxValueSize = (int?)null }));
            Assert.Equal("Invalid parameter \"/\": unexpected parameter \"maxValueSize\".", refused.Detail);
            return;
        }
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "maxvaluesize", [("zbxtest.json", ZabbixItemValueType.Json)]);
        string big = "{\"a\":\"" + new string('x', 100_000) + "\"}";
        using ZabbixSender sender = new ZabbixSender([trapper]);
        await LiveFixtures.SendWhenKnownAsync(sender, [new ZabbixValue(host.Host, "zbxtest.json", big, DateTimeOffset.UtcNow)]);
        await LiveFixtures.HistoryAsync(scope.Server, host["zbxtest.json"], 1);

        JsonElement cut = await scope.Server.RpcAsync("history.get", new { history = 6, itemids = new[] { host["zbxtest.json"].ItemId }, output = new[] { "value" } });
        JsonElement whole = await scope.Server.RpcAsync("history.get", new { history = 6, itemids = new[] { host["zbxtest.json"].ItemId }, output = new[] { "value" }, maxValueSize = (int?)null });

        Assert.Equal(64 * 1024, cut[0].GetProperty("value").GetString()!.Length);
        // 8.0 stores a JSON value re-serialised ({"a": "…"}, a space after the colon), so compare it as JSON.
        using JsonDocument sent = JsonDocument.Parse(big), stored = JsonDocument.Parse(whole[0].GetProperty("value").GetString()!);
        Assert.True(JsonElement.DeepEquals(sent.RootElement, stored.RootElement));
    }

    /// <summary>8.0's <c>trend.get</c> refuses <c>sortfield</c>/<c>filter</c>/<c>search</c>; 7.x accepts them.</summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task Trend_get_refuses_sortfield_filter_and_search_on_8_0_only(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "fact-trend");
        (string Name, object Parameters)[] probes =
        [
            ("sortfield", new { itemids = new[] { "1" }, sortfield = "clock" }),
            ("filter", new { itemids = new[] { "1" }, filter = new { clock = new[] { 1 } } }),
            ("search", new { itemids = new[] { "1" }, search = new { clock = "1" } }),
        ];
        foreach ((string name, object parameters) in probes)
        {
            if (version == "8.0")
                Assert.Equal($"Invalid parameter \"/\": unexpected parameter \"{name}\".", (await Assert.ThrowsAsync<ZabbixApiException>(() => scope.Server.RpcAsync("trend.get", parameters))).Detail);
            else
                Assert.Equal(JsonValueKind.Array, (await scope.Server.RpcAsync("trend.get", parameters)).ValueKind);
        }
    }

    /// <summary><c>task.get</c> takes only <c>taskids</c>/<c>output</c>/<c>preservekeys</c>.</summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task Task_get_takes_only_taskids_output_and_preservekeys(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "fact-task");

        JsonElement accepted = await scope.Server.RpcAsync("task.get", new { taskids = new[] { "1" }, output = "extend", preservekeys = true });
        ZabbixApiException refused = await Assert.ThrowsAsync<ZabbixApiException>(() => scope.Server.RpcAsync("task.get", new { output = "extend", limit = 1 }));

        Assert.NotEqual(JsonValueKind.Undefined, accepted.ValueKind);
        Assert.Equal("Invalid parameter \"/\": unexpected parameter \"limit\".", refused.Detail);
    }

    /// <summary>A fresh server's <c>ha_failover_delay</c> is 1m and <c>search_limit</c> 1000; a standalone server lists itself as one active node.</summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_standalone_server_is_one_active_node_with_the_default_failover_delay(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "fact-hanode");

        JsonElement settings = await scope.Server.RpcAsync("settings.get", new { output = new[] { "ha_failover_delay", "search_limit" } });
        JsonElement nodes = await scope.Server.RpcAsync("hanode.get", new { output = new[] { "address", "port", "status" } });

        Assert.Equal("1m", settings.GetProperty("ha_failover_delay").GetString());
        Assert.Equal("1000", settings.GetProperty("search_limit").GetString());
        JsonElement node = Assert.Single(nodes.EnumerateArray());
        Assert.Equal("3", node.GetProperty("status").GetString());                            // ZBX_NODE_STATUS_ACTIVE
        Assert.Equal("10051", node.GetProperty("port").GetString());                          // the container's port, never the host's
    }

    /// <summary>
    /// 8.0 defaults a trapper item's <c>trapper_hosts</c> to <c>{$TRAPPER.ALLOWED_HOSTS}</c> and 7.x to empty; an
    /// empty list denies every sender on 8.0 and skips the check on 7.x.
    /// </summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task Trapper_hosts_defaults_and_its_empty_value_differ_between_7_and_8(string version)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "fact-trapperhosts");
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "trapperhosts", []);
        LiveFixtures.LiveItem inherited = await LiveFixtures.ItemAsync(scope, host, "zbxtest.default", ZabbixItemValueType.Unsigned, ZabbixItemType.Trapper);
        LiveFixtures.LiveItem empty = await LiveFixtures.ItemAsync(scope, host, "zbxtest.empty", ZabbixItemValueType.Unsigned, ZabbixItemType.Trapper, ""","trapper_hosts":"" """);
        // The probe is created LAST and accepts everyone explicitly (on 8.0 the default refuses): once a value to it is
        // processed, the configuration cache holds a snapshot taken after it was committed, so the two items committed
        // before it are in that snapshot too. A probe created with the host says only that the HOST is cached
        // (on a loaded runner zbxtest.empty may not be yet).
        await LiveFixtures.ItemAsync(scope, host, "zbxtest.probe", ZabbixItemValueType.Unsigned, ZabbixItemType.Trapper, ""","trapper_hosts":"0.0.0.0/0" """);
        using var sender = new ZabbixSender([trapper]);
        await LiveFixtures.SendWhenKnownAsync(sender, [new ZabbixValue(host.Host, "zbxtest.probe", "1", DateTimeOffset.UtcNow)]);

        JsonElement read = await scope.Server.RpcAsync("item.get", new { itemids = new[] { inherited.ItemId }, output = new[] { "trapper_hosts" } });
        ZabbixSenderResponse toEmpty = await sender.SendAsync([new ZabbixValue(host.Host, "zbxtest.empty", "1", DateTimeOffset.UtcNow)], Cancel);

        Assert.Equal(version == "8.0" ? "{$TRAPPER.ALLOWED_HOSTS}" : "", read[0].GetProperty("trapper_hosts").GetString());
        Assert.Equal(version == "8.0" ? (0, 1) : (1, 0), (toEmpty.Processed, toEmpty.Failed));
    }

    /// <summary>
    /// Unicode round-trips through the API verbatim, and the server's defaults for a minimal host are what the SDK
    /// documents, compared field by field (the rest printed, so a new default shows up in the output).
    /// </summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_minimal_host_reads_back_with_the_server_s_defaults_and_a_unicode_name_verbatim(string version)
    {
        await using LiveScope scope = LiveScope.Open(version, "fact-defaults");
        string visible = scope.Name("grüße ✓ 漢字 Ω");
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "defaults", [], r => r with { VisibleName = visible });

        JsonElement read = (await scope.Server.RpcAsync("host.get", new { hostids = new[] { host.HostId }, output = "extend" }))[0];
        IReadOnlyList<ZabbixHost> typed = await scope.Server.Hosts.GetAsync(new ZabbixHostGetParameters { HostIds = [host.HostId], Output = ZabbixOutput.Extend }, Cancel);

        foreach (JsonProperty field in read.EnumerateObject()) TestContext.Current.TestOutputHelper?.WriteLine($"{version} host.{field.Name} = {field.Value}");
        Assert.Equal(visible, read.GetProperty("name").GetString());
        Assert.Equal(visible, Assert.Single(typed).VisibleName);
        Dictionary<string, string> expected = new(StringComparer.Ordinal)
        {
            ["status"] = "0",
            ["flags"] = "0",
            ["inventory_mode"] = "-1",
            ["ipmi_authtype"] = "-1",
            ["ipmi_privilege"] = "2",
            ["tls_connect"] = "1",
            ["tls_accept"] = "1",
            ["maintenance_status"] = "0",
            ["monitored_by"] = "0",
            ["proxyid"] = "0",
            ["description"] = "",
            ["active_available"] = "0",
        };
        Assert.All(expected, pair => Assert.Equal((pair.Key, pair.Value), (pair.Key, read.GetProperty(pair.Key).GetString())));
        // Every field the server sent reads into the record or its bag: nothing is dropped.
        Assert.All(read.EnumerateObject(), field => Assert.True(
            ((IZabbixAdditionalFields)typed[0]).AdditionalFields?.ContainsKey(field.Name) == true || typeof(ZabbixHost).GetProperties().Any(p => p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonPropertyNameAttribute), false).Cast<System.Text.Json.Serialization.JsonPropertyNameAttribute>().Any(a => a.Name == field.Name)),
            $"{version}: host.{field.Name} is neither modelled nor in AdditionalFields"));
    }

    /// <summary>
    /// The tree's history read at exactly its page size (<c>ZabbixHistoryRead.PageSize</c>, 5,000): a page that ends on a
    /// second is re-read whole, so 5,000 values over 5,000 seconds and 5,001 values in one saturated second both read back
    /// exactly, once each, in (clock, ns) order.
    /// </summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task The_tree_reads_history_exactly_at_the_page_boundary_and_across_a_saturated_second(string version)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "fact-paging");
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "paging", [("zbxtest.spread", ZabbixItemValueType.Unsigned), ("zbxtest.saturated", ZabbixItemValueType.Unsigned)]);
        const int Page = 5000;
        long start = DateTimeOffset.UtcNow.AddHours(-3).ToUnixTimeSeconds();
        ZabbixValue[] spread = [.. Enumerable.Range(0, Page).Select(i => new ZabbixValue(host.Host, "zbxtest.spread", i.ToString(CultureInfo.InvariantCulture), new ZabbixClock(start + i, 0)))];
        ZabbixValue[] saturated = [.. Enumerable.Range(0, Page + 1).Select(i => new ZabbixValue(host.Host, "zbxtest.saturated", i.ToString(CultureInfo.InvariantCulture), new ZabbixClock(start, i)))];
        using var sender = new ZabbixSender([trapper]);
        // One probe per item, before the read window, until the trapper knows it: a request that arrives while its item is
        // reaching the configuration cache is processed in part, so the batches go only once both are known.
        await LiveFixtures.SendWhenKnownAsync(sender, [new ZabbixValue(host.Host, "zbxtest.spread", "0", new ZabbixClock(start - 60, 0))]);
        await LiveFixtures.SendWhenKnownAsync(sender, [new ZabbixValue(host.Host, "zbxtest.saturated", "0", new ZabbixClock(start - 60, 0))]);
        ZabbixSenderResponse spreadSent = await sender.SendAsync(spread, Cancel);
        ZabbixSenderResponse saturatedSent = await sender.SendAsync(saturated, Cancel);
        Assert.Equal((Page, Page + 1), (spreadSent.Processed, saturatedSent.Processed));
        // 10,001 rows through the history syncers: minutes on a loaded server, so a longer wait on the same observable.
        await LiveFixtures.HistoryAsync(scope.Server, host["zbxtest.spread"], Page + 1, TimeSpan.FromMinutes(4));
        await LiveFixtures.HistoryAsync(scope.Server, host["zbxtest.saturated"], Page + 2, TimeSpan.FromMinutes(4));

        List<ZabbixValue> spreadRead = [], saturatedRead = [];
        await foreach (ZabbixValue v in scope.Server.Hosts[host.Host].Item("zbxtest.spread").GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(start), cancellationToken: Cancel)) spreadRead.Add(v);
        await foreach (ZabbixValue v in scope.Server.Hosts[host.Host].Item("zbxtest.saturated").GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(start), cancellationToken: Cancel)) saturatedRead.Add(v);

        Assert.Equal(spread.Select(v => (v.Value, v.Clock)), spreadRead.Select(v => (v.Value, v.Clock)));
        Assert.Equal(saturated.Select(v => (v.Value, v.Clock)), saturatedRead.Select(v => (v.Value, v.Clock)));
    }

    // ----- the wire ------------------------------------------------------------------------------------------------

    /// <summary>A successful <c>active check heartbeat</c> gets no reply — the server closes the connection.</summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_heartbeat_gets_no_reply(string version)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "fact-heartbeat");
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "heartbeat", []);
        byte[] heartbeat = Encoding.UTF8.GetBytes($$"""{"request":"active check heartbeat","host":"{{host.Host}}","heartbeat_freq":60,"version":"{{version}}.0","variant":2}""");

        await ZabbixFrames.Notify([trapper])(heartbeat, Cancel);                              // the SDK's notification: written, nothing awaited
        Exception silent = await Assert.ThrowsAnyAsync<Exception>(async () => await ZabbixFrames.Connect(trapper, new ZabbixConnectionOptions { Timeout = TimeSpan.FromSeconds(5) })(heartbeat, Cancel));

        Assert.True(silent is InvalidDataException or IOException, $"{silent.GetType().Name}: {silent.Message}");
    }

    /// <summary>An agent's reported version gates nothing on 7.0–8.0 (only agents older than 4.4 get another answer).</summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task The_active_checks_answer_does_not_depend_on_the_agent_s_version(string version)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "fact-agentversion");
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "agentversion", []);
        await LiveFixtures.ItemAsync(scope, host, "zbxtest.active", ZabbixItemValueType.Unsigned, ZabbixItemType.ZabbixActive, ""","delay":"1m" """);
        ZabbixExchangeCallback exchange = ZabbixFrames.Connect(trapper);

        async Task<string?> AskAsync(string agentVersion)
        {
            ReadOnlyMemory<byte> reply = await exchange(Encoding.UTF8.GetBytes($$"""{"request":"active checks","host":"{{host.Host}}","version":"{{agentVersion}}","variant":2,"config_revision":0,"session":"{{Guid.NewGuid():N}}"}"""), Cancel);
            using JsonDocument document = JsonDocument.Parse(reply);
            // Absent until the host AND its item are in the configuration cache: a host that arrived before its item answers
            // success with an empty list (seen on a loaded runner), which is not yet the answer under test.
            return document.RootElement.GetProperty("response").GetString() == "success" && document.RootElement.TryGetProperty("data", out JsonElement data)
                && string.Join(",", data.EnumerateArray().Select(i => i.GetProperty("key").GetString())) is { Length: > 0 } keys
                ? keys
                : null;
        }

        string keys = await LiveApi.EventuallyAsync(() => AskAsync(version + ".0"), "the host and its item in the configuration cache");

        Assert.Equal("zbxtest.active", keys);
        Assert.Equal(keys, await AskAsync("6.0.0"));
        Assert.Equal(keys, await AskAsync("7.0.0"));
        Assert.Equal(keys, await AskAsync("8.0.0"));
    }

    /// <summary>
    /// A server sends configuration only to a proxy that announces its own major.minor; any other version is
    /// refused ("proxy and server major versions do not match"), and the refusal carries the server's version in its
    /// <c>version</c> field (which the SDK proxy adopts).
    /// </summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_proxy_gets_configuration_only_when_it_announces_the_server_s_major_minor(string version)
    {
        (_, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "fact-proxyversion");
        string proxy = scope.Name("proxy");
        scope.Delete("proxy", (await scope.Server.RpcAsync("proxy.create", new { name = proxy, operating_mode = 0 })).Id("proxyids"));
        ZabbixExchangeCallback exchange = ZabbixFrames.Connect(trapper, new ZabbixConnectionOptions { MaxPayloadBytes = ZabbixHeader.MaxLargePayload });

        async Task<(string? Response, string? Info)> AskAsync(string announced)
        {
            try
            {
                ReadOnlyMemory<byte> reply = await exchange(Encoding.UTF8.GetBytes($$"""{"request":"proxy config","host":"{{proxy}}","version":"{{announced}}","session":"{{Guid.NewGuid():N}}","config_revision":0}"""), Cancel);
                using JsonDocument document = JsonDocument.Parse(reply);
                JsonElement root = document.RootElement;
                return (root.TryGetProperty("response", out JsonElement r) ? r.GetString() : "config",
                        (root.TryGetProperty("info", out JsonElement i) ? i.GetString() : null) + " version=" + (root.TryGetProperty("version", out JsonElement v) ? v.GetString() : null));
            }
            catch (InvalidDataException) { return (null, null); }                               // the proxy is not in the cache yet: the server closes
        }

        (string? Response, string? Info) own = await LiveApi.EventuallyAsync<(string?, string?)?>(async () => await AskAsync(version + ".0") is { Response: not null } answer ? answer : null, "the proxy in the configuration cache") ?? default;
        Assert.NotEqual("failed", own.Response);
        foreach (string other in new[] { "7.0.0", "7.4.0", "8.0.0" }.Where(v => !v.StartsWith(version, StringComparison.Ordinal)))
        {
            (string? response, string? info) = await AskAsync(other);
            TestContext.Current.TestOutputHelper?.WriteLine($"{version} server, proxy {other}: {response} {info}");
            Assert.Equal("failed", response);
            Assert.StartsWith("proxy and server major versions do not match", info, StringComparison.Ordinal);
            Assert.Contains(" version=" + version, info, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The server wire answers <c>queue.get</c> to an API token sent as the session id; <c>zabbix.stats</c> is
    /// answered only where <c>StatsAllowedIP</c> admits this host, and refused with a typed exception elsewhere.
    /// </summary>
    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task The_server_wire_takes_an_api_token_as_its_session_and_zabbix_stats_follows_StatsAllowedIP(string version)
    {
        (LiveTarget target, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        using var wire = new ZabbixServerWire(trapper, target.Token);

        IReadOnlyList<ZabbixQueueDelays> queue = await wire.GetQueueAsync(ZabbixQueueOverview.ByItemType, Cancel);
        ZabbixQueueDetails details = await wire.GetQueueDetailsAsync(5, Cancel);

        Assert.NotNull(queue);
        Assert.True(details.Items.Count <= 5);
        if (target.Features.Contains("connector"))                                              // the lab definition opens StatsAllowedIP
            Assert.Equal(JsonValueKind.Object, (await wire.GetStatsAsync(Cancel)).ValueKind);
        else
            Assert.Contains("Permission denied", (await Assert.ThrowsAsync<ZabbixServerRefusedException>(() => wire.GetStatsAsync(Cancel))).Info ?? string.Empty, StringComparison.Ordinal);
    }

    private static List<string?> Names(JsonElement rows, string field) => [.. rows.EnumerateArray().Select(r => r.GetProperty(field).GetString()).Order(StringComparer.Ordinal)];
}
