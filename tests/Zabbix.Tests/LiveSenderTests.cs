// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Security.Authentication;
using System.Security.Cryptography;

namespace Community.Zabbix.Tests;

/// <summary>
/// The sender against every live server, read back through <c>history.get</c>: the values, their exact clocks to the
/// nanosecond, unicode, partial batches, and the TLS paths (a pre-shared key set on the host through the API; the lab's
/// certificates where the server was started with them).
/// </summary>
public sealed class LiveSenderTests
{
    private static readonly (string, ZabbixItemValueType)[] TextAndFloat = [("zbxtest.text", ZabbixItemValueType.Text), ("zbxtest.float", ZabbixItemValueType.Float)];

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task Sent_values_are_read_back_with_their_exact_clock_and_nanoseconds(string version)
    {
        (LiveTarget target, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "sender");
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "sender", TextAndFloat);
        // Two minutes back and a second apart, at nanoseconds the wire must carry verbatim (no rounding to µs or ms).
        long second = DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds();
        ZabbixValue[] values =
        [
            new(host.Host, "zbxtest.text", "grüße ✓ 漢字 — \"quoted\"\n2nd line", new ZabbixClock(second, 1)),
            new(host.Host, "zbxtest.text", "second", new ZabbixClock(second, 999_999_999)),
            new(host.Host, "zbxtest.float", "3.14159", new ZabbixClock(second + 1, 123_456_789)),
            new(host.Host, "zbxtest.float", "-0.5", new ZabbixClock(second + 1, 123_456_790)),
        ];

        using ZabbixSender sender2 = new ZabbixSender([trapper]);
        ZabbixSenderResponse response = await LiveFixtures.SendWhenKnownAsync(sender2, values);
        IReadOnlyList<ZabbixHistoryRecord> text = await LiveFixtures.HistoryAsync(scope.Server, host["zbxtest.text"], 2);
        IReadOnlyList<ZabbixHistoryRecord> floats = await LiveFixtures.HistoryAsync(scope.Server, host["zbxtest.float"], 2);

        Assert.Equal((4, 0, 4), (response.Processed, response.Failed, response.Total));
        Assert.Equal([values[0].Value, values[1].Value], text.Select(r => r.Value));
        Assert.Equal([(second, 1), (second, 999_999_999)], text.Select(r => (r.ClockExact!.Value.Seconds, r.ClockExact!.Value.Nanoseconds)));
        Assert.Equal([3.14159, -0.5], floats.Select(r => double.Parse(r.Value!, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal([(second + 1, 123_456_789), (second + 1, 123_456_790)], floats.Select(r => (r.ClockExact!.Value.Seconds, r.ClockExact!.Value.Nanoseconds)));

        // The pull plane reads the same values with the same clocks, as ZabbixClock.
        List<ZabbixValue> tree = [];
        await foreach (ZabbixValue v in scope.Server.Hosts[host.Host].Items["zbxtest.text"].GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(second), cancellationToken: TestContext.Current.CancellationToken))
            tree.Add(v);
        Assert.Equal([values[0].Clock, values[1].Clock], tree.Select(v => v.Clock));
        Assert.Equal([values[0].Value, values[1].Value], tree.Select(v => v.Value));
        Assert.All(tree, v => Assert.Equal(host.Host, v.HostName));
    }

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_batch_with_an_unknown_key_is_partial_and_counts_both(string version)
    {
        (LiveTarget target, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "partial");
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "partial", [("zbxtest.float", ZabbixItemValueType.Float)]);
        using var sender = new ZabbixSender([trapper]);
        await LiveFixtures.SendWhenKnownAsync(sender, [new ZabbixValue(host.Host, "zbxtest.float", "1", DateTimeOffset.UtcNow)]);

        ZabbixSenderResponse response = await sender.SendAsync([new ZabbixValue(host.Host, "zbxtest.float", "2", DateTimeOffset.UtcNow), new ZabbixValue(host.Host, "zbxtest.nope", "1", DateTimeOffset.UtcNow)], TestContext.Current.CancellationToken);

        Assert.True(response.HasFailures);
        Assert.Equal((1, 1, 2), (response.Processed, response.Failed, response.Total));
        Assert.Equal(["1", "2"], (await LiveFixtures.HistoryAsync(scope.Server, host["zbxtest.float"], 2)).Select(r => r.Value));
    }

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_host_that_accepts_only_a_pre_shared_key_takes_values_over_psk_and_refuses_plaintext(string version)
    {
        (LiveTarget target, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "psk");
        string identity = scope.Name("psk-identity");
        string psk = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "psk", [("zbxtest.float", ZabbixItemValueType.Float)],
            r => r with { TlsAccept = ZabbixTlsAcceptModes.Psk, TlsConnect = ZabbixTlsConnectMode.Psk, TlsPskIdentity = identity, TlsPsk = psk });
        using var overPsk = new ZabbixSender([trapper], new ZabbixConnectionOptions { Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = identity, Psk = psk } });
        using var plain = new ZabbixSender([trapper]);

        ZabbixSenderResponse sent = await LiveFixtures.SendWhenKnownAsync(overPsk, [new ZabbixValue(host.Host, "zbxtest.float", "7", DateTimeOffset.UtcNow)]);
        ZabbixSenderResponse refused = await plain.SendAsync([new ZabbixValue(host.Host, "zbxtest.float", "8", DateTimeOffset.UtcNow)], TestContext.Current.CancellationToken);

        Assert.Equal((1, 0), (sent.Processed, sent.Failed));
        Assert.Equal((0, 1), (refused.Processed, refused.Failed));                          // the server drops a value whose connection the host does not accept
        Assert.Equal(["7"], (await LiveFixtures.HistoryAsync(scope.Server, host["zbxtest.float"], 1)).Select(r => r.Value));
    }

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_wrong_pre_shared_key_fails_the_handshake_as_an_authentication_failure(string version)
    {
        (LiveTarget target, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        await using LiveScope scope = LiveScope.Open(version, "psk-wrong");
        string identity = scope.Name("psk-identity");
        string psk = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "psk-wrong", [("zbxtest.float", ZabbixItemValueType.Float)],
            r => r with { TlsAccept = ZabbixTlsAcceptModes.Psk, TlsConnect = ZabbixTlsConnectMode.Psk, TlsPskIdentity = identity, TlsPsk = psk });
        ZabbixSender With(string key) => new([trapper], new ZabbixConnectionOptions { Timeout = TimeSpan.FromSeconds(5), Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = identity, Psk = key } });
        using ZabbixSender sender2 = With(psk);
        await LiveFixtures.SendWhenKnownAsync(sender2, [new ZabbixValue(host.Host, "zbxtest.float", "1", DateTimeOffset.UtcNow)]);   // the identity is in the cache now

        await Assert.ThrowsAsync<AuthenticationException>(() => With(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))).SendAsync([new ZabbixValue(host.Host, "zbxtest.float", "2", DateTimeOffset.UtcNow)], TestContext.Current.CancellationToken));
    }

    [Theory, MemberData(nameof(LiveMatrix.Versions), MemberType = typeof(LiveMatrix))]
    public async Task A_host_that_accepts_only_certificates_takes_values_over_the_lab_certificate(string version)
    {
        (LiveTarget target, ZabbixServerNode trapper) = LiveMatrix.RequireTrapper(version);
        target.RequireFeature("cert", "the server needs TLSCAFile/TLSCertFile/TLSKeyFile to accept a certificate");
        if (LiveMatrix.Pki is not { } pki) { Assert.Skip("ZABBIX_TEST_PKI is not set"); return; }
        await using LiveScope scope = LiveScope.Open(version, "cert");
        LiveFixtures.LiveHost host = await LiveFixtures.HostAsync(scope, "cert", [("zbxtest.float", ZabbixItemValueType.Float)],
            r => r with { TlsAccept = ZabbixTlsAcceptModes.Certificate, TlsConnect = ZabbixTlsConnectMode.Certificate });
        var tls = new ZabbixTlsOptions
        {
            Mode = ZabbixTlsMode.Certificate,
            CaFile = Path.Combine(pki, "ca.crt"),
            CertificateFile = Path.Combine(pki, "client.crt"),
            KeyFile = Path.Combine(pki, "client.key"),
            PeerCertificateSubject = "CN=zbxlab-server,O=Zabbix lab",
        };

        using ZabbixSender sender2 = new ZabbixSender([trapper], new ZabbixConnectionOptions { Tls = tls });
        ZabbixSenderResponse sent = await LiveFixtures.SendWhenKnownAsync(sender2, [new ZabbixValue(host.Host, "zbxtest.float", "9", DateTimeOffset.UtcNow)]);

        Assert.Equal((1, 0), (sent.Processed, sent.Failed));
        Assert.Equal(["9"], (await LiveFixtures.HistoryAsync(scope.Server, host["zbxtest.float"], 1)).Select(r => r.Value));
    }
}
