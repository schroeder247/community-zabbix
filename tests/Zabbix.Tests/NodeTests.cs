// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix.Tests;

/// <summary>
/// The nodes as ADR 0013 (section 4) shapes them: a host reached by id or by name is one node type with the same members, and
/// so is a host group; a host's items are an indexer, <c>host.Items["system.uptime"]</c>; a <c>Get…Async</c> throws when there's
/// nothing and its <c>Find…Async</c> returns <see langword="null"/>. And the client the container hands out is shared
/// (section 1): a caller's dispose leaves it running.
/// </summary>
public sealed class NodeTests
{
    private const string WebItems = """
        [
          {"itemid":"1","hostid":"10084","key_":"system.cpu.load[all,avg1]","value_type":"0","lastvalue":"0.42","lastclock":"1700000000","lastns":"0","hosts":[{"hostid":"10084","host":"web-01"}]},
          {"itemid":"2","hostid":"10084","key_":"agent.ping","value_type":"3","lastvalue":"1","lastclock":"1700000001","lastns":"0","hosts":[{"hostid":"10084","host":"web-01"}]}
        ]
        """;

    private sealed record Nodes(ZabbixClient Zabbix, ScriptedApi Api, ServiceProvider Provider) : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }

    private static Nodes Build(Func<ScriptedApi, ScriptedApi> script)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_ => script(new ScriptedApi()));                                     // the container owns it
        services.AddZabbixClient().Configure(options => { options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"); options.ApiToken = "tok"; });
        services.AddHttpClient(ZabbixHttp.GetClientName()).ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<ScriptedApi>());
        ServiceProvider provider = services.BuildValidated();
        return new Nodes(provider.GetRequiredService<ZabbixClient>(), provider.GetRequiredService<ScriptedApi>(), provider);
    }

    // ----- one node type by id and by name -------------------------------------------------------------------------------

    [Fact]
    public void A_host_and_a_host_group_by_id_are_the_nodes_their_names_reach_with_the_same_members()
    {
        using Nodes nodes = Build(api => api);

        ZabbixHostNode byId = nodes.Zabbix.Hosts[10084];
        ZabbixHostNode byName = nodes.Zabbix.Hosts["web-01"];
        ZabbixHostGroupNode groupById = nodes.Zabbix.HostGroups[5];
        ZabbixHostGroupNode groupByName = nodes.Zabbix.HostGroups["Linux servers"];

        Assert.Equal(byName.GetType(), byId.GetType());
        Assert.Equal(groupByName.GetType(), groupById.GetType());
        Assert.Equal(typeof(ZabbixHostClient).GetProperty("Item", [typeof(string)])!.PropertyType, typeof(ZabbixHostClient).GetProperty("Item", [typeof(ulong)])!.PropertyType);
        Assert.Equal(typeof(ZabbixHostGroupClient).GetProperty("Item", [typeof(string)])!.PropertyType, typeof(ZabbixHostGroupClient).GetProperty("Item", [typeof(ulong)])!.PropertyType);
        Assert.Equal((10084UL, (string?)null), (byId.Id, byId.Name));
        Assert.Equal(((ulong?)null, "web-01"), (byName.Id, byName.Name));
        Assert.Equal((5UL, (string?)null), (groupById.Id, groupById.Name));
        Assert.Empty(nodes.Api.Calls);                                                             // no call until asked
        string[] verbs = ["GetAsync", "FindAsync", "UpdateAsync", "DeleteAsync", "GetLastAsync", "FindLastAsync", "GetHistoryAsync", "GetItemsAsync", "GetLatestAsync"];
        Assert.All(verbs, verb => Assert.NotEmpty(typeof(ZabbixHostNode).GetMember(verb, BindingFlags.Public | BindingFlags.Instance)));
        Assert.NotNull(typeof(ZabbixHostNode).GetProperty(nameof(ZabbixHostNode.Items)));
    }

    [Fact]
    public async Task A_host_by_id_reads_changes_and_deletes_by_its_id_without_a_lookup()
    {
        using Nodes nodes = Build(api => api
            .On("host.get", """[{"hostid":"10084","host":"web-01","name":"Web 01","status":"0"}]""")
            .On("host.update", """{"hostids":["10084"]}""")
            .On("host.delete", """{"hostids":["10084"]}""")
            .On("item.get", WebItems));
        ZabbixHostNode host = nodes.Zabbix.Hosts[10084];

        ZabbixHost read = await host.GetAsync(TestContext.Current.CancellationToken);
        await host.UpdateAsync(h => h.Status = ZabbixHostStatus.Disabled, TestContext.Current.CancellationToken);
        double load = await host.GetLastAsync(ZabbixItemKeys.Cpu.Load(), TestContext.Current.CancellationToken);
        await host.DeleteAsync(TestContext.Current.CancellationToken);

        Assert.Equal("web-01", read.TechnicalName);
        Assert.Equal(0.42, load);
        Assert.Equal("10084", Assert.Single(nodes.Api.ParamsOf("host.get")).GetProperty("hostids")[0].ToString());   // the read, and no other
        Assert.Equal("10084", Assert.Single(nodes.Api.ParamsOf("host.update")).GetProperty("hostid").ToString());
        Assert.Equal("10084", Assert.Single(nodes.Api.ParamsOf("host.delete"))[0].ToString());
        Assert.Equal("10084", Assert.Single(nodes.Api.ParamsOf("item.get")).GetProperty("hostids")[0].ToString());
    }

    [Fact]
    public async Task A_host_by_name_reads_its_id_first_to_change_or_delete_it()
    {
        using Nodes nodes = Build(api => api
            .On("host.get", """[{"hostid":"10084","host":"web-01"}]""")
            .On("host.update", """{"hostids":["10084"]}""")
            .On("host.delete", """{"hostids":["10084"]}"""));
        ZabbixHostNode host = nodes.Zabbix.Hosts["web-01"];

        await host.UpdateAsync(h => h.Status = ZabbixHostStatus.Disabled, TestContext.Current.CancellationToken);
        await host.DeleteAsync(TestContext.Current.CancellationToken);

        Assert.All(nodes.Api.ParamsOf("host.get"), sent => Assert.Equal("web-01", sent.GetProperty("filter").GetProperty("host").GetString()));
        Assert.Equal(2, nodes.Api.ParamsOf("host.get").Count());
        Assert.Equal("10084", Assert.Single(nodes.Api.ParamsOf("host.update")).GetProperty("hostid").ToString());
        Assert.Equal("10084", Assert.Single(nodes.Api.ParamsOf("host.delete"))[0].ToString());
    }

    [Fact]
    public async Task A_host_group_by_id_reaches_its_hosts_and_changes_and_deletes_by_its_id()
    {
        using Nodes nodes = Build(api => api
            .On("hostgroup.get", """[{"groupid":"5","name":"Linux servers"}]""")
            .On("host.get", """[{"hostid":"10084","host":"web-01"}]""")
            .On("hostgroup.update", """{"groupids":["5"]}""")
            .On("hostgroup.delete", """{"groupids":["5"]}"""));
        ZabbixHostGroupNode group = nodes.Zabbix.HostGroups[5];

        List<ZabbixHostNode> hosts = await group.Hosts.ToListAsync(TestContext.Current.CancellationToken);
        await group.UpdateAsync(g => g.Name = "Linux", TestContext.Current.CancellationToken);
        await group.DeleteAsync(TestContext.Current.CancellationToken);

        Assert.Equal((10084UL, "web-01"), (Assert.Single(hosts).Id, hosts[0].Name));
        Assert.Equal("5", Assert.Single(nodes.Api.ParamsOf("hostgroup.get")).GetProperty("groupids")[0].ToString());
        Assert.Equal("5", Assert.Single(nodes.Api.ParamsOf("host.get")).GetProperty("groupids")[0].ToString());
        Assert.Equal("5", Assert.Single(nodes.Api.ParamsOf("hostgroup.update")).GetProperty("groupid").ToString());
        Assert.Equal("5", Assert.Single(nodes.Api.ParamsOf("hostgroup.delete"))[0].ToString());
    }

    // ----- items by indexer ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_host_s_items_are_an_indexer_by_key_text_or_key_and_an_enumeration()
    {
        using Nodes nodes = Build(api => api.On("item.get", WebItems));
        ZabbixHostNode host = nodes.Zabbix.Hosts["web-01"];

        ZabbixItemNode byText = host.Items["system.cpu.load[all,avg1]"];
        ZabbixItemNode byKey = host.Items[ZabbixItemKeys.Cpu.Load().Key];
        Assert.Empty(nodes.Api.Calls);                                                             // no call until asked
        ZabbixValue value = await byText.GetLastValueAsync(TestContext.Current.CancellationToken);
        List<ZabbixItemNode> all = await host.Items.ToListAsync(TestContext.Current.CancellationToken);

        Assert.Equal(byText.Key, byKey.Key);
        Assert.Same(host, byKey.Host);
        Assert.Equal("0.42", value.Value);
        Assert.Equal(["system.cpu.load[all,avg1]", "agent.ping"], all.Select(item => item.Key.ToString()));
        Assert.Equal("system.cpu.load[all,avg1]", nodes.Api.ParamsOf("item.get").First().GetProperty("filter").GetProperty("key_").GetString());
        Assert.Throws<FormatException>(() => host.Items["not a key["]);
    }

    [Fact]
    public async Task An_item_of_a_host_by_id_reads_by_the_host_s_id()
    {
        const string ping = """[{"itemid":"2","hostid":"10084","key_":"agent.ping","value_type":"3","lastvalue":"1","lastclock":"1700000001","lastns":"0","hosts":[{"hostid":"10084","host":"web-01"}]}]""";
        using Nodes nodes = Build(api => api.On("item.get", ping).On("host.get", "[]"));

        ZabbixValue value = await nodes.Zabbix.Hosts[10084].Items["agent.ping"].GetLastValueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(("1", "web-01"), (value.Value, value.Host));
        Assert.Equal("10084", Assert.Single(nodes.Api.ParamsOf("item.get")).GetProperty("hostids")[0].ToString());
    }

    // ----- Get throws, Find returns null ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_get_of_nothing_throws_not_found_and_its_find_returns_null()
    {
        using Nodes nodes = Build(api => api.On("host.get", "[]").On("hostgroup.get", "[]").On("item.get", "[]"));

        Assert.Null(await nodes.Zabbix.Hosts[10084].FindAsync(TestContext.Current.CancellationToken));
        Assert.Null(await nodes.Zabbix.Hosts["nope"].FindAsync(TestContext.Current.CancellationToken));
        Assert.Null(await nodes.Zabbix.HostGroups[5].FindAsync(TestContext.Current.CancellationToken));
        Assert.Null(await nodes.Zabbix.Hosts["nope"].Items["agent.ping"].FindAsync(TestContext.Current.CancellationToken));
        ZabbixNotFoundException byId = await Assert.ThrowsAsync<ZabbixNotFoundException>(() => nodes.Zabbix.Hosts[10084].GetAsync(TestContext.Current.CancellationToken));
        ZabbixNotFoundException byName = await Assert.ThrowsAsync<ZabbixNotFoundException>(() => nodes.Zabbix.Hosts["nope"].GetAsync(TestContext.Current.CancellationToken));
        ZabbixNotFoundException group = await Assert.ThrowsAsync<ZabbixNotFoundException>(() => nodes.Zabbix.HostGroups[5].GetAsync(TestContext.Current.CancellationToken));
        ZabbixNotFoundException item = await Assert.ThrowsAsync<ZabbixNotFoundException>(() => nodes.Zabbix.Hosts[10084].Items["agent.ping"].GetAsync(TestContext.Current.CancellationToken));
        ZabbixNotFoundException byIdItems = await Assert.ThrowsAsync<ZabbixNotFoundException>(async () => await nodes.Zabbix.Hosts[10084].Items.ToListAsync(TestContext.Current.CancellationToken));

        Assert.Equal(("host", 10084UL, (string?)null), (byId.ObjectType, byId.Id, byId.Name));
        Assert.Equal(("host", (ulong?)null, "nope"), (byName.ObjectType, byName.Id, byName.Name));
        Assert.Equal(("hostgroup", 5UL), (group.ObjectType, group.Id));
        Assert.Equal(("host", 10084UL), (item.ObjectType, item.Id));                                  // the host, not the key, is what's absent
        Assert.Equal(("host", 10084UL), (byIdItems.ObjectType, byIdItems.Id));
        Assert.Contains("with the id 10084", byId.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_key_a_present_host_does_not_have_is_found_null_and_got_as_not_found()
    {
        using Nodes nodes = Build(api => api.On("host.get", """[{"hostid":"10084"}]""").On("item.get", "[]"));
        ZabbixItemNode absent = nodes.Zabbix.Hosts[10084].Items["no.such.key"];

        Assert.Null(await absent.FindAsync(TestContext.Current.CancellationToken));
        ZabbixNotFoundException got = await Assert.ThrowsAsync<ZabbixNotFoundException>(() => absent.GetAsync(TestContext.Current.CancellationToken));
        ZabbixNotFoundException last = await Assert.ThrowsAsync<ZabbixNotFoundException>(() => absent.GetLastValueAsync(TestContext.Current.CancellationToken));

        Assert.Equal(("item", "no.such.key"), (got.ObjectType, got.Name));
        Assert.Equal(("item", "no.such.key"), (last.ObjectType, last.Name));
        Assert.Contains("host with the id 10084", got.Message, StringComparison.Ordinal);
    }

    // ----- the generated user.login and user.logout ----------------------------------------------------------------------

    [Fact]
    public async Task User_login_and_logout_are_log_on_and_log_off_as_the_client_s_own_is()
    {
        using Nodes nodes = Build(api => api.On("user.logout", "true"));

        await nodes.Zabbix.Users.LogOffAsync(TestContext.Current.CancellationToken);

        Assert.Single(nodes.Api.ParamsOf("user.logout"));
        string[] names = [.. typeof(ZabbixUserClient).GetMethods().Concat(typeof(ZabbixClient).GetMethods()).Select(method => method.Name)];
        Assert.Contains(nameof(ZabbixUserClient.LogOnAsync), names);
        Assert.Contains(nameof(ZabbixUserClient.LogOffAsync), names);
        Assert.Contains(nameof(ZabbixClient.LogOffAsync), names);
        Assert.DoesNotContain(names, name => name is "LoginAsync" or "LogoutAsync" or "LogInAsync" or "LogOutAsync");
    }

    // ----- a shared client survives a caller's dispose -------------------------------------------------------------------

    [Fact]
    public async Task A_client_from_the_container_or_its_factory_survives_a_caller_s_dispose_and_the_container_releases_it()
    {
        using Nodes nodes = Build(api => api.On("host.get", "[]"));
        ZabbixClient injected = nodes.Zabbix;
        ZabbixClient created = nodes.Provider.GetRequiredService<IZabbixClientFactory>().Create(Microsoft.Extensions.Options.Options.DefaultName);

#pragma warning disable CA1849 // both disposals are what this pins
        injected.Dispose();
#pragma warning restore CA1849
        await created.DisposeAsync();
        await injected.Hosts.GetAsync(TestContext.Current.CancellationToken);                      // still running
        Assert.Same(injected, created);
        Assert.Single(nodes.Api.ParamsOf("host.get"));

        await nodes.Provider.DisposeAsync();                                                       // the container releases it

        Assert.Throws<ObjectDisposedException>(() => injected.AllHosts);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => injected.GetApiVersionAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_named_client_from_the_factory_survives_a_caller_s_dispose()
    {
        var services = new ServiceCollection();
        services.AddZabbixClient("prod").WithEndpoint("https://zabbix.example/").WithToken("token");
        using ServiceProvider provider = services.BuildValidated();
        ZabbixClient prod = provider.GetRequiredService<IZabbixClientFactory>().Create("prod");

        using (prod)
        {
        }

        Assert.Equal("prod", prod.Name);
        Assert.NotNull(prod.AllHosts);
    }

    [Fact]
    public async Task A_client_the_caller_builds_or_constructs_is_disposed_by_the_caller()
    {
        ZabbixClient built = ZabbixClient.CreateBuilder().WithEndpoint("https://zabbix.example/").WithToken("token").Build();
        ZabbixClient constructed = new("https://zabbix.example/", "token");

#pragma warning disable CA1849 // both disposals are what this pins
        built.Dispose();
#pragma warning restore CA1849
        await constructed.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => built.AllHosts);
        Assert.Throws<ObjectDisposedException>(() => constructed.AllHosts);
    }
}
