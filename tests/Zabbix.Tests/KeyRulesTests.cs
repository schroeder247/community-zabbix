// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0

namespace Community.Zabbix.Tests;

/// <summary>
/// The item key access rules (docs/adr/0009-bundled-items.md §3): Zabbix's <c>AllowKey</c> and <c>DenyKey</c>, matched
/// as <c>zbx_check_request_access_rules</c> matches them, and held against Zabbix's own golden cases
/// (<c>tests/libs/zbxsysinfo/zbx_check_key_access_rules.yaml</c>): a final <c>*</c> parameter matches the parameters that
/// remain, none included.
/// </summary>
public sealed class KeyRulesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string, string, bool> PatternTable => new()
    {
        { "*", "vfs.file.contents[/etc/passwd]", true },
        { "*", "agent.ping", true },
        { "vfs.file.*", "vfs.file.size", true },
        { "vfs.file.*", "vfs.file.contents[]", false },
        { "vfs.*.contents", "vfs.mount.point.file.contents", true },
        { "vfs.*.contents", "vfs.contents", false },
        { "vfs.*.contents", "vfs.file.contents[]", false },
        { "vfs.file.*[*]", "vfs.file.size.bytes[]", true },
        { "vfs.file.*[*]", "vfs.file.size[/var/log/zabbix_agent.log,bytes]", true },
        { "vfs.file.*[*]", "vfs.file.get.custom", false },
        { "vfs.file.contents", "vfs.file.contents", true },
        { "vfs.file.contents", "vfs.file.contents[/etc/passwd]", false },
        { "vfs.file.contents[]", "vfs.file.contents[]", true },
        { "vfs.file.contents[]", "vfs.file.contents", false },
        { "vfs.file.contents[*]", "vfs.file.contents[]", true },
        { "vfs.file.contents[*]", "vfs.file.contents[/path/to/file]", true },
        { "vfs.file.contents[*]", "vfs.file.contents", false },
        { "vfs.file.contents[/etc/passwd,*]", "vfs.file.contents[/etc/passwd,]", true },
        { "vfs.file.contents[/etc/passwd,*]", "vfs.file.contents[/etc/passwd,utf8]", true },
        { "vfs.file.contents[/etc/passwd,*]", "vfs.file.contents[/etc/passwd]", true },
        { "vfs.file.contents[/etc/passwd,*]", "vfs.file.contents[/var/log/zabbix_server.log]", false },
        { "test[a,*]", "test[a]", true },
        { "test[a,*]", "test[a,anything]", true },
        { "test[a,*]", "test[]", false },
        { "vfs.file.contents[/etc/passwd,*]", "vfs.file.contents[]", false },
        { "vfs.file.contents[*passwd*]", "vfs.file.contents[/etc/passwd]", true },
        { "vfs.file.contents[*passwd*]", "vfs.file.contents[/etc/passwd,]", false },
        { "vfs.file.contents[*passwd*]", "vfs.file.contents[/etc/passwd,utf8]", false },
        { "vfs.file.contents[*passwd*,*]", "vfs.file.contents[/etc/passwd,]", true },
        { "vfs.file.contents[*passwd*,*]", "vfs.file.contents[/etc/passwd]", true },
        { "vfs.file.contents[*passwd*,*]", "vfs.file.contents[/tmp/test]", false },
        { "vfs.file.contents[/etc/passwd,utf8]", "vfs.file.contents[/etc/passwd,utf8]", true },
        { "vfs.file.contents[/etc/passwd,utf8]", "vfs.file.contents[/etc/passwd,]", false },
        { "vfs.file.contents[/etc/passwd,utf8]", "vfs.file.contents[/etc/passwd,utf16]", false },
    };

    [Theory]
    [MemberData(nameof(PatternTable))]
    public void A_deny_rule_matches_the_keys_Zabbix_s_pattern_table_says_it_matches(string pattern, string key, bool matches)
    {
        ZabbixKeyRules rules = ZabbixKeyRules.Parse(["DenyKey=" + pattern]);

        Assert.Equal(!matches, rules.Allows(Key(key)));
    }

    [Fact]
    public void The_first_rule_that_matches_decides()
    {
        ZabbixKeyRules rules = ZabbixKeyRules.Parse(["DenyKey=vfs.file.contents[/etc/shadow]", "AllowKey=vfs.file.*[*]", "AllowKey=agent.*", "DenyKey=*"]);

        Assert.True(rules.Allows(Key("vfs.file.contents[/etc/passwd]")));
        Assert.False(rules.Allows(Key("vfs.file.contents[/etc/shadow]")));
        Assert.True(rules.Allows(Key("agent.ping")));
        Assert.False(rules.Allows(Key("proc.num")));
    }

    [Fact]
    public void System_run_is_denied_unless_a_rule_allows_it()
    {
        Assert.False(ZabbixKeyRules.Default.Allows(Key("system.run[ls]")));
        Assert.True(ZabbixKeyRules.Default.Allows(Key("system.uname")));
        Assert.Equal(["DenyKey=system.run[*]"], ZabbixKeyRules.Default.Rules);

        ZabbixKeyRules allowed = ZabbixKeyRules.Parse(["AllowKey=system.run[uptime]"]);

        Assert.True(allowed.Allows(Key("system.run[uptime]")));
        Assert.False(allowed.Allows(Key("system.run[rm -rf /]")));
    }

    [Fact]
    public void Rules_that_change_nothing_are_dropped_as_Zabbix_drops_them()
    {
        // A trailing AllowKey is the default; a rule after DenyKey=* can never match; a repeated pattern is ignored.
        ZabbixKeyRules rules = ZabbixKeyRules.Parse(["DenyKey=vfs.file.contents[*]", "DenyKey=vfs.file.contents[*]", "AllowKey=agent.*"]);

        Assert.Equal(["DenyKey=vfs.file.contents[*]", "DenyKey=system.run[*]"], rules.Rules);
        Assert.Equal(["DenyKey=*"], ZabbixKeyRules.Parse(["DenyKey=*", "AllowKey=agent.ping"]).Rules);
    }

    [Theory]
    [InlineData("AllowKey=agent.*")]
    [InlineData("AllowKey=*")]
    public void Rules_that_allow_every_key_are_refused_as_Zabbix_s_agent_refuses_to_start(string rule)
    {
        FormatException refused = Assert.Throws<FormatException>(() => ZabbixKeyRules.Parse([rule]));

        Assert.Contains("match all keys", refused.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Deny=agent.ping")]
    [InlineData("DenyKey=")]
    [InlineData("DenyKey=vfs.file.contents[*")]
    [InlineData("DenyKey=vfs.file.contents*utf8]")]
    public void A_line_that_is_not_one_rule_is_refused(string rule)
    {
        Assert.Throws<FormatException>(() => ZabbixKeyRules.Parse([rule]));
    }

    [Fact]
    public async Task A_denied_key_is_answered_as_one_nobody_answers()
    {
        ZabbixItemDictionary handlers = ZabbixItemDictionary.BuiltIn("edge-01");
        handlers.KeyRules = ZabbixKeyRules.Parse(["DenyKey=agent.version"]);

        ZabbixAgentAnswer denied = await handlers.AnswerAsync(Key("agent.version"), TimeSpan.FromSeconds(3), Ct);
        ZabbixAgentAnswer allowed = await handlers.AnswerAsync(Key("agent.ping"), TimeSpan.FromSeconds(3), Ct);

        Assert.Equal("This agent does not answer the key.", denied.Error);
        Assert.Equal("1", allowed.Value);
    }

    [Fact]
    public void An_agent_s_options_refuse_rules_that_do_not_parse()
    {
        var options = new ZabbixActiveAgentOptions();
        options.KeyRules.Add("Deny=agent.ping");

        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        bool valid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(options, new(options), results, validateAllProperties: true);

        Assert.False(valid);
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(ZabbixActiveAgentOptions.KeyRules)));
    }

    private static ZabbixItemKey Key(string text) => ZabbixItemKey.Parse(text, null);
}
