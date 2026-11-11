// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Protocol;

namespace Community.Zabbix.Tests;

/// <summary>The public wire link: the real framed-TCP transport, which a caller can decorate and hand to any façade.</summary>
public sealed class WireLinkTests
{
    [Fact]
    public async Task A_decorated_link_carries_a_facade_s_request_to_the_trapper()
    {
        var (port, request) = MockTrapper.Answer("""{"response":"success"}""");
        using var link = new ZabbixWireLink([new ZabbixServerNode("127.0.0.1", port)]);
        ZabbixExchangeCallback real = link.AsExchange(followRedirects: false);
        int exchanges = 0;
        ZabbixExchangeCallback counted = (frame, cancellationToken) =>
        {
            Interlocked.Increment(ref exchanges);
            return real(frame, cancellationToken);
        };
        using var wire = new ZabbixServerWire(counted, "sid");

        await wire.PingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, exchanges);
        Assert.Contains("status.get", await request, StringComparison.Ordinal);
        Assert.Equal(new System.Net.DnsEndPoint("127.0.0.1", port), link.First);
    }

    [Fact]
    public async Task An_exchange_taken_from_a_disposed_link_throws_object_disposed()
    {
        var link = new ZabbixWireLink([new ZabbixServerNode("127.0.0.1", 10051)]);
        ZabbixExchangeCallback exchange = link.AsExchange(followRedirects: false);
        link.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await exchange("{}"u8.ToArray(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_link_without_nodes_is_refused()
        => Assert.Throws<ArgumentException>(() => new ZabbixWireLink([]));
}
