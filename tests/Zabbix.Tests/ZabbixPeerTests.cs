// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Community.Zabbix.Tests;

/// <summary>
/// <see cref="ZabbixPeer"/>, one entry of Zabbix's allowed hosts: an <see cref="IPAddress"/> and an
/// <see cref="IPNetwork"/> convert to one, <c>Parse</c> takes an address, a CIDR block, a DNS name or a macro as the API
/// checks them (<c>CIPRangeParser.php</c> 7.0), and a list goes to the server as Zabbix's comma-separated text.
/// </summary>
public sealed class ZabbixPeerTests
{
    [Fact]
    public void An_address_or_a_block_converts_to_the_entry_that_allows_it()
    {
        ZabbixPeer v4 = IPAddress.Parse("10.0.0.1");
        ZabbixPeer v6 = IPAddress.IPv6Loopback;
        ZabbixPeer scoped = IPAddress.Parse("fe80::1%4");
        ZabbixPeer block = IPNetwork.Parse("10.0.0.0/8");

        Assert.Equal("10.0.0.1", v4.ToString());
        Assert.Equal(IPNetwork.Parse("10.0.0.1/32"), v4.Network);
        Assert.Equal("::1", v6.ToString());
        Assert.Equal(IPNetwork.Parse("::1/128"), v6.Network);
        Assert.Equal("fe80::1", scoped.ToString());
        Assert.Equal("10.0.0.0/8", block.ToString());
        Assert.Equal("2001:db8::/32", ZabbixPeer.FromIPNetwork(IPNetwork.Parse("2001:db8::/32")).ToString());
        Assert.Null(block.HostName);
        Assert.Equal("address", Assert.Throws<ArgumentNullException>(() => ZabbixPeer.FromIPAddress(null!)).ParamName);
    }

    [Theory]
    [InlineData("10.0.0.1", "10.0.0.1/32", null)]
    [InlineData(" 10.0.0.1 ", "10.0.0.1/32", null)]
    [InlineData("192.168.1.7/24", "192.168.1.0/24", null)]
    [InlineData("::ffff:10.0.0.1", "::ffff:10.0.0.1/128", null)]
    [InlineData("2001:db8::/32", "2001:db8::/32", null)]
    [InlineData("monitor.example", null, "monitor.example")]
    [InlineData("zbx_proxy", null, "zbx_proxy")]
    [InlineData("{$TRAPPER_HOSTS}", null, null)]
    [InlineData("{HOST.HOST}", null, null)]
    [InlineData("{HOST.CONN}", null, null)]
    [InlineData("{IPADDRESS}", null, null)]
    [InlineData("{{HOST.HOST}.regsub(\"^(web)\", \\1)}", null, null)]
    public void Parse_reads_an_address_a_block_a_name_or_a_macro(string text, string? network, string? hostName)
    {
        ZabbixPeer peer = ZabbixPeer.Parse(text);

        Assert.Equal(text.Trim(), peer.ToString());
        Assert.Equal(network is null ? null : IPNetwork.Parse(network), peer.Network);
        Assert.Equal(hostName, peer.HostName);
        Assert.Equal(peer, ZabbixPeer.Parse(text.AsSpan()));
        Assert.Equal(peer, IParsableOf<ZabbixPeer>(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("10.0.0.0/33")]
    [InlineData("::1/129")]
    [InlineData("10.0.0.0/")]
    [InlineData("10.0.0.0/8/8")]
    [InlineData("zabbix.example/24")]
    [InlineData("fe80::1%4")]
    [InlineData("[::1]")]
    [InlineData("-host")]
    [InlineData("host name")]
    [InlineData("a,b")]
    [InlineData("{$MACRO")]
    [InlineData("{ITEM.VALUE}")]
    public void Parse_refuses_what_the_API_refuses(string text)
    {
        Assert.False(ZabbixPeer.TryParse(text, out ZabbixPeer result));
        Assert.Equal(default, result);
        Assert.StartsWith($"'{text}' isn't an allowed host Zabbix takes", Assert.Throws<FormatException>(() => ZabbixPeer.Parse(text)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_list_splits_at_the_commas_outside_its_macros()
    {
        Assert.Equal<ZabbixPeer>(
            [IPNetwork.Parse("10.0.0.0/8"), IPAddress.IPv6Loopback, ZabbixPeer.Parse("{HOST.HOST}")],
            ZabbixPeer.ParseList("10.0.0.0/8, ::1,{HOST.HOST}"));
        Assert.Equal(["{$A:\"x,y\"}", "10.0.0.1"], ZabbixPeer.ParseList("{$A:\"x,y\"},10.0.0.1").Select(peer => peer.ToString()));
        Assert.Equal(["{{HOST.IP}.regsub(\"a,b\", \\1)}", "::1"], ZabbixPeer.ParseList("{{HOST.IP}.regsub(\"a,b\", \\1)}, ::1").Select(peer => peer.ToString()));
        Assert.Equal(["monitor.example"], ZabbixPeer.ParseList("monitor.example,").Select(peer => peer.ToString()));
        Assert.Empty(ZabbixPeer.ParseList(string.Empty));
        Assert.Empty(ZabbixPeer.ParseList("  "));
        Assert.StartsWith("'' isn't an allowed host", Assert.Throws<FormatException>(() => ZabbixPeer.ParseList("10.0.0.1,,::1")).Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentNullException>(() => ZabbixPeer.ParseList(null!));
    }

    [Fact]
    public void Entries_are_equal_when_written_the_same()
    {
        Assert.Equal(ZabbixPeer.Parse("::1"), (ZabbixPeer)IPAddress.IPv6Loopback);
        Assert.NotEqual(ZabbixPeer.Parse("10.0.0.0/8"), ZabbixPeer.Parse("10.1.2.3/8"));
        Assert.Equal(ZabbixPeer.Parse("10.0.0.0/8").Network, ZabbixPeer.Parse("10.1.2.3/8").Network);
        Assert.Equal(string.Empty, default(ZabbixPeer).ToString());
        Assert.Null(default(ZabbixPeer).Network);
        Assert.Null(default(ZabbixPeer).HostName);
    }

    [Fact]
    public void A_list_goes_to_the_server_as_comma_separated_text()
    {
        var item = new ZabbixItemUpdateRequest(7) { TrapperHosts = [IPNetwork.Parse("10.0.0.0/8"), IPAddress.IPv6Loopback, ZabbixPeer.Parse("{HOST.CONN}")] };
        var anyone = new ZabbixItemUpdateRequest(7) { TrapperHosts = [] };

        Assert.Equal("10.0.0.0/8,::1,{HOST.CONN}", SentTrapperHosts(item));
        Assert.Equal(string.Empty, SentTrapperHosts(anyone));
        Assert.Throws<ArgumentException>(() => JsonSerializer.Serialize(new ZabbixItemUpdateRequest(7) { TrapperHosts = [default] }, ZabbixApiJson.Default.ZabbixItemUpdateRequest));
    }

    [Fact]
    public void A_reply_s_list_is_read_entry_by_entry_and_an_entry_outside_the_grammar_is_kept_as_written()
    {
        ZabbixItem item = JsonSerializer.Deserialize("""{"itemid":"7","trapper_hosts":"10.0.0.0/8, ::1,{HOST.HOST}"}""", ZabbixApiJson.Default.ZabbixItem)!;
        ZabbixItem open = JsonSerializer.Deserialize("""{"itemid":"8","trapper_hosts":""}""", ZabbixApiJson.Default.ZabbixItem)!;
        ZabbixItem newer = JsonSerializer.Deserialize("""{"itemid":"9","trapper_hosts":"10.0.0.0/8,some newer entry"}""", ZabbixApiJson.Default.ZabbixItem)!;

        Assert.Equal<ZabbixPeer>([IPNetwork.Parse("10.0.0.0/8"), IPAddress.IPv6Loopback, ZabbixPeer.Parse("{HOST.HOST}")], item.TrapperHosts!);
        Assert.Empty(open.TrapperHosts!);
        Assert.Equal(["10.0.0.0/8", "some newer entry"], newer.TrapperHosts!.Select(peer => peer.ToString()));
        Assert.Null(newer.TrapperHosts![1].Network);
        Assert.Null(newer.TrapperHosts![1].HostName);
        Assert.Contains("\"trapper_hosts\":\"10.0.0.0/8,some newer entry\"", JsonSerializer.Serialize(newer, ZabbixApiJson.Default.ZabbixItem), StringComparison.Ordinal);
    }

    private static string? SentTrapperHosts(ZabbixItemUpdateRequest request)
    {
        using JsonDocument sent = JsonDocument.Parse(JsonSerializer.Serialize(request, ZabbixApiJson.Default.ZabbixItemUpdateRequest));
        return sent.RootElement.GetProperty("trapper_hosts").GetString();
    }

    private static T IParsableOf<T>(string text) where T : IParsable<T> => T.Parse(text, CultureInfo.InvariantCulture);
}
