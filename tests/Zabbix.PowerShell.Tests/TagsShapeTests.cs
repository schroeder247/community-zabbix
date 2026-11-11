// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// <c>-Tag</c> on every generated <c>New-</c>/<c>Set-</c> that carries one accepts the
/// flat-hashtable idiom (<c>@{ env = 'x'; role = 'y' }</c> → two tags), decided by shape
/// (<see cref="ZabbixNameValueListAttribute{TRecord}"/>: no <c>Tag</c> key and every value scalar), beside the
/// array of typed tag objects/hashtables carrying their own <c>Tag</c>/<c>Value</c> keys.
/// </summary>
public sealed class TagsShapeTests
{
    [Fact]
    public async Task New_ZbxHost_Tags_expands_one_flat_hashtable_into_one_tag_per_key()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -Tag @(@{ env = 'staging'; role = 'worker' }) -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        JsonElement[] tags = [.. request.GetProperty("tags").EnumerateArray()];
        Assert.Equal(2, tags.Length);
        Assert.Contains(tags, t => t.GetProperty("tag").GetString() == "env" && t.GetProperty("value").GetString() == "staging");
        Assert.Contains(tags, t => t.GetProperty("tag").GetString() == "role" && t.GetProperty("value").GetString() == "worker");
    }

    [Fact]
    public async Task New_ZbxHost_Tags_expands_one_bare_flat_hashtable_with_no_array_wrapper_into_one_tag_per_key()
    {
        // The dominant idiom of existing scripts passes the flat hashtable BARE, with no @() wrapper
        // (-Tag @{ env = 'x' }, not -Tag @(@{ env = 'x' })). A Hashtable is itself an IEnumerable (of DictionaryEntry),
        // so enumerated element-wise before the Hashtable shape check it would throw "'[env, staging]' is not a tag"
        // (DictionaryEntry.ToString()'s own format).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -Tag @{ env = 'staging'; role = 'worker' } -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        JsonElement[] tags = [.. request.GetProperty("tags").EnumerateArray()];
        Assert.Equal(2, tags.Length);
        Assert.Contains(tags, t => t.GetProperty("tag").GetString() == "env" && t.GetProperty("value").GetString() == "staging");
        Assert.Contains(tags, t => t.GetProperty("tag").GetString() == "role" && t.GetProperty("value").GetString() == "worker");
    }

    [Fact]
    public async Task New_ZbxHost_Tags_bare_hashtable_with_Tag_key_is_the_single_typed_tag_shape()
    {
        // The bare-hashtable branch must still tell the two shapes apart by their OWN "Tag" key, exactly like the
        // array-wrapped path already does (TagsShapeTests above) — a bare @{ Tag = ..; Value = .. } is one typed tag,
        // not two tags named "Tag"/"Value".
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -Tag @{ Tag = 'env'; Value = 'staging' } -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        JsonElement tag = Assert.Single(request.GetProperty("tags").EnumerateArray());
        Assert.Equal("env", tag.GetProperty("tag").GetString());
        Assert.Equal("staging", tag.GetProperty("value").GetString());
    }

    [Fact]
    public async Task New_ZbxHost_Tags_still_accepts_an_array_of_typed_tag_hashtables()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        // The already-working shape (New-ZbxTag's own output, and a bare @{Tag=..;Value=..} hashtable): unaffected.
        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -Tag @(@{ Tag = 'env'; Value = 'staging' }) -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        JsonElement tag = Assert.Single(request.GetProperty("tags").EnumerateArray());
        Assert.Equal("env", tag.GetProperty("tag").GetString());
        Assert.Equal("staging", tag.GetProperty("value").GetString());
    }

    [Fact]
    public async Task New_ZbxHost_Tags_accepts_typed_ZabbixHostTagRequest_instances()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("host.create", """{"hostids":["10084"]}""");
        server.On("host.get", FakeZabbix.Hosts(("10084", "web-01")));
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("""
            $t = New-ZbxTag -Tag env -Value staging
            New-ZbxHost -Host web-01 -Group @('7') -Tag @($t) -Confirm:$false
            """);

        Assert.Empty(run.Errors);
        JsonElement request = Assert.Single(server.ParamsOf("host.create"));
        JsonElement tag = Assert.Single(request.GetProperty("tags").EnumerateArray());
        Assert.Equal("env", tag.GetProperty("tag").GetString());
        Assert.Equal("staging", tag.GetProperty("value").GetString());
    }

    [Fact]
    public async Task New_ZbxHost_Tags_flat_hashtable_with_a_non_scalar_value_is_refused()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxHost -Host web-01 -Group @('7') -Tag @(@{ env = @('a','b') }) -Confirm:$false");

        Assert.Single(run.Errors);
        Assert.Empty(server.ParamsOf("host.create"));
    }

    [Theory]
    [InlineData("-Parameter @{ url = 'https://x'; token = 't' }")]
    [InlineData("-Parameter @(@{ name = 'url'; value = 'https://x' }, @{ name = 'token'; value = 't' })")]
    public async Task New_ZbxScript_WebhookParameters_takes_a_name_to_value_hashtable_or_name_value_records(string parameters)
    {
        // Every {name, value} list (script webhook parameters, item/httptest headers, variables, query
        // fields) takes the same flat hashtable -Tag takes, one pair per key; the record shape still binds.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("script.create", """{"scriptids":["4"]}""");
        server.On("script.get", """[{"scriptid":"4","name":"next-s"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("New-ZbxScript -Name next-s -Type Webhook -Scope Action -Command 'return 1' " + parameters + " -Confirm:$false");

        Assert.Empty(run.Errors);
        JsonElement[] pairs = [.. Assert.Single(server.ParamsOf("script.create")).GetProperty("parameters").EnumerateArray()];
        Assert.Equal(2, pairs.Length);
        Assert.Contains(pairs, p => p.GetProperty("name").GetString() == "url" && p.GetProperty("value").GetString() == "https://x");
        Assert.Contains(pairs, p => p.GetProperty("name").GetString() == "token" && p.GetProperty("value").GetString() == "t");
    }
}
