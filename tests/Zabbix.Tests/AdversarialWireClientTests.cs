// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using Community.Zabbix.Listening;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The wire's client side against peers that misbehave: a port that accepts and closes without a word, ten thousand
/// values in one send, an agent whose answer is past the cap or written oddly, a PSK identity the listener does not know,
/// and a CRL file that cannot be read. Each is one designed failure, logged once at the façade's level.
/// </summary>
[Collection("Adversarial wire stress")]
public sealed class AdversarialWireClientTests
{
    private const string Key = "1f87b595725ac58dd977beef14b97461a7c1045b9a1c963065002c5473194952";

    /// <summary>Accepts one connection, reads what arrives (or not) and closes it without answering.</summary>
    private static (ZabbixServerNode Node, Task Closed) Silent(bool read)
    {
        var port = new System.Runtime.CompilerServices.StrongBox<int>();
        Task closed = SilentAsync(port, read);                                                 // binds before its first await
        return (new ZabbixServerNode("127.0.0.1", port.Value), closed);
    }

    private static async Task SilentAsync(System.Runtime.CompilerServices.StrongBox<int> port, bool read)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        port.Value = ((IPEndPoint)listener.LocalEndpoint).Port;
        using TcpClient client = await listener.AcceptTcpClientAsync();
        if (read) await ZabbixHeader.ReadAsync(client.GetStream());
        client.Client.Shutdown(SocketShutdown.Both);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_port_that_accepts_and_closes_without_answering_is_one_exchange_failure_logged_once(bool afterReading)
    {
        (ZabbixServerNode node, Task closed) = Silent(afterReading);
        var logs = new FakeLogCollector();
        using var sender = new ZabbixSender([node], null, null, new FakeLogger<ZabbixSender>(logs));

        Exception failure = await Assert.ThrowsAnyAsync<Exception>(() => sender.SendAsync([new ZabbixValue("h", "k", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken));
        await closed;

        Assert.True(ZabbixFrames.IsExchangeFailure(failure), failure.GetType().FullName);
        // Logged once, by the façade, at Debug: the caller has the exception and logs it at the level it chooses.
        FakeLogRecord reported = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 1003);
        Assert.Equal(LogLevel.Debug, reported.Level);
        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Level >= LogLevel.Warning);
    }

    // zabbix_sender sends at most 250 values a frame (VALUES_MAX); this sender sends what it is given as one frame, so a
    // caller that wants upstream's batching chunks itself (or drains a store, whose send loop batches). Pinned here and
    // in docs/KNOWN-LIMITATIONS.md.
    [Fact]
    public async Task Ten_thousand_values_go_as_one_frame_and_are_counted_as_the_trapper_answers()
    {
        (int port, Task<int> received) = MockTrapper.Collect(10_000);
        using var sender = new ZabbixSender("127.0.0.1", port);
        ZabbixValue[] values = [.. Enumerable.Range(0, 10_000).Select(n => new ZabbixValue("h", "k", n.ToString(System.Globalization.CultureInfo.InvariantCulture), DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + n)))];

        ZabbixSenderResponse response = await sender.SendAsync(values, TestContext.Current.CancellationToken);

        Assert.Equal(10_000, await received);
        Assert.Equal(10_000, response.Processed);
    }

    [Fact]
    public async Task An_agent_answer_past_the_cap_is_invalid_data_logged_as_a_failed_ask()
    {
        (int port, Task<string> asked) = MockTrapper.Answer(new string('v', 4096));
        var logs = new FakeLogCollector();
        using var getter = new ZabbixGetter(new ZabbixServerNode("127.0.0.1", port), new ZabbixConnectionOptions { MaxPayloadBytes = 1024 }, null, new FakeLogger<ZabbixGetter>(logs));

        await Assert.ThrowsAsync<InvalidDataException>(() => getter.GetAsync("vfs.file.contents[/etc/motd]", TestContext.Current.CancellationToken));
        await asked;

        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 1201);
    }

    [Theory]
    [InlineData("""{"version":"7.0.0","variant":1,"data":[{"error":7}]}""", null, "7")]
    [InlineData("""{"version":"7.0.0","variant":1,"data":[{"value":12.5}]}""", "12.5", null)]
    [InlineData("""{"version":"7.0.0","variant":1,"data":[{"value":["a",1]}]}""", "[\"a\",1]", null)]
    public async Task An_agent_row_written_with_other_json_kinds_is_read_as_its_text(string reply, string? value, string? error)
    {
        using var getter = new ZabbixGetter((_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(reply)), "scripted");

        ZabbixAgentAnswer answer = await getter.GetAsync("agent.ping", TestContext.Current.CancellationToken);

        Assert.Equal(value, answer.Value);
        Assert.Equal(error, answer.Error);
    }

    [Theory]
    [InlineData("""{"version":"7.0.0","variant":1,"data":[]}""")]
    [InlineData("""{"version":"7.0.0","variant":1,"data":{}}""")]
    [InlineData("""{"version":"7.0.0","variant":1,"data":[5]}""")]
    [InlineData("""{"version":"7.0.0","variant":1,"data":[{}]}""")]
    public async Task An_agent_reply_without_a_readable_row_is_invalid_data_not_an_empty_answer(string reply)
    {
        using var getter = new ZabbixGetter((_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes(reply)), "scripted");

        await Assert.ThrowsAsync<InvalidDataException>(() => getter.GetAsync("agent.ping", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_psk_identity_the_listener_does_not_know_is_a_refused_handshake_logged_with_the_reason()
    {
        var options = new ZabbixListenerOptions { Address = "127.0.0.1", Port = 0, Timeout = TimeSpan.FromSeconds(5), TlsAccept = ZabbixTlsAcceptModes.Psk };
        options.AllowedPeers.Add("127.0.0.1");
        options.PreSharedKeys["edge-01"] = Key;
        await using var listener = new ZabbixListener(options, static (request, _) => ValueTask.FromResult(request.Payload), ListenerTests.Transport());
        await listener.StartingAsync(TestContext.Current.CancellationToken);
        await listener.StartAsync(TestContext.Current.CancellationToken);
        var node = new ZabbixServerNode("127.0.0.1", ((IPEndPoint)listener.LocalEndPoint!).Port);
        var logs = new FakeLogCollector();
        ZabbixExchangeCallback stranger = ZabbixFrames.Connect(node, new ZabbixConnectionOptions { Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = "edge-99", Psk = Key } }, logger: new FakeLogger(logs));
        ZabbixExchangeCallback known = ZabbixFrames.Connect(node, new ZabbixConnectionOptions { Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = "edge-01", Psk = Key } });

        await Assert.ThrowsAsync<AuthenticationException>(async () => await stranger("x"u8.ToArray(), TestContext.Current.CancellationToken));
        ReadOnlyMemory<byte> echo = await known("x"u8.ToArray(), TestContext.Current.CancellationToken);

        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 1103 && record.Message.Contains("pre-shared-key", StringComparison.Ordinal));
        Assert.Equal("x", Encoding.UTF8.GetString(echo.Span));                                // the same listener takes the identity it knows
        await listener.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public void A_crl_file_that_cannot_be_read_fails_when_the_exchange_is_built_not_on_every_connection()
    {
        string directory = Directory.CreateTempSubdirectory("zbx-crl-").FullName;
        try
        {
            string empty = Path.Combine(directory, "empty.pem");
            File.WriteAllText(empty, "not a pem file");
            var words = new ZabbixTlsOptions { Mode = ZabbixTlsMode.Certificate, CaFile = empty, CertificateFile = empty, KeyFile = empty, CrlFile = Path.Combine(directory, "missing.crl") };

            Exception failure = Assert.ThrowsAny<Exception>(() => ZabbixFrames.Over([new ZabbixServerNode("127.0.0.1")], new ZabbixConnectionOptions { Tls = words }));

            Assert.True(failure is IOException or System.Security.Cryptography.CryptographicException or InvalidDataException, failure.GetType().FullName);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
