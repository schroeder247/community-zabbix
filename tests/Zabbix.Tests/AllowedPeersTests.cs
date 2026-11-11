// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using Community.Zabbix.Listening;

namespace Community.Zabbix.Tests;

/// <summary>Zabbix's allowed-peer list as upstream reads it (<c>comms.c</c>): addresses, CIDR blocks, names; the IPv4 forms are one peer; empty fails closed.</summary>
public sealed class AllowedPeersTests
{
    [Theory]
    [InlineData("127.0.0.1", "127.0.0.1", true)]
    [InlineData("127.0.0.1", "127.0.0.2", false)]
    [InlineData("127.0.0.1", "::ffff:127.0.0.1", true)]
    [InlineData("127.0.0.1", "::127.0.0.1", true)]
    [InlineData("10.0.0.0/8", "10.200.1.1", true)]
    [InlineData("10.1.2.3/8", "10.200.1.1", true)]
    [InlineData("10.0.0.0/8", "::ffff:10.9.9.9", true)]
    [InlineData("::ffff:127.0.0.0/104", "127.0.0.1", true)]
    [InlineData("::/0", "192.0.2.1", true)]
    [InlineData("::1", "::1", true)]
    [InlineData("::1", "127.0.0.1", false)]
    [InlineData("192.0.2.0/24, 2001:db8::/32", "2001:db8::5", true)]
    public async Task A_peer_matches_as_upstream_matches_it(string list, string peer, bool allowed)
    {
        ZabbixAllowedPeers peers = ZabbixAllowedPeers.Parse(list, null);

        Assert.Equal(allowed, await peers.ContainsAsync(IPAddress.Parse(peer), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("10.0.0.0/33")]
    [InlineData("::/129")]
    [InlineData("zabbix.example/24")]
    [InlineData("10.0.0.0/")]
    [InlineData("10.0.0.0/+8")]
    [InlineData("a b")]
    [InlineData("127.0.0.1,,10.0.0.1")]
    public void A_list_upstream_refuses_does_not_parse(string list)
    {
        Assert.False(ZabbixAllowedPeers.TryParse(list, null, out _));
        Assert.Throws<FormatException>(() => ZabbixAllowedPeers.Parse(list, null));
    }

    [Fact]
    public void An_empty_list_says_why_and_how_to_allow_everyone()
    {
        FormatException refused = Assert.Throws<FormatException>(() => ZabbixAllowedPeers.Parse("", null));

        Assert.Contains("0.0.0.0/0,::/0", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Entries_keep_their_kind_and_the_list_formats_as_zabbix_writes_it()
    {
        ZabbixAllowedPeers peers = ZabbixAllowedPeers.Parse("127.0.0.1, 10.0.0.0/8 ,zbx_proxy.example", null);

        Assert.Equal(["127.0.0.1", "10.0.0.0/8", "zbx_proxy.example"], peers.Entries);
        Assert.Equal(2, peers.Networks.Count);
        Assert.Equal(["zbx_proxy.example"], peers.Names);
        Assert.Equal("127.0.0.1,10.0.0.0/8,zbx_proxy.example", peers.ToString());
    }

    [Fact]
    public async Task A_name_is_resolved_on_the_check()
    {
        ZabbixAllowedPeers peers = ZabbixAllowedPeers.Parse("localhost", null);

        Assert.True(await peers.ContainsAsync(IPAddress.Loopback, TestContext.Current.CancellationToken));
    }
}
