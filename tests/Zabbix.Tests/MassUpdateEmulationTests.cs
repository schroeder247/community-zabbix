// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Community.Zabbix.Tests;

/// <summary>
/// The 8.0 mass-update compositions (<c>ZabbixMassUpdateEmulations.cs</c>): what the removed
/// <c>hostgroup</c>/<c>templategroup.massupdate</c> methods are rebuilt from — two reads then one batched write — is
/// not one server-side transaction the way the removed method was, and the exact sequence of calls it sends proves it.
/// </summary>
public sealed class MassUpdateEmulationTests
{
    private static (ZabbixClient Server, ScriptedApi Api) Build(ScriptedApi api)
    {
        var services = new ServiceCollection();
        services.AddZabbix().Configure(options => { options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"); options.ApiToken = "tok"; });
        services.AddHttpClient(ZabbixHttp.GetClientName()).ConfigurePrimaryHttpMessageHandler(() => api);
        ServiceProvider provider = services.BuildValidated();
        return (provider.GetRequiredService<ZabbixClient>(), api);
    }

    [Fact]
    public async Task Hostgroup_massupdate_on_8_0_is_two_reads_then_one_write_in_that_order()
    {
        using ScriptedApi api = new ScriptedApi()
            .On("apiinfo.version", "\"8.0.0\"")
            .On("host.get", p => p.TryGetProperty("hostids", out _)
                ? """[{"hostid":"1","hostgroups":[{"groupid":"7"}]}]"""
                : """[{"hostid":"2","hostgroups":[{"groupid":"5"},{"groupid":"8"}]}]""")
            .On("host.update", """{"hostids":["1","2"]}""");
        (ZabbixClient client, _) = Build(api);

        await client.HostGroups.MassUpdateAsync(new ZabbixHostGroupMassUpdateRequest
        {
            Groups = [new ZabbixHostGroupMassGroupRequest { GroupId = 5 }],
            Hosts = [new ZabbixHostGroupMassHostRequest { HostId = 1 }],
        }, TestContext.Current.CancellationToken);

        // apiinfo.version is the transport's own version probe; what the composition itself sends is exactly these three,
        // in this order — the membership read by host id, the membership read by group id, then the one batched write.
        Assert.Equal(["apiinfo.version", "host.get", "host.get", "host.update"], api.Calls.Select(c => c.Method));
        JsonElement byHosts = api.Calls[1].Params;
        Assert.Equal(["1"], byHosts.GetProperty("hostids").EnumerateArray().Select(e => e.GetString()));
        JsonElement byGroups = api.Calls[2].Params;
        Assert.Equal(["5"], byGroups.GetProperty("groupids").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Hostgroup_massupdate_refuses_a_host_left_without_a_group_before_the_write_is_sent()
    {
        // No named hosts: the by-hostids read has nothing to ask for and is skipped, so this is one read (by the
        // group's own members) and no write — the refusal is checked entirely against what that read found.
        using ScriptedApi api = new ScriptedApi()
            .On("apiinfo.version", "\"8.0.0\"")
            .On("host.get", """[{"hostid":"2","hostgroups":[{"groupid":"5"}]}]""");
        (ZabbixClient client, _) = Build(api);

        ZabbixApiException refused = await Assert.ThrowsAsync<ZabbixApiException>(() => client.HostGroups.MassUpdateAsync(new ZabbixHostGroupMassUpdateRequest
        {
            Groups = [new ZabbixHostGroupMassGroupRequest { GroupId = 5 }],
            Hosts = [],
        }, TestContext.Current.CancellationToken));

        // The native 7.0 method's own refusal, so one handler serves both versions.
        Assert.Equal(ZabbixApiErrorKind.Validation, refused.Kind);
        Assert.Equal(-32602, refused.Code);
        Assert.Equal("hostgroup.massupdate", refused.Method);
        Assert.Equal("Host \"2\" cannot be without host group.", refused.Detail);

        Assert.Equal(["apiinfo.version", "host.get"], api.Calls.Select(c => c.Method));
        Assert.Empty(api.ParamsOf("host.update"));
    }

    [Fact]
    public async Task Templategroup_massupdate_on_8_0_is_two_reads_then_one_write_in_that_order()
    {
        using ScriptedApi api = new ScriptedApi()
            .On("apiinfo.version", "\"8.0.0\"")
            .On("template.get", p => p.TryGetProperty("templateids", out _)
                ? """[{"templateid":"1","templategroups":[{"groupid":"7"}]}]"""
                : """[{"templateid":"2","templategroups":[{"groupid":"5"},{"groupid":"8"}]}]""")
            .On("template.update", """{"templateids":["1","2"]}""");
        (ZabbixClient client, _) = Build(api);

        await client.TemplateGroups.MassUpdateAsync(new ZabbixTemplateGroupMassUpdateRequest
        {
            Groups = [new ZabbixTemplateGroupMassGroupRequest { GroupId = 5 }],
            Templates = [new ZabbixTemplateGroupMassTemplateRequest { TemplateId = 1 }],
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["apiinfo.version", "template.get", "template.get", "template.update"], api.Calls.Select(c => c.Method));
        JsonElement byTemplates = api.Calls[1].Params;
        Assert.Equal(["1"], byTemplates.GetProperty("templateids").EnumerateArray().Select(e => e.GetString()));
        JsonElement byGroups = api.Calls[2].Params;
        Assert.Equal(["5"], byGroups.GetProperty("groupids").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Templategroup_massupdate_refuses_a_template_left_without_a_group_before_the_write_is_sent()
    {
        using ScriptedApi api = new ScriptedApi()
            .On("apiinfo.version", "\"8.0.0\"")
            .On("template.get", """[{"templateid":"2","host":"linux","templategroups":[{"groupid":"5"}]}]""");
        (ZabbixClient client, _) = Build(api);

        ZabbixApiException refused = await Assert.ThrowsAsync<ZabbixApiException>(() => client.TemplateGroups.MassUpdateAsync(new ZabbixTemplateGroupMassUpdateRequest
        {
            Groups = [new ZabbixTemplateGroupMassGroupRequest { GroupId = 5 }],
            Templates = [],
        }, TestContext.Current.CancellationToken));

        Assert.Equal(ZabbixApiErrorKind.Validation, refused.Kind);
        Assert.Equal("templategroup.massupdate", refused.Method);
        Assert.Equal("Template \"linux\" cannot be without template group.", refused.Detail);

        Assert.Equal(["apiinfo.version", "template.get"], api.Calls.Select(c => c.Method));
        Assert.Empty(api.ParamsOf("template.update"));
    }

    [Fact]
    public async Task Template_massupdate_on_8_0_is_one_template_update_with_the_linked_templates_under_their_update_name()
    {
        // No macros named: nothing to read, one write. template.update names the linked templates "templates" where
        // template.massupdate said "templates_link" (CTemplate.php 8.0).
        using ScriptedApi api = new ScriptedApi()
            .On("apiinfo.version", "\"8.0.0\"")
            .On("template.update", """{"templateids":["1","2"]}""");
        (ZabbixClient client, _) = Build(api);

        IReadOnlyList<ulong> ids = await client.Templates.MassUpdateAsync(new ZabbixTemplateMassUpdateRequest
        {
            Templates = [new ZabbixTemplateMassTemplateRequest { TemplateId = 1 }, new ZabbixTemplateMassTemplateRequest { TemplateId = 2 }],
            Groups = [new ZabbixTemplateMassGroupRequest { GroupId = 5 }],
            LinkedTemplates = [new ZabbixTemplateMassTemplatesLinkRequest { TemplateId = 9 }],
        }, TestContext.Current.CancellationToken);

        Assert.Equal<ulong>([1, 2], ids);
        Assert.Equal(["apiinfo.version", "template.update"], api.Calls.Select(c => c.Method));
        JsonElement update = api.ParamsOf("template.update").Single();
        Assert.Equal(["1", "2"], update.EnumerateArray().Select(row => row.GetProperty("templateid").GetString()));
        Assert.All(update.EnumerateArray(), row =>
        {
            Assert.Equal("9", row.GetProperty("templates")[0].GetProperty("templateid").GetString());
            Assert.False(row.TryGetProperty("templates_link", out _));
            Assert.Equal("5", row.GetProperty("groups")[0].GetProperty("groupid").GetString());
        });
    }

    [Fact]
    public async Task Template_massupdate_on_8_0_with_macros_reads_the_existing_macro_ids_first_and_attaches_them()
    {
        using ScriptedApi api = new ScriptedApi()
            .On("apiinfo.version", "\"8.0.0\"")
            .On("template.get", """[{"templateid":"1","macros":[{"hostmacroid":"91","macro":"{$A}"}]},{"templateid":"2","macros":[]}]""")
            .On("template.update", """{"templateids":["1","2"]}""");
        (ZabbixClient client, _) = Build(api);

        await client.Templates.MassUpdateAsync(new ZabbixTemplateMassUpdateRequest
        {
            Templates = [new ZabbixTemplateMassTemplateRequest { TemplateId = 1 }, new ZabbixTemplateMassTemplateRequest { TemplateId = 2 }],
            Macros = [new ZabbixTemplateMassMacroRequest { Macro = "{$A}", Value = "x" }],
        }, TestContext.Current.CancellationToken);

        Assert.Equal(["apiinfo.version", "template.get", "template.update"], api.Calls.Select(c => c.Method));
        Assert.Equal(["1", "2"], api.Calls[1].Params.GetProperty("templateids").EnumerateArray().Select(id => id.GetString()));
        JsonElement update = api.ParamsOf("template.update").Single();
        Assert.Equal("91", update[0].GetProperty("macros")[0].GetProperty("hostmacroid").GetString());
        Assert.False(update[1].GetProperty("macros")[0].TryGetProperty("hostmacroid", out _));
        Assert.Empty(api.ParamsOf("template.massupdate"));
    }

    [Fact]
    public async Task Template_massupdate_is_sent_as_itself_where_the_server_has_it()
    {
        using ScriptedApi api = new ScriptedApi().On("apiinfo.version", "\"7.4.15\"").On("template.massupdate", """{"templateids":["1"]}""");
        (ZabbixClient client, _) = Build(api);

        IReadOnlyList<ulong> ids = await client.Templates.MassUpdateAsync(new ZabbixTemplateMassUpdateRequest
        {
            Templates = [new ZabbixTemplateMassTemplateRequest { TemplateId = 1 }],
            LinkedTemplates = [new ZabbixTemplateMassTemplatesLinkRequest { TemplateId = 9 }],
        }, TestContext.Current.CancellationToken);

        Assert.Equal<ulong>([1], ids);
        Assert.Equal("9", api.ParamsOf("template.massupdate").Single().GetProperty("templates_link")[0].GetProperty("templateid").GetString());
        Assert.Empty(api.ParamsOf("template.update"));
    }
}
