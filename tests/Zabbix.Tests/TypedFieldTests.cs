// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix.Tests;

/// <summary>
/// The fields ADR 0013 (section 5) types beyond an item's: an item key wherever a record holds one, a port wherever one is
/// a number or a macro, a length of time wherever the API takes a time unit, and a reply keyed by the ids it was asked
/// about read with those ids. Each reads what the server writes and writes what it reads.
/// </summary>
public sealed class TypedFieldTests
{
    [Fact]
    public void An_item_key_reads_the_api_s_text_and_writes_it_back_as_it_was()
    {
        ZabbixItem item = JsonSerializer.Deserialize<ZabbixItem>("""{"itemid":"7","key_":"system.cpu.load[all,avg1]"}""", ZabbixJson.Options)!;
        ZabbixItem odd = JsonSerializer.Deserialize<ZabbixItem>("""{"itemid":"8","key_":"vfs.fs.size[/,"}""", ZabbixJson.Options)!;

        Assert.Equal("system.cpu.load", item.Key?.Name);
        Assert.Equal(["all", "avg1"], item.Key?.Parameters);
        Assert.Equal("system.cpu.load[all,avg1]", JsonSerializer.SerializeToElement(new ZabbixItemUpdateRequest(7) { Key = item.Key }, ZabbixJson.Options).GetProperty("key_").GetString());
        // text outside the grammar is kept as the server wrote it, so an update sends it back unchanged
        Assert.Equal("vfs.fs.size[/,", odd.Key?.ToString());
    }

    [Fact]
    public void An_item_key_that_is_not_text_is_refused()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ZabbixItem>("""{"key_":{"name":"agent.ping"}}""", ZabbixJson.Options));

    [Fact]
    public void A_port_reads_text_or_a_number_and_writes_text_a_macro_included()
    {
        ZabbixProxy proxy = JsonSerializer.Deserialize<ZabbixProxy>("""{"proxyid":"1","port":"{$PROXY_PORT}","local_port":"10051"}""", ZabbixJson.Options)!;
        ZabbixHANode node = JsonSerializer.Deserialize<ZabbixHANode>("""{"ha_nodeid":"ckuo7i1nw000h0sajj3l3hh8u","port":10051}""", ZabbixJson.Options)!;
        ZabbixMediaType mail = JsonSerializer.Deserialize<ZabbixMediaType>("""{"mediatypeid":"1","smtp_port":"25"}""", ZabbixJson.Options)!;

        Assert.Null(proxy.Port?.Number);
        Assert.Equal("{$PROXY_PORT}", proxy.Port?.ToString());
        Assert.Equal(10051, proxy.LocalPort?.Number);
        Assert.Equal(10051, node.Port?.Number);
        Assert.Equal(25, mail.SmtpPort?.Number);
        Assert.Equal("587", JsonSerializer.SerializeToElement(new ZabbixMediaTypeUpdateRequest(1) { SmtpPort = 587 }, ZabbixJson.Options).GetProperty("smtp_port").GetString());
    }

    [Fact]
    public void A_length_of_time_reads_every_unit_the_api_takes_a_year_included()
    {
        ZabbixAction action = JsonSerializer.Deserialize<ZabbixAction>("""{"actionid":"3","esc_period":"1h"}""", ZabbixJson.Options)!;
        ZabbixCepRuleWindow window = JsonSerializer.Deserialize<ZabbixCepRuleWindow>("""{"duration":"1y"}""", ZabbixJson.Options)!;
        ZabbixSettingsRecord settings = JsonSerializer.Deserialize<ZabbixSettingsRecord>("""{"ha_failover_delay":"1m"}""", ZabbixJson.Options)!;
        ZabbixProxy proxy = JsonSerializer.Deserialize<ZabbixProxy>("""{"proxyid":"1","timeout_zabbix_agent":"{$AGENT_TIMEOUT}"}""", ZabbixJson.Options)!;

        Assert.Equal(TimeSpan.FromHours(1), action.EscalationPeriod?.Length);
        Assert.Equal(TimeSpan.FromDays(365), window.Duration?.Length);
        Assert.Equal(TimeSpan.FromMinutes(1), settings.HAFailoverDelay?.Length);
        Assert.Null(proxy.TimeoutZabbixAgent?.Length);
        Assert.Equal("{$AGENT_TIMEOUT}", proxy.TimeoutZabbixAgent?.ToString());
    }

    [Fact]
    public async Task A_reply_keyed_by_the_ids_it_was_asked_about_reads_them_as_ids()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => new ScriptedApi().On("script.getscriptsbyhosts", """{"10084":[{"scriptid":"1","name":"Ping"}],"10085":[]}"""));
        services.AddZabbixClient().Configure(options => { options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"); options.ApiToken = "tok"; });
        services.AddHttpClient(ZabbixHttp.GetClientName()).ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<ScriptedApi>());
        await using ServiceProvider provider = services.BuildValidated();

        IReadOnlyDictionary<ulong, IReadOnlyList<ZabbixScript>> scripts = await provider.GetRequiredService<ZabbixClient>().Scripts
            .GetByHostAsync([new(10084), new(10085)], TestContext.Current.CancellationToken);

        Assert.Equal([10084UL, 10085UL], scripts.Keys.Order());
        Assert.Equal("Ping", Assert.Single(scripts[10084]).Name);
        Assert.Empty(scripts[10085]);
    }
}
