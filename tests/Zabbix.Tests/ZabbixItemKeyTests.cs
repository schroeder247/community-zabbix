// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;

namespace Community.Zabbix.Tests;

/// <summary>
/// The item key: its verbatim text is its identity (what the server stores in <c>items.key_</c> and compares with
/// <c>strcmp</c>), and its parameters are a parsed view of that text. The grammar is upstream's: validity is
/// <c>zbx_parse_key</c> (<c>src/libs/zbxexpr/expr.c</c>) consuming the whole text, and the view is
/// <c>zbx_parse_item_key</c> (<c>src/libs/zbxsysinfo/sysinfo.c</c> over <c>src/libs/zbxparam/param.c</c>).
/// </summary>
public sealed class ZabbixItemKeyTests
{
    private const ZabbixItemKeyParameterType U = ZabbixItemKeyParameterType.Undefined;
    private const ZabbixItemKeyParameterType S = ZabbixItemKeyParameterType.String;
    private const ZabbixItemKeyParameterType A = ZabbixItemKeyParameterType.Array;

    /// <summary>
    /// Upstream's own golden cases, transcribed from <c>tests/libs/zbxsysinfo/parse_item_key.yaml</c> (Zabbix 7.0.31; the
    /// same cases in 7.4.15 and 8.0.0rc1): the key, the name, and each parameter with its type.
    /// </summary>
    public static TheoryData<string, string, string[], ZabbixItemKeyParameterType[]> UpstreamValidKeys => new()
    {
        { "key", "key", [], [] },
        { "key[]", "key", [""], [U] },
        { "key[\"\"]", "key", [""], [S] },
        { "key[ ]", "key", [""], [U] },
        { "key[ \"\"]", "key", [""], [S] },
        { "key[ \"\" ]", "key", [""], [S] },
        { "key[a]", "key", ["a"], [S] },
        { "key[ a]", "key", ["a"], [S] },
        { "key[ a ]", "key", ["a "], [S] },
        { "key[\"a\"]", "key", ["a"], [S] },
        { "key[\"a\",]", "key", ["a", ""], [S, U] },
        { "key[a,]", "key", ["a", ""], [S, U] },
        { "key[a,b,c]", "key", ["a", "b", "c"], [S, S, S] },
        { "key[\"a\",\"b\",\"c\"]", "key", ["a", "b", "c"], [S, S, S] },
        { "key[a,[b,c]]", "key", ["a", "b,c"], [S, A] },
        { "key[a,[b,]]", "key", ["a", "b,"], [S, A] },
        { "key[a,b[c]", "key", ["a", "b[c"], [S, S] },
        { "key[\"a\",\"b\",[\"c\",\"d\\\",]\"]]", "key", ["a", "b", "\"c\",\"d\\\",]\""], [S, S, A] },
        { "key[\"a\",\"b\",[\"c\",\"d\\\",]\"],[e,f]]", "key", ["a", "b", "\"c\",\"d\\\",]\"", "e,f"], [S, S, A, A] },
        { "key[a\"b\"]", "key", ["a\"b\""], [S] },
        { "key[\"a\",b\"c\",d]", "key", ["a", "b\"c\"", "d"], [S, S, S] },
        {
            "key[\"\\\"aaa\\\"\", \"bbb\",\"ccc\" , \"ddd\" ,\"\", \"\",\"\" , \"\" ,, ,  ,eee, fff,ggg , hhh\" ]", "key",
            ["\"aaa\"", "bbb", "ccc", "ddd", "", "", "", "", "", "", "", "eee", "fff", "ggg ", "hhh\" "],
            [S, S, S, S, S, S, S, S, U, U, U, S, S, S, S]
        },
        { "key[[],\"[]\",[a,b],\"z,[c,d]\"]", "key", ["", "[]", "a,b", "z,[c,d]"], [A, S, A, S] },
        { "net.tcp.service[tcp,,[80,443]]", "net.tcp.service", ["tcp", "", "80,443"], [S, U, A] },     // measured live: an array parameter
    };

    /// <summary>
    /// Upstream's refusals: every key <c>parse_item_key.yaml</c> expects to fail, which are the <c>parse_key.yaml</c> cases
    /// whose valid part stops short of the text or that fail outright (a key is valid only when the whole text is one).
    /// </summary>
    public static TheoryData<string> UpstreamInvalidKeys => new()
    {
        "key[[\"a\",]",
        "key[[a\"]\"]",
        "key[[\"a\",\"\\\"b\\\"]\"]",
        "key[\"a\",[\"b\",\"c\\\"]\"]]]",
        "key[a ]]",
        "key[ a]]",
        "key[ГУГЛ]654",
        "{}key",
        "ssh,21",
        "key[][]",
        "key[\"a\",b,[\"c\",\"d\\\",]\"]][\"d\"]",
        "key[[[]]]",
        "key[\"a\",[\"b\",[\"c\",\"d\"],e],\"f\"]",
        "key[\"a\",\"b\",[[\"c\",\"d\\\",]\"]]]",
        "key[a]]",
        "key[a[b]]",
        "key[\"a\",b[c,d],e]",
        "key[\"a\"b]",
        "key[\"a\",[\"b\",\"]\"c]]",
        "key[[\"]\"a]]",
        "key[[a]\"b\"]",
        "",
        "[a]",
        "k a",
        "k[a",
        "k[\"a]",
    };

    [Theory]
    [MemberData(nameof(UpstreamValidKeys))]
    public void Every_key_upstream_accepts_parses_into_upstream_s_name_parameters_and_types(string text, string name, string[] parameters, ZabbixItemKeyParameterType[] types)
    {
        ZabbixItemKey key = ZabbixItemKey.Parse(text, CultureInfo.InvariantCulture);

        Assert.Equal(name, key.Name);
        Assert.Equal(parameters, key.Parameters);
        Assert.Equal(types, Enumerable.Range(0, key.Parameters.Count).Select(key.GetParameterType));
        Assert.Equal(text, key.ToString());                                                // the text is the identity, verbatim
    }

    [Theory]
    [MemberData(nameof(UpstreamInvalidKeys))]
    public void Every_key_upstream_refuses_is_refused_and_TryParse_never_throws(string text)
    {
        Assert.False(ZabbixItemKey.TryParse(text, out _));
        Assert.Throws<FormatException>(() => ZabbixItemKey.Parse(text, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_quoted_parameter_keeps_its_quotes_on_the_wire_and_is_a_different_key_from_the_unquoted_one()
    {
        ZabbixItemKey quoted = ZabbixItemKey.Parse("k[\"a\"]", CultureInfo.InvariantCulture);
        ZabbixItemKey bare = ZabbixItemKey.Parse("k[a]", CultureInfo.InvariantCulture);

        Assert.Equal("k[\"a\"]", quoted.ToString());
        Assert.Equal(quoted.Parameters, bare.Parameters);                                  // the same view…
        Assert.NotEqual(quoted, bare);                                                     // …but the server stores and matches the text
    }

    [Fact]
    public void Spacing_is_part_of_the_identity()
        => Assert.NotEqual(ZabbixItemKey.Parse("k[a, b]", CultureInfo.InvariantCulture), ZabbixItemKey.Parse("k[a,b]", CultureInfo.InvariantCulture));

    [Fact]
    public void Keys_are_equal_by_text_and_index_their_parameters()
    {
        ZabbixItemKey parsed = ZabbixItemKey.Parse("net.if.in[eth0,bytes]", CultureInfo.InvariantCulture);
        ZabbixItemKey created = ZabbixItemKey.Create("net.if.in", "eth0", "bytes");

        Assert.Equal(parsed, created);
        Assert.True(parsed == created);
        Assert.Equal(parsed.GetHashCode(), created.GetHashCode());
        Assert.Equal("eth0", parsed[0]);
        Assert.Equal("net.if.in[eth0,bytes]", $"{parsed}");
    }

    [Theory]
    [InlineData("db with, comma", "k[\"db with, comma\"]")]
    [InlineData(" lead", "k[\" lead\"]")]
    [InlineData("[x", "k[\"[x\"]")]
    [InlineData("a]b", "k[\"a]b\"]")]
    [InlineData("\"q\"", "k[\"\\\"q\\\"\"]")]
    [InlineData("a\"b", "k[a\"b]")]
    [InlineData("", "k[]")]
    public void Create_quotes_a_parameter_exactly_when_upstream_would(string parameter, string text)
    {
        ZabbixItemKey key = ZabbixItemKey.Create("k", parameter);

        Assert.Equal(text, key.ToString());
        Assert.Equal([parameter], key.Parameters);
    }

    [Fact]
    public void Create_refuses_a_parameter_that_needs_quotes_and_ends_with_a_backslash()
        => Assert.Throws<FormatException>(() => ZabbixItemKey.Create("k", "a,b\\"));

    [Theory]
    [InlineData("probe/bad", new[] { "x" })]
    [InlineData("k", new[] { "a,b\\" })]
    [InlineData("", new string[0])]
    public void TryCreate_says_false_where_Create_would_throw(string name, string[] parameters)
        => Assert.False(ZabbixItemKey.TryCreate(name, parameters, out _));

    [Fact]
    public void Create_with_no_parameters_is_the_bare_name()
        => Assert.Equal("system.uptime", ZabbixItemKey.Create("system.uptime").ToString());

    [Theory]
    [InlineData("web.page.get[https://user:s3cret@example/,", "the parameter list is not closed (at character 43)")]
    [InlineData("db.query[\"dsn=s3cret]", "a quoted parameter is not closed (at character 22)")]
    [InlineData("vfs.file[s3cret]tail", "text follows the closing bracket (at character 17)")]
    public void A_parse_failure_names_the_rule_and_the_position_never_the_text(string text, string reason)
    {
        // A key's parameters can carry a credential, and an exception's message reaches every log and span unredacted.
        FormatException failure = Assert.Throws<FormatException>(() => ZabbixItemKey.Parse(text, CultureInfo.InvariantCulture));

        Assert.DoesNotContain("s3cret", failure.Message, StringComparison.Ordinal);
        Assert.Equal($"The text is not a Zabbix item key: {reason}.", failure.Message);
    }

    [Fact]
    public void A_create_failure_names_the_parameter_by_position_never_its_value()
    {
        FormatException failure = Assert.Throws<FormatException>(() => ZabbixItemKey.Create("k", "ok", "s3cret,\\"));

        Assert.DoesNotContain("s3cret", failure.Message, StringComparison.Ordinal);
        Assert.Contains("parameter 2", failure.Message, StringComparison.Ordinal);
    }
}
