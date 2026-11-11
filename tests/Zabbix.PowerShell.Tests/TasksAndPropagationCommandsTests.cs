// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>Tasks and propagation: <c>Invoke-ZbxCheckNow</c>, <c>Invoke-ZbxDiagnostic</c>, <c>Sync-ZbxProxyConfig</c>, <c>Invoke-ZbxHostGroupPropagation</c>, <c>Invoke-ZbxTemplateGroupPropagation</c>.</summary>
public sealed class TasksAndPropagationCommandsTests
{
    [Fact]
    public async Task Invoke_ZbxCheckNow_resolves_the_item_then_creates_a_check_now_task_and_writes_its_id()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("item.get", """[{"itemid":"501","key_":"agent.ping","name":"agent.ping","hostid":"10084","hosts":[{"hostid":"10084","host":"web-01"}]}]""");
        server.On("task.create", """{"taskids":["1"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Invoke-ZbxCheckNow agent.ping");

        Assert.Empty(run.Errors);
        // The created task id is written, not discarded.
        Assert.Equal(1UL, Assert.Single(run.Output).BaseObject);
        JsonElement task = Assert.Single(Assert.Single(server.ParamsOf("task.create")).EnumerateArray());
        Assert.Equal(6, task.GetProperty("type").GetInt32());
        // CTask::validateCreate makes the request's fields exclusive per type — check-now is itemid only.
        Assert.Equal("""{"itemid":"501"}""", task.GetProperty("request").GetRawText());
    }

    [Fact]
    public async Task Invoke_ZbxDiagnostic_creates_a_diagnostic_information_task_and_writes_its_id()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("task.create", """{"taskids":["1"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Invoke-ZbxDiagnostic");

        Assert.Empty(run.Errors);
        // The created task id is written, not discarded.
        Assert.Equal(1UL, Assert.Single(run.Output).BaseObject);
        JsonElement task = Assert.Single(server.ParamsOf("task.create"));
        Assert.Equal(1, task.GetProperty("type").GetInt32());
        // No itemid/proxyids (the server answers 'unexpected parameter "proxyids"'); with no -Request,
        // every diaginfo section, each with its own default stats.
        Assert.Equal(
            """{"historycache":{"stats":"extend"},"valuecache":{"stats":"extend"},"preprocessing":{"stats":"extend"},"alerting":{"stats":"extend"},"lld":{"stats":"extend"}}""",
            task.GetProperty("request").GetRawText());
    }

    [Fact]
    public async Task Sync_ZbxProxyConfig_resolves_the_proxy_then_creates_a_reload_task_and_writes_its_id()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("proxy.get", """[{"proxyid":"5","name":"proxy-1"}]""");
        server.On("task.create", """{"taskids":["1"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Sync-ZbxProxyConfig proxy-1");

        Assert.Empty(run.Errors);
        // The created task id is written, not discarded.
        Assert.Equal(1UL, Assert.Single(run.Output).BaseObject);
        JsonElement task = Assert.Single(server.ParamsOf("task.create"));
        Assert.Equal(2, task.GetProperty("type").GetInt32());
        Assert.Equal("""{"proxyids":["5"]}""", task.GetProperty("request").GetRawText());
    }

    [Fact]
    public async Task Invoke_ZbxHostGroupPropagation_resolves_the_group_then_propagates()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"7","name":"Web servers"}]""");
        server.On("hostgroup.propagate", """{"groupids":["7"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Invoke-ZbxHostGroupPropagation 'Web servers' -Permissions -TagFilters -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("hostgroup.propagate"));
        Assert.True(request.GetProperty("permissions").GetBoolean());
        Assert.True(request.GetProperty("tag_filters").GetBoolean());
        Assert.Equal("7", Assert.Single(request.GetProperty("groups").EnumerateArray()).GetProperty("groupid").GetString());
    }

    [Fact]
    public async Task Invoke_ZbxTemplateGroupPropagation_resolves_the_group_then_propagates()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("templategroup.get", """[{"groupid":"9","name":"Templates"}]""");
        server.On("templategroup.propagate", """{"groupids":["9"]}""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Invoke-ZbxTemplateGroupPropagation Templates -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("templategroup.propagate"));
        Assert.False(request.GetProperty("permissions").GetBoolean());
        Assert.Equal("9", Assert.Single(request.GetProperty("groups").EnumerateArray()).GetProperty("groupid").GetString());
    }
}
