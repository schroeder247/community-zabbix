// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix.Tests;

/// <summary>
/// <c>item.create</c> from a typed key (ADR 0013, section 3): the key's text is the item's key and, until the lambda names
/// the item, its name; the key's type sets the value type the item stores, an integer key, signed or not, making an
/// unsigned item, and a value type passed with the key wins.
/// </summary>
public sealed class ItemCreateTests
{
    private sealed record Fluent(ZabbixClient Zabbix, ScriptedApi Api, ServiceProvider Provider) : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }

    private static Fluent Build()
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => new ScriptedApi().On("item.create", """{"itemids":["501"]}"""));
        services.AddZabbixClient().Configure(options => { options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"); options.ApiToken = "tok"; });
        services.AddHttpClient(ZabbixHttp.GetClientName()).ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<ScriptedApi>());
        ServiceProvider provider = services.BuildValidated();
        return new Fluent(provider.GetRequiredService<ZabbixClient>(), provider.GetRequiredService<ScriptedApi>(), provider);
    }

    // The value type item.create carries, as the wire number.
    private static async Task<int> ValueTypeSentFor<T>()
        where T : ISpanParsable<T>
    {
        using Fluent fluent = Build();
        await fluent.Zabbix.Items.CreateAsync(10084UL, new ZabbixItemKey<T>("app.value"), ZabbixItemType.Trapper, TestContext.Current.CancellationToken);
        return fluent.Api.ParamsOf("item.create").Single().GetProperty("value_type").GetInt32();
    }

    [Fact]
    public async Task A_key_s_type_sets_the_value_type_its_values_fit()
    {
        // Unsigned (3) for a whole number, 1 and 0 and seconds since the epoch; Float (0) for fractions and a TimeSpan's
        // seconds; Text (4) for text and any other type.
        Assert.Equal(3, await ValueTypeSentFor<ulong>());
        Assert.Equal(3, await ValueTypeSentFor<long>());
        Assert.Equal(3, await ValueTypeSentFor<int>());
        Assert.Equal(3, await ValueTypeSentFor<byte>());
        Assert.Equal(3, await ValueTypeSentFor<bool>());
        Assert.Equal(3, await ValueTypeSentFor<DateTimeOffset>());
        Assert.Equal(0, await ValueTypeSentFor<double>());
        Assert.Equal(0, await ValueTypeSentFor<decimal>());
        Assert.Equal(0, await ValueTypeSentFor<TimeSpan>());
        Assert.Equal(4, await ValueTypeSentFor<string>());
        Assert.Equal(4, await ValueTypeSentFor<Guid>());
    }

    [Fact]
    public async Task The_item_s_key_and_name_are_the_key_s_text_until_the_lambda_names_it()
    {
        using Fluent fluent = Build();
        var pending = new ZabbixItemKey<ulong>(ZabbixItemKey.Create("orders.pending", "eu", "web shop"));

        ulong id = await fluent.Zabbix.Items.CreateAsync(10084UL, pending, ZabbixItemType.Trapper, TestContext.Current.CancellationToken);
        await fluent.Zabbix.Items.CreateAsync(10084UL, pending, ZabbixItemType.Trapper, i => i.Name = "Pending orders", TestContext.Current.CancellationToken);

        Assert.Equal(501UL, id);
        JsonElement[] sent = [.. fluent.Api.ParamsOf("item.create")];
        Assert.Equal("10084", sent[0].GetProperty("hostid").ToString());
        Assert.Equal("orders.pending[eu,web shop]", sent[0].GetProperty("key_").GetString());
        Assert.Equal("orders.pending[eu,web shop]", sent[0].GetProperty("name").GetString());
        Assert.Equal(2, sent[0].GetProperty("type").GetInt32());
        Assert.Equal("Pending orders", sent[1].GetProperty("name").GetString());
        Assert.Equal("orders.pending[eu,web shop]", sent[1].GetProperty("key_").GetString());
    }

    [Fact]
    public async Task A_signed_key_makes_an_unsigned_item_as_a_counter_s_is()
    {
        // ADR 0013's own example: a ZabbixItemKey<long> counter, named by the lambda. Zabbix refuses a negative value for an
        // unsigned item when it arrives, not here; a key whose values can be negative passes Float (the next test).
        using Fluent fluent = Build();
        ZabbixItemKey<long> pending = new("orders.pending");

        ulong id = await fluent.Zabbix.Items.CreateAsync(10084UL, pending, ZabbixItemType.Trapper, i => i.Name = "Pending", TestContext.Current.CancellationToken);
        await fluent.Zabbix.Items.CreateAsync(10084UL, new ZabbixItemKey<int>("orders.failed"), ZabbixItemType.Trapper, TestContext.Current.CancellationToken);

        Assert.Equal(501UL, id);
        JsonElement[] sent = [.. fluent.Api.ParamsOf("item.create")];
        Assert.Equal([3, 3], sent.Select(item => item.GetProperty("value_type").GetInt32()));
        Assert.Equal("Pending", sent[0].GetProperty("name").GetString());
        Assert.Equal("orders.pending", sent[0].GetProperty("key_").GetString());
    }

    [Fact]
    public async Task A_value_type_passed_with_the_key_is_the_one_sent()
    {
        // A key whose values can be negative passes Float, which wins over the unsigned its type gives; a key of text is a
        // character or a log item when it says so.
        using Fluent fluent = Build();

        await fluent.Zabbix.Items.CreateAsync(10084UL, new ZabbixItemKey<long>("account.balance"), ZabbixItemType.Trapper, ZabbixItemValueType.Float, TestContext.Current.CancellationToken);
        await fluent.Zabbix.Items.CreateAsync(10084UL, new ZabbixItemKey<string>("app.state"), ZabbixItemType.Trapper, ZabbixItemValueType.Character, TestContext.Current.CancellationToken);
        await fluent.Zabbix.Items.CreateAsync(10084UL, new ZabbixItemKey<string>("app.log"), ZabbixItemType.Trapper, ZabbixItemValueType.Log, i => i.LogTimeFormat = "yyyy-MM-dd hh:mm:ss", TestContext.Current.CancellationToken);

        JsonElement[] sent = [.. fluent.Api.ParamsOf("item.create")];
        Assert.Equal([0, 1, 2], sent.Select(item => item.GetProperty("value_type").GetInt32()));
        Assert.Equal("yyyy-MM-dd hh:mm:ss", sent[2].GetProperty("logtimefmt").GetString());
        Assert.Equal("app.log", sent[2].GetProperty("name").GetString());
    }

    [Fact]
    public async Task The_default_key_is_refused_before_anything_is_sent()
    {
        using Fluent fluent = Build();

        ArgumentException refused = await Assert.ThrowsAsync<ArgumentException>(() => fluent.Zabbix.Items.CreateAsync(10084UL, default(ZabbixItemKey<ulong>), ZabbixItemType.Trapper, TestContext.Current.CancellationToken));

        Assert.Equal("key", refused.ParamName);
        Assert.Empty(fluent.Api.Calls);
    }
}
