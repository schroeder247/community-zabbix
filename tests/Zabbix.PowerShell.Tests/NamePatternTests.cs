// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Reflection;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// A <c>Get-</c>'s <c>-Name</c> is a PowerShell wildcard pattern (<c>*</c>, <c>?</c>, backtick
/// escapes, case-insensitive; brackets literal), declared with <c>[SupportsWildcards]</c>, sent as one case-insensitive
/// <c>search</c>, and narrowed on this side where Zabbix's <c>search</c> cannot say it.
/// </summary>
public sealed class NamePatternTests
{
    [Theory]
    [InlineData("web01", "web01", true)]
    [InlineData("web*", "web*", true)]
    [InlineData("web**01", "web*01", true)]
    [InlineData("web0?", "web0*", false)]
    [InlineData("a`*b", "a*b", false)]
    [InlineData("a`?b", "a?b", true)]
    [InlineData("Proxy [a]*", "Proxy [a]*", true)]
    [InlineData("50%_off", "50%_off", true)]
    public void A_written_name_becomes_the_server_pattern_Zabbix_can_search(string written, string server, bool precise)
    {
        ZabbixNamePattern pattern = ZabbixNamePattern.Parse(written);

        Assert.Equal((server, precise), (pattern.Server, pattern.Precise));
    }

    [Theory]
    [InlineData("web0?", "WEB01", true)]
    [InlineData("web0?", "web001", false)]
    [InlineData("Proxy [a]*", "Proxy [a] down", true)]
    [InlineData("Proxy [a]*", "Proxy a down", false)]
    [InlineData("a`*b", "a*b", true)]
    [InlineData("a`*b", "axb", false)]
    public void IsMatch_is_PowerShells_own_match_with_literal_brackets(string written, string name, bool matches)
    {
        Assert.Equal(matches, ZabbixNamePattern.Parse(written).IsMatch(name));
    }

    [Fact]
    public void Every_Get_Name_that_takes_a_pattern_declares_SupportsWildcards()
    {
        string[] offenders = [.. ModuleUnderTest.All.SelectMany(module => module.Assembly.GetExportedTypes())
            .Where(t => !t.IsAbstract && t.GetCustomAttribute<CmdletAttribute>() is { VerbName: "Get" })
            .Select(t => (Type: t, Name: t.GetProperty("Name")))
            .Where(c => c.Name?.GetCustomAttributes<ParameterAttribute>().Any(p => p.ParameterSetName == ZabbixParameterSets.ByName) == true
                && c.Name.GetCustomAttribute<SupportsWildcardsAttribute>() is null)
            .Select(c => c.Type.Name)];

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task An_exact_name_matches_case_insensitively_in_one_search()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"2","name":"Linux servers"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHostGroup 'linux servers' -Limit 3");

        Assert.Empty(run.Errors);
        Assert.Single(run.Output);
        JsonElement sent = Assert.Single(server.ParamsOf("hostgroup.get"));
        Assert.Equal("""["linux servers"]""", sent.GetProperty("search").GetProperty("name").GetRawText());
        Assert.True(sent.GetProperty("searchWildcardsEnabled").GetBoolean());
        Assert.Equal(3, sent.GetProperty("limit").GetInt32());
        Assert.False(sent.TryGetProperty("filter", out _));
    }

    [Fact]
    public async Task A_question_mark_widens_the_search_and_is_matched_here_with_Limit_counted_here()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"1","name":"web001"},{"groupid":"2","name":"web01"},{"groupid":"3","name":"web02"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHostGroup 'web0?' -Limit 1");

        Assert.Empty(run.Errors);
        Assert.Equal(["web01"], run.Output.Select(o => ((ZabbixHostGroup)o.BaseObject).Name));
        JsonElement sent = Assert.Single(server.ParamsOf("hostgroup.get"));
        Assert.Equal("""["web0*"]""", sent.GetProperty("search").GetProperty("name").GetRawText());
        Assert.False(sent.TryGetProperty("limit", out _));
    }

    [Fact]
    public async Task A_name_beside_the_callers_own_Search_is_matched_here_so_both_must_hold()
    {
        // zbx_db_search joins every field and every pattern with one glue: -Name OR-ed across names would OR the
        // caller's -Search too. So the server gets the caller's search alone and the names are matched here.
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("hostgroup.get", """[{"groupid":"1","name":"db-linux"},{"groupid":"2","name":"web-linux"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHostGroup 'web*' -Search @{ name = 'linux' }");

        Assert.Empty(run.Errors);
        Assert.Equal(["web-linux"], run.Output.Select(o => ((ZabbixHostGroup)o.BaseObject).Name));
        JsonElement sent = Assert.Single(server.ParamsOf("hostgroup.get"));
        Assert.Equal("linux", sent.GetProperty("search").GetProperty("name").GetString());
        Assert.False(sent.TryGetProperty("searchWildcardsEnabled", out _));
    }

    [Fact]
    public async Task CountOutput_refuses_a_name_the_server_cannot_match()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHostGroup 'web0?' -CountOutput");

        Assert.StartsWith("ZabbixCountOutputNameNotServerSide,", Assert.Single(run.Errors).FullyQualifiedErrorId, StringComparison.Ordinal);
        Assert.Empty(server.ParamsOf("hostgroup.get"));
    }

    [Fact]
    public async Task Get_ZbxHttpTest_Name_searches_the_longest_literal_and_matches_the_pattern_here()
    {
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("httptest.get", """[{"httptestid":"1","name":"Login check"},{"httptestid":"2","name":"Logout check page"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxHttpTest 'log* check'");

        Assert.Empty(run.Errors);
        Assert.Equal(["Login check"], run.Output.Select(o => ((ZabbixHttpTest)o.BaseObject).Name));
        JsonElement sent = Assert.Single(server.ParamsOf("httptest.get"));
        Assert.Equal(" check", sent.GetProperty("search").GetProperty("name").GetString());
        Assert.False(sent.TryGetProperty("filter", out _));
    }

    [Fact]
    public async Task Get_ZbxUserDirectory_Name_is_the_directory_name_not_its_LDAP_host()
    {
        // userdirectory.get refuses a search on "host" (on 7.0.31 and 8.0.0rc1).
        await using FakeZabbix server = await FakeZabbix.StartAsync();
        server.On("userdirectory.get", """[{"userdirectoryid":"1","name":"corp-ldap","host":"ldap.example"}]""");
        using Shell shell = Shell.Open();
        shell.Connect(server);

        Run run = shell.Invoke("Get-ZbxUserDirectory corp-ldap");

        Assert.Empty(run.Errors);
        JsonElement sent = Assert.Single(server.ParamsOf("userdirectory.get"));
        Assert.Equal("""["corp-ldap"]""", sent.GetProperty("search").GetProperty("name").GetRawText());
        Assert.False(sent.GetProperty("search").TryGetProperty("host", out _));
    }
}
