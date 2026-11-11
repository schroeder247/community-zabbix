// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>Membership and tags: <c>Add/Remove-ZbxHostGroupMember</c>, <c>Add/Remove-ZbxHostTemplate</c>, <c>Add/Remove-ZbxTag</c>.</summary>
public sealed class MembershipAndTagsCommandsTests
{
    [Fact]
    public async Task Add_ZbxHostGroupMember_resolves_both_names_then_massadds()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("hostgroup.get", """[{"groupid":"7","name":"Web servers"}]""");
        server.On("host.massadd", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Add-ZbxHostGroupMember web-01 'Web servers'");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.massadd"));
        Assert.Equal("10084", Assert.Single(request.GetProperty("hosts").EnumerateArray()).GetProperty("hostid").GetString());
        Assert.Equal("7", Assert.Single(request.GetProperty("groups").EnumerateArray()).GetProperty("groupid").GetString());
    }

    [Fact]
    public async Task Remove_ZbxHostGroupMember_massremoves()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("hostgroup.get", """[{"groupid":"7","name":"Web servers"}]""");
        server.On("host.massremove", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Remove-ZbxHostGroupMember web-01 'Web servers' -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.massremove"));
        Assert.Equal("10084", Assert.Single(request.GetProperty("hostids").EnumerateArray()).GetString());
        Assert.Equal("7", Assert.Single(request.GetProperty("groupids").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Add_ZbxHostTemplate_links_the_template()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("template.get", """[{"templateid":"9","host":"Base"}]""");
        server.On("host.massadd", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Add-ZbxHostTemplate web-01 Base");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.massadd"));
        Assert.Equal("9", Assert.Single(request.GetProperty("templates").EnumerateArray()).GetProperty("templateid").GetString());
    }

    [Fact]
    public async Task Remove_ZbxHostTemplate_unlinks_the_template()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        server.On("template.get", """[{"templateid":"9","host":"Base"}]""");
        server.On("host.massremove", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Remove-ZbxHostTemplate web-01 Base -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.massremove"));
        Assert.Equal("9", Assert.Single(request.GetProperty("templateids").EnumerateArray()).GetString());
    }

    [Fact]
    public async Task Add_ZbxTag_merges_with_the_hosts_current_tags()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", parameters => parameters.TryGetProperty("selectTags", out _)
            ? """[{"hostid":"10084","host":"web-01","tags":[{"tag":"env","value":"prod"}]}]"""
            : FakeZabbix.Hosts(("10084", "web-01")));
        server.On("host.update", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Add-ZbxTag tier front -InputObject web-01 -Force -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.update"));
        JsonElement update = Assert.Single(request.EnumerateArray());
        JsonElement[] tags = [.. update.GetProperty("tags").EnumerateArray()];
        Assert.Equal(2, tags.Length);
        Assert.Contains(tags, tag => tag.GetProperty("tag").GetString() == "env" && tag.GetProperty("value").GetString() == "prod");
        Assert.Contains(tags, tag => tag.GetProperty("tag").GetString() == "tier" && tag.GetProperty("value").GetString() == "front");
    }

    [Fact]
    public async Task Remove_ZbxTag_drops_only_the_named_tag()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", parameters => parameters.TryGetProperty("selectTags", out _)
            ? """[{"hostid":"10084","host":"web-01","tags":[{"tag":"env","value":"prod"},{"tag":"tier","value":"front"}]}]"""
            : FakeZabbix.Hosts(("10084", "web-01")));
        server.On("host.update", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Remove-ZbxTag tier -InputObject web-01 -Force -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.update"));
        JsonElement update = Assert.Single(request.EnumerateArray());
        JsonElement tag = Assert.Single(update.GetProperty("tags").EnumerateArray());
        Assert.Equal("env", tag.GetProperty("tag").GetString());
    }

    [Theory]
    [InlineData("Add-ZbxTag tier front -InputObject web-01 -Force -PassThru -Confirm:$false")]
    [InlineData("Remove-ZbxTag tier -InputObject web-01 -Force -PassThru -Confirm:$false")]
    public async Task Add_and_Remove_ZbxTag_PassThru_reread_the_hosts_with_their_tags(string command)
    {
        // The -PassThru re-read carries selectTags, so the written host's Tags is not $null.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", parameters => parameters.TryGetProperty("selectTags", out _)
            ? """[{"hostid":"10084","host":"web-01","tags":[{"tag":"env","value":"prod"}]}]"""
            : FakeZabbix.Hosts(("10084", "web-01")));
        server.On("host.update", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("(" + command + ").Tags.Name");

        Assert.Empty(run.Errors);
        Assert.Equal("env", Assert.Single(run.Output).ToString());
        Assert.All(server.ParamsOf("host.get").Skip(1), parameters => Assert.True(parameters.TryGetProperty("selectTags", out _)));
    }

    [Fact]
    public async Task Tag_records_piped_into_Add_ZbxTag_InputObject_bind_Name_and_Value_by_property_name()
    {
        // The "copy tags from one host to another" idiom, (Get-ZbxHost -SelectTags).Tags |
        // Add-ZbxTag -InputObject $other. A read tag record (Tag, with the Name alias) and a [pscustomobject] with Name
        // both bind; every piped tag lands in ONE host.update. The line passes neither -Force nor -Confirm:
        // adding a tag is ShouldProcess alone (ConfirmImpact.Medium).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", parameters => parameters.TryGetProperty("selectTags", out _)
            ? """[{"hostid":"10084","host":"web-01","tags":[{"tag":"env","value":"prod"}]}]"""
            : FakeZabbix.Hosts(("10084", "web-01")));
        server.On("host.update", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            @([Community.Zabbix.ZabbixTag]@{ tag = 'a'; value = '1' }, [pscustomobject]@{ Name = 'b'; Value = '2' }) |
                Add-ZbxTag -InputObject web-01
            """);

        Assert.Empty(run.Errors);
        JsonElement update = Assert.Single(Assert.Single(server.ParamsOf("host.update")).EnumerateArray());
        Assert.Equal(
            ["env=prod", "a=1", "b=2"],
            update.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("tag").GetString() + "=" + t.GetProperty("value").GetString()));
    }

    [Fact]
    public async Task Add_ZbxTag_does_not_repeat_a_tag_the_host_already_carries()
    {
        // Copying a tag set onto a host that already has one of the pairs: host.update refuses a duplicate
        // (tag, value) pair (CHost's uniqueness rule on tags), so the merge keeps the existing one.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.get", parameters => parameters.TryGetProperty("selectTags", out _)
            ? """[{"hostid":"10084","host":"web-01","tags":[{"tag":"env","value":"prod"}]}]"""
            : FakeZabbix.Hosts(("10084", "web-01")));
        server.On("host.update", """{"hostids":["10084"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Add-ZbxTag env prod -InputObject web-01 -Force -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement update = Assert.Single(Assert.Single(server.ParamsOf("host.update")).EnumerateArray());
        Assert.Single(update.GetProperty("tags").EnumerateArray());
    }
}
