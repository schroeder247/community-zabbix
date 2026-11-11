// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0

namespace Community.Zabbix.PowerShell.Tests;

/// <summary>
/// Hand-rolled parsing where the SDK or the BCL already parses. A <c>-Server</c> entry is a
/// <see cref="System.Net.DnsEndPoint"/>, which the module reads from text through <see cref="ZabbixEndPoint"/>
/// (IPv6 judged by <c>IPAddress.TryParse</c>), never by
/// a split at the last colon, which turns <c>2001:db8::7</c> into host <c>2001:db8:</c> port 7.
/// </summary>
public sealed class AdversarialParsingTests
{
    public static TheoryData<string> ServerTexts =>
    [
        "zabbix.example", "zabbix.example:10052", "192.0.2.7", "192.0.2.7:10052",
        "[2001:db8::7]:10052", "2001:db8::7", "::1",
    ];

    [Theory]
    [MemberData(nameof(ServerTexts))]
    public void A_server_entry_binds_as_the_sdk_s_server_node_parser_reads_it(string text)
    {
        System.Net.DnsEndPoint expected = ZabbixEndPoint.Parse(text);

        Assert.Equal(expected, new ZabbixEndPointAttribute().Transform(null!, text));
    }
}
