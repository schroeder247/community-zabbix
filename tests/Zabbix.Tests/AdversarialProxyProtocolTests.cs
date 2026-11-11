// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text;
using System.Text.Json;
using Community.Zabbix.Proxy;

namespace Community.Zabbix.Tests;

/// <summary>
/// Adversarial tests of the proxy's side of the server conversation: a server whose answers are not what the
/// protocol says (a non-string <c>response</c>, no <c>info</c>, a version the proxy cannot read, the same refusal after the
/// version was adopted, a non-object reply, a task the proxy does not know) and a configuration that contradicts itself
/// (two hosts of one name, a <c>fields</c> array that is not names, an item whose type changes between syncs). Every one
/// must end as a failure the loops treat as transient (<see cref="ZabbixProxyRefusedException"/> or
/// <see cref="JsonException"/>), never an exception that stops a loop.
/// </summary>
public sealed class AdversarialProxyProtocolTests
{
    private const string Session = "0123456789abcdef0123456789abcdef";

    private sealed class Scripted(params string[] replies)
    {
        private readonly Queue<string> _replies = new(replies);

        public int Calls { get; private set; }

        public ZabbixExchangeCallback Exchange => (request, _) =>
        {
            Calls++;
            string reply = _replies.Count > 1 ? _replies.Dequeue() : _replies.Peek();
            return ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(reply));
        };
    }

    private static ZabbixProxyUplink Uplink(Scripted server) => new(server.Exchange, "proxy-01", new Version(7, 4, 0), Session, loggers: null, time: null);

    private static ZabbixProxyConfig Merge(ZabbixProxyConfig held, string reply)
    {
        using JsonDocument document = JsonDocument.Parse(reply);
        return held.Merge(document.RootElement);
    }

    private static bool IsTransient(Exception? failure) => failure is ZabbixProxyRefusedException or JsonException;

    [Theory]
    [InlineData("""{"response":1}""")]
    [InlineData("""{"response":null}""")]
    [InlineData("""{"response":{"nested":true}}""")]
    [InlineData("""{"response":"failed","info":5}""")]
    [InlineData("""{"response":"failed","info":"x","version":7}""")]
    public async Task A_config_reply_whose_fields_are_not_strings_is_a_transient_failure_not_a_defect(string reply)
    {
        ZabbixProxyUplink uplink = Uplink(new Scripted(reply));

        Exception? thrown = await Record.ExceptionAsync(() => uplink.GetConfigAsync(ZabbixProxyConfig.Empty, TestContext.Current.CancellationToken));

        Assert.True(IsTransient(thrown), $"{thrown?.GetType().Name}: {thrown?.Message}");
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"proxy config\"")]
    [InlineData("42")]
    [InlineData("{\"data\":")]
    [InlineData("")]
    public async Task A_config_reply_that_is_not_a_json_object_is_a_json_failure(string reply)
    {
        ZabbixProxyUplink uplink = Uplink(new Scripted(reply));

        Exception? thrown = await Record.ExceptionAsync(() => uplink.GetConfigAsync(ZabbixProxyConfig.Empty, TestContext.Current.CancellationToken));

        Assert.True(IsTransient(thrown), $"{thrown?.GetType().Name}: {thrown?.Message}");
    }

    [Fact]
    public async Task A_refusal_without_info_says_no_reason_was_given()
    {
        ZabbixProxyUplink uplink = Uplink(new Scripted("""{"response":"failed"}"""));

        ZabbixProxyRefusedException refused = await Assert.ThrowsAsync<ZabbixProxyRefusedException>(() => uplink.GetConfigAsync(ZabbixProxyConfig.Empty, TestContext.Current.CancellationToken));

        Assert.Null(refused.Info);
        Assert.Contains("proxy config", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("banana")]
    [InlineData("")]
    [InlineData("7")]
    [InlineData("99999999999.0.0")]
    public async Task A_refusal_naming_a_version_the_proxy_cannot_read_is_not_adopted(string version)
    {
        var server = new Scripted($$"""{"response":"failed","info":"proxy and server major versions do not match","version":"{{version}}"}""");
        ZabbixProxyUplink uplink = Uplink(server);

        await Assert.ThrowsAsync<ZabbixProxyRefusedException>(() => uplink.GetConfigAsync(ZabbixProxyConfig.Empty, TestContext.Current.CancellationToken));

        Assert.Equal(1, server.Calls);
        Assert.Equal(new Version(7, 4, 0), uplink.Version);
    }

    [Fact]
    public async Task A_server_that_refuses_again_after_its_version_was_adopted_is_asked_twice_not_for_ever()
    {
        var server = new Scripted("""{"response":"failed","info":"proxy and server major versions do not match","version":"8.0.0"}""");
        ZabbixProxyUplink uplink = Uplink(server);

        await Assert.ThrowsAsync<ZabbixProxyRefusedException>(() => uplink.GetConfigAsync(ZabbixProxyConfig.Empty, TestContext.Current.CancellationToken));

        Assert.Equal(2, server.Calls);
        Assert.Equal(new Version(8, 0, 0), uplink.Version);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{\"response\":1}")]
    [InlineData("not json")]
    public async Task A_data_reply_that_is_not_the_protocol_s_object_is_a_json_failure(string reply)
    {
        ZabbixProxyUplink uplink = Uplink(new Scripted(reply));

        Exception? thrown = await Record.ExceptionAsync(() => uplink.ProbeAsync(TestContext.Current.CancellationToken));

        Assert.IsAssignableFrom<JsonException>(thrown);
    }

    [Fact]
    public async Task A_data_reply_with_trailing_members_the_proxy_does_not_know_is_read()
    {
        ZabbixProxyUplink uplink = Uplink(new Scripted("""{"response":"success","upload":"enabled","future":{"a":[1,2]},"redirect":null}"""));

        ZabbixProxyResponse response = await uplink.ProbeAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsSuccess);
        Assert.False(response.UploadDisabled);
    }

    [Fact]
    public void Tasks_of_a_type_the_proxy_does_not_know_are_read_with_their_type_and_unknown_members_ignored()
    {
        ZabbixProxyResponse response = JsonSerializer.Deserialize(
            """{"response":"success","tasks":[{"type":99,"clock":1700000000,"ttl":3600,"data":{"x":1}},{"type":6,"clock":1,"ttl":0,"itemid":5}]}""",
            ZabbixProxyJson.Default.ZabbixProxyResponse)!;

        IReadOnlyList<ZabbixProxyTask> tasks = response.DeserializeTasks();

        Assert.Equal([(ZabbixProxyTaskType)99, ZabbixProxyTaskType.CheckNow], tasks.Select(task => task.Type));                  // an unknown type reads as its number
    }

    [Fact]
    public void A_config_naming_two_hosts_with_one_technical_name_is_refused_as_invalid_and_the_held_one_stays()
    {
        const string Reply = """
            {"full_sync":1,"config_revision":2,"data":{"hosts":{"fields":["hostid","host","status"],"data":[[10,"web-01",0],[11,"web-01",0]]}}}
            """;

        Exception? thrown = Record.Exception(() => Merge(ZabbixProxyConfig.Empty, Reply));

        Assert.IsAssignableFrom<JsonException>(thrown);
    }

    [Fact]
    public void A_delta_that_gives_a_new_host_the_name_of_a_held_one_without_removing_it_is_refused_as_invalid()
    {
        ZabbixProxyConfig held = Merge(ZabbixProxyConfig.Empty, """{"full_sync":1,"config_revision":1,"data":{"hosts":{"fields":["hostid","host","status"],"data":[[10,"web-01",0]]}}}""");

        Exception? thrown = Record.Exception(() => Merge(held, """{"config_revision":2,"data":{"hosts":{"fields":["hostid","host","status"],"data":[[11,"web-01",0]]}}}"""));

        Assert.IsAssignableFrom<JsonException>(thrown);
    }

    [Fact]
    public void A_delta_that_swaps_two_host_names_is_taken()
    {
        ZabbixProxyConfig held = Merge(ZabbixProxyConfig.Empty, """{"full_sync":1,"config_revision":1,"data":{"hosts":{"fields":["hostid","host","status"],"data":[[10,"a",0],[11,"b",0]]}}}""");

        ZabbixProxyConfig swapped = Merge(held, """{"config_revision":2,"data":{"hosts":{"fields":["hostid","host","status"],"data":[[10,"b",0],[11,"a",0]]}}}""");

        Assert.Equal(11UL, swapped.HostsByName["a"].HostId);
        Assert.Equal(10UL, swapped.HostsByName["b"].HostId);
    }

    [Theory]
    [InlineData("""{"config_revision":2,"data":{"hosts":{"fields":["hostid",7,"status"],"data":[[10,"web-01",0]]}}}""")]
    [InlineData("""{"config_revision":2,"data":{"hosts":{"fields":["hostid",null,"status"],"data":[[10,"web-01",0]]}}}""")]
    [InlineData("""{"config_revision":2,"data":{"hosts":{"fields":"hostid","data":[]}}}""")]
    [InlineData("""{"config_revision":2,"data":{"hosts":{"fields":["hostid","host"],"data":[[10]]}}}""")]
    public void A_table_whose_fields_are_not_names_is_refused_as_invalid(string reply)
    {
        Exception? thrown = Record.Exception(() => Merge(ZabbixProxyConfig.Empty, reply));

        Assert.IsAssignableFrom<JsonException>(thrown);
    }

    [Fact]
    public void An_item_whose_type_changes_between_syncs_is_admitted_by_its_new_type()
    {
        const string Tables = "\"hosts\":{\"fields\":[\"hostid\",\"host\",\"status\"],\"data\":[[10,\"web-01\",0]]},"
            + "\"items\":{\"fields\":[\"itemid\",\"type\",\"hostid\",\"key_\",\"status\",\"trapper_hosts\",\"allow_traps\"],\"data\":[[100,TYPE,10,\"cpu\",0,\"\",0]]}";
        ZabbixProxyConfig trapper = Merge(ZabbixProxyConfig.Empty, "{\"full_sync\":1,\"config_revision\":1,\"data\":{" + Tables.Replace("TYPE", "2", StringComparison.Ordinal) + "}}");
        ZabbixProxyConfig active = Merge(trapper, "{\"config_revision\":2,\"data\":{" + Tables.Replace("TYPE", "7", StringComparison.Ordinal) + "}}");

        Assert.Equal(ZabbixItemType.Trapper, trapper.Items[100].Type);
        Assert.Equal(ZabbixItemType.ZabbixActive, active.Items[100].Type);
        Assert.Same(active.Items[100], active.ItemsByKey[("web-01", "cpu")]);
    }

    [Fact]
    public void A_delta_that_removes_a_host_also_removes_its_items_and_its_key_lookup()
    {
        ZabbixProxyConfig held = Merge(ZabbixProxyConfig.Empty, """{"full_sync":1,"config_revision":1,"data":{"hosts":{"fields":["hostid","host","status"],"data":[[10,"web-01",0]]},"items":{"fields":["itemid","type","hostid","key_","status"],"data":[[100,2,10,"cpu",0]]}}}""");

        ZabbixProxyConfig removed = Merge(held, """{"config_revision":2,"del_hostids":[10]}""");

        Assert.Empty(removed.Hosts);
        Assert.Empty(removed.Items);
        Assert.Empty(removed.ItemsByKey);
        Assert.Equal(2, removed.ConfigRevision);
    }
}
