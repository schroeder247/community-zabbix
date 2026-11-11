// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Community.Zabbix.Tests;

/// <summary>
/// <see cref="ZabbixPort"/>, an interface's port as the API takes it (<c>CHostInterface.php</c> and
/// <c>CHostPrototype.php</c> 7.0): a number from 0 to 65535 converts to one, <c>Parse</c> takes a number or a user or LLD
/// macro, and the wire carries the text, reading a reply's number or text.
/// </summary>
public sealed class ZabbixPortTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(161)]
    [InlineData(10050)]
    [InlineData(65535)]
    public void A_number_converts_to_the_port_and_back(int number)
    {
        ZabbixPort port = number;

        Assert.Equal(number, port.Number);
        Assert.Equal(number.ToString(CultureInfo.InvariantCulture), port.ToString());
        Assert.Equal(port, ZabbixPort.FromInt32(number));
        Assert.Equal(port, ZabbixPort.Parse(port.ToString()));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(65536)]
    [InlineData(int.MaxValue)]
    public void A_number_outside_the_port_range_is_refused(int number)
        => Assert.Equal("number", Assert.Throws<ArgumentOutOfRangeException>(() => ZabbixPort.FromInt32(number)).ParamName);

    [Theory]
    [InlineData("{$AGENT_PORT}")]
    [InlineData("{$SNMP_PORT:\"core\"}")]
    [InlineData("{#PORT}")]
    public void A_macro_is_kept_as_written_and_has_no_number(string text)
    {
        ZabbixPort port = ZabbixPort.Parse(text);

        Assert.Null(port.Number);
        Assert.Equal(text, port.ToString());
        Assert.True(ZabbixPort.TryParse(text.AsSpan(), out ZabbixPort spanned));
        Assert.Equal(port, spanned);
        Assert.Equal(port, ParsableOf<ZabbixPort>(text));
    }

    [Theory]
    [InlineData("")]
    [InlineData("65536")]
    [InlineData("99999999999")]
    [InlineData("-1")]
    [InlineData("+1")]
    [InlineData(" 161")]
    [InlineData("161 ")]
    [InlineData("1e3")]
    [InlineData("port")]
    [InlineData("{$PORT")]
    [InlineData("{HOST.PORT}")]
    public void Parse_refuses_what_the_API_refuses(string text)
    {
        Assert.False(ZabbixPort.TryParse(text, out ZabbixPort result));
        Assert.Equal(default, result);
        Assert.StartsWith($"'{text}' isn't a port Zabbix takes", Assert.Throws<FormatException>(() => ZabbixPort.Parse(text)).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Null_is_refused_and_TryParse_says_no()
    {
        Assert.Throws<ArgumentNullException>(() => ZabbixPort.Parse((string)null!));
        Assert.False(ZabbixPort.TryParse((string?)null, out _));
    }

    [Fact]
    public void The_interface_factories_send_Zabbix_s_port_for_the_kind_or_the_one_given()
    {
        Assert.Equal(10050, ZabbixInterface.Agent(IPAddress.Loopback).Port!.Value.Number);
        Assert.Equal(161, ZabbixInterface.Snmp(IPAddress.Loopback).Port!.Value.Number);
        Assert.Equal(623, ZabbixInterface.Ipmi("bmc-01.example").Port!.Value.Number);
        Assert.Equal(12345, ZabbixInterface.Jmx("app-01.example").Port!.Value.Number);
        Assert.Equal(10051, ZabbixInterface.Agent(IPAddress.Loopback, 10051).Port!.Value.Number);
        Assert.Equal("port", Assert.Throws<ArgumentOutOfRangeException>(() => ZabbixInterface.Agent(IPAddress.Loopback, 65536)).ParamName);
    }

    [Fact]
    public void A_request_sends_the_text_and_a_reply_s_number_or_text_is_read()
    {
        ZabbixHostInterfaceRequest snmp = ZabbixInterface.Snmp(IPAddress.Loopback) with { Port = 1161 };
        ZabbixHostInterfaceRequest agent = ZabbixInterface.Agent(IPAddress.Loopback) with { Port = ZabbixPort.Parse("{$AGENT_PORT}") };

        Assert.Contains("\"port\":\"1161\"", JsonSerializer.Serialize(snmp, ZabbixApiJson.Default.ZabbixHostInterfaceRequest), StringComparison.Ordinal);
        Assert.Contains("\"port\":\"{$AGENT_PORT}\"", JsonSerializer.Serialize(agent, ZabbixApiJson.Default.ZabbixHostInterfaceRequest), StringComparison.Ordinal);
        Assert.Equal(10050, Read("""{"interfaceid":"1","port":"10050"}""").Port!.Value.Number);
        Assert.Equal(10050, Read("""{"interfaceid":"1","port":10050}""").Port!.Value.Number);
        Assert.Equal(ZabbixPort.Parse("{$AGENT_PORT}"), Read("""{"interfaceid":"1","port":"{$AGENT_PORT}"}""").Port);
        ZabbixPort newer = Read("""{"interfaceid":"1","port":"10050-10060"}""").Port!.Value;
        Assert.Null(newer.Number);
        Assert.Equal("10050-10060", newer.ToString());
    }

    private static ZabbixHostInterface Read(string json) => JsonSerializer.Deserialize(json, ZabbixApiJson.Default.ZabbixHostInterface)!;

    private static T ParsableOf<T>(string text) where T : IParsable<T> => T.Parse(text, CultureInfo.InvariantCulture);
}
