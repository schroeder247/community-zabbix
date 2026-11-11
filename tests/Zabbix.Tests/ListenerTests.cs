// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Community.Zabbix.Listening;
using Community.Zabbix.Protocol;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The listener: Kestrel's socket transport with one framed request per connection, peers allowed by Zabbix's list (fail
/// closed), a global and a per-peer cap, a deadline, <c>TLSAccept</c> as a set, one fault contract, and its telemetry.
/// Every wait is on an observable: a handler's TCS, or <c>StopAsync</c>, which returns when the connections in flight are done.
/// </summary>
public sealed class ListenerTests
{
    internal const string Psk = "1f87b595725ac58dd977beef14b97461a7c1045b9a1c963065002c5473194952";

    internal static SocketTransportFactory Transport() => new(Options.Create(new SocketTransportOptions()), NullLoggerFactory.Instance);

    private static async Task<(ZabbixListener Listener, ZabbixServerNode Node)> StartAsync(ZabbixRequestCallback handler, Action<ZabbixListenerOptions>? configure = null, ILogger<ZabbixListener>? logger = null, IMeterFactory? meters = null, TimeProvider? time = null)
    {
        var options = new ZabbixListenerOptions { Address = "127.0.0.1", Port = 0, Timeout = TimeSpan.FromSeconds(5) };
        options.AllowedPeers.Add("127.0.0.1");
        configure?.Invoke(options);
        using ILoggerFactory? loggers = SingleLoggerFactory.Of(logger);
        var listener = new ZabbixListener(options, handler, Transport(), loggers, time, meters);
        await listener.StartingAsync(default);
        await listener.StartAsync(default);
        var bound = (IPEndPoint)listener.LocalEndPoint!;
        return (listener, new ZabbixServerNode(bound.Address.ToString(), bound.Port));
    }

    // Stopping waits for the connections in flight, so everything a connection logs or measures has happened after it.
    private static async Task StopAsync(ZabbixListener listener)
    {
        await listener.StoppingAsync(default);
        await listener.StopAsync(default);
        await listener.StoppedAsync(default);
        await listener.DisposeAsync();
    }

    private static ValueTask<ReadOnlyMemory<byte>> Echo(ZabbixRequest request, CancellationToken _) => ValueTask.FromResult(request.Payload);

    private static ServiceProvider Metrics() => new ServiceCollection().AddMetrics().BuildServiceProvider();

    private static ZabbixExchangeCallback PskClient(ZabbixServerNode node, string identity, string key)
        => ZabbixFrames.Connect(node, new ZabbixConnectionOptions { Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = identity, Psk = key } });

    [Fact]
    public async Task One_frame_in_one_frame_out_over_the_platform_s_transport()
    {
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync((request, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>(Encoding.UTF8.GetBytes("echo:" + Encoding.UTF8.GetString(request.Payload.Span))));
        ReadOnlyMemory<byte> reply;
        try
        {
            reply = await ZabbixFrames.Connect(node)(Encoding.UTF8.GetBytes("hello"), TestContext.Current.CancellationToken);
        }
        finally { await StopAsync(listener); }

        Assert.Equal("echo:hello", Encoding.UTF8.GetString(reply.Span));
        Assert.Equal(0, listener.InFlight);
    }

    [Fact]
    public void A_refusal_is_logged_and_counted_by_a_code_never_by_prose()
    {
        List<string> codes = [.. typeof(ZabbixListenerRejection).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral)
            .Select(field => (string)field.GetRawConstantValue()!)];
        MethodInfo logged = typeof(ZabbixListener).Assembly.GetType("Community.Zabbix.Listening.ListenerLog")!.GetMethod("PeerRefused")!;

        Assert.NotEmpty(codes);
        Assert.All(codes, code => Assert.Matches("^[a-z]+(_[a-z]+)*$", code));
        Assert.Equal(["logger", "peer", "rejection"], logged.GetParameters().Select(parameter => parameter.Name));
    }

    [Fact]
    public async Task A_peer_outside_the_allowed_blocks_is_closed_without_a_reply_and_counted()
    {
        var logs = new FakeLogCollector();
        using ServiceProvider metrics = Metrics();
        using var rejected = new MetricCollector<long>(metrics.GetRequiredService<IMeterFactory>(), ZabbixListenerTelemetry.Name, ZabbixListenerTelemetry.RejectedConnections);
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync((_, _) => ValueTask.FromResult<ReadOnlyMemory<byte>>("never"u8.ToArray()), o => { o.AllowedPeers.Clear(); o.AllowedPeers.Add("10.0.0.0/8"); }, new FakeLogger<ZabbixListener>(logs), metrics.GetRequiredService<IMeterFactory>());
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(async () => await ZabbixFrames.Connect(node)("hello"u8.ToArray(), TestContext.Current.CancellationToken));
        }
        finally { await StopAsync(listener); }

        FakeLogRecord logged = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 6003);
        Assert.True(ListenerRefusals.Is(logged, ZabbixListenerRejection.PeerNotAllowed));
        Assert.Equal(LogLevel.Warning, logged.Level);
        Assert.DoesNotContain("10.0.0.0", logged.Message, StringComparison.Ordinal);            // the code, never the allowed list
        CollectedMeasurement<long> refused = Assert.Single(rejected.GetMeasurementSnapshot());
        Assert.Equal(ZabbixListenerRejection.PeerNotAllowed, refused.Tags[ZabbixListenerTelemetry.RejectionTag]);
    }

    [Fact]
    public async Task A_peer_over_its_connection_cap_is_refused_while_the_first_is_served()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new FakeLogCollector();
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(async (_, token) => { entered.TrySetResult(); await gate.Task.WaitAsync(token); return "done"u8.ToArray(); }, o => o.MaxConnectionsPerPeer = 1, new FakeLogger<ZabbixListener>(logs));
        ReadOnlyMemory<byte> reply;
        try
        {
            ZabbixExchangeCallback exchange = ZabbixFrames.Connect(node);
            Task<ReadOnlyMemory<byte>> first = exchange("one"u8.ToArray(), TestContext.Current.CancellationToken).AsTask();
            await entered.Task;                                                                   // the first holds the peer's one slot now

            await Assert.ThrowsAnyAsync<Exception>(async () => await exchange("two"u8.ToArray(), TestContext.Current.CancellationToken));
            gate.SetResult();
            reply = await first;
        }
        finally { await StopAsync(listener); }

        Assert.Equal("done", Encoding.UTF8.GetString(reply.Span));
        Assert.Contains(logs.GetSnapshot(), record => ListenerRefusals.Is(record, ZabbixListenerRejection.PeerLimit));
    }

    [Fact]
    public async Task A_request_past_the_deadline_is_cut_and_logged()
    {
        var logs = new FakeLogCollector();
        var time = new FakeTimeProvider();
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(async (_, token) =>
        {
            arrived.TrySetResult();
            await new TaskCompletionSource().Task.WaitAsync(token);                             // never answers
            return default;
        }, o => o.Timeout = TimeSpan.FromSeconds(7), new FakeLogger<ZabbixListener>(logs), time: time);
        try
        {
            ZabbixExchangeCallback exchange = ZabbixFrames.Connect(node, new ZabbixConnectionOptions { Timeout = TimeSpan.FromSeconds(30) });

            ValueTask<ReadOnlyMemory<byte>> pending = exchange("slow"u8.ToArray(), TestContext.Current.CancellationToken);
            await arrived.Task.WaitAsync(TestContext.Current.CancellationToken);                  // the request is being handled: its deadline runs
            time.Advance(TimeSpan.FromSeconds(7));
            await Assert.ThrowsAnyAsync<Exception>(async () => await pending);
        }
        finally { await StopAsync(listener); }

        Assert.Contains(logs.GetSnapshot(), record => ListenerRefusals.Is(record, ZabbixListenerRejection.Timeout));
    }

    [Fact]
    public async Task A_handler_fault_answers_the_peer_a_fixed_failure_that_names_no_internals()
    {
        var logs = new FakeLogCollector();
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync((_, _) => throw new InvalidOperationException("the store is gone"), logger: new FakeLogger<ZabbixListener>(logs));
        ReadOnlyMemory<byte> reply;
        try
        {
            reply = await ZabbixFrames.Connect(node)("{}"u8.ToArray(), TestContext.Current.CancellationToken);
        }
        finally { await StopAsync(listener); }

        using JsonDocument answer = JsonDocument.Parse(reply);
        Assert.Equal("failed", answer.RootElement.GetProperty("response").GetString());
        Assert.Equal("the request could not be processed", answer.RootElement.GetProperty("info").GetString());   // the fault's type stays in the log
        FakeLogRecord fault = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 6005);
        Assert.Equal(LogLevel.Error, fault.Level);
        Assert.IsType<InvalidOperationException>(fault.Exception);
    }

    // A handler deaf to its token loses its connection at the deadline and runs on; a fault it raises after
    // that reaches nobody on the wire, so it is logged (6007), never left an unobserved task exception.
    [Fact]
    public async Task A_handler_that_outlives_its_deadline_and_then_fails_is_logged_not_unobserved()
    {
        var time = new FakeTimeProvider();
        var logs = new FakeLogCollector();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task;                                                                    // deaf to its token
            throw new InvalidOperationException("too late");
        }, logger: new FakeLogger<ZabbixListener>(logs), time: time);
        try
        {
            ValueTask<ReadOnlyMemory<byte>> pending = ZabbixFrames.Connect(node)("{}"u8.ToArray(), TestContext.Current.CancellationToken);
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            time.Advance(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAnyAsync<Exception>(async () => await pending);                   // released: closed without a reply
            release.SetResult();
            using var failsafe = new CancellationTokenSource(TimeSpan.FromSeconds(10), TimeProvider.System);
            while (!logs.GetSnapshot().Any(record => record.Id.Id == 6007))
            {
                failsafe.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }
        finally { await StopAsync(listener); }

        FakeLogRecord late = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 6007);
        Assert.Equal(LogLevel.Warning, late.Level);
        Assert.IsType<InvalidOperationException>(late.Exception);
        Assert.DoesNotContain(logs.GetSnapshot(), record => record.Id.Id == 6005);
    }

    [Fact]
    public async Task A_listener_without_allowed_peers_refuses_to_start_not_to_construct()
    {
        var options = new ZabbixListenerOptions { Address = "127.0.0.1", Port = 0 };
        await using var listener = new ZabbixListener(options, Echo, Transport());

        OptionsValidationException refused = await Assert.ThrowsAsync<OptionsValidationException>(() => listener.StartingAsync(TestContext.Current.CancellationToken));

        Assert.Contains(refused.Failures, failure => failure.Contains("empty", StringComparison.Ordinal));
        Assert.DoesNotContain(refused.Failures, failure => failure.Contains("AllowedPeers: AllowedPeers", StringComparison.Ordinal));   // the setting is named once
        Assert.Null(listener.LocalEndPoint);
    }

    [Fact]
    public async Task A_disposed_listener_releases_its_port()
    {
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(Echo);

#pragma warning disable CA1849 // the synchronous Dispose IS the behaviour under test: the container's path, no stop phases
        listener.Dispose();
#pragma warning restore CA1849

        using var again = new TcpListener(IPAddress.Loopback, node.Port);
        again.Start();
        Assert.Equal(node.Port, ((IPEndPoint)again.LocalEndpoint).Port);
    }

    [Fact]
    public async Task An_allowed_peer_given_by_name_is_resolved_when_the_peer_connects()
    {
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(Echo, o => { o.AllowedPeers.Clear(); o.AllowedPeers.Add("localhost"); });
        ReadOnlyMemory<byte> reply;
        try
        {
            reply = await ZabbixFrames.Connect(node)("named"u8.ToArray(), TestContext.Current.CancellationToken);
        }
        finally { await StopAsync(listener); }

        Assert.Equal("named", Encoding.UTF8.GetString(reply.Span));
    }

    [Fact]
    public async Task An_allowed_peer_name_that_does_not_resolve_matches_nothing_and_is_logged_once_an_hour()
    {
        var logs = new FakeLogCollector();
        var options = new ZabbixListenerOptions { Address = "127.0.0.1", Port = 0 };
        options.AllowedPeers.Add("zabbix.invalid");
        using var loggers = new SingleLoggerFactory(new FakeLogger<ZabbixListener>(logs));
        var listener = new ZabbixListener(options, Echo, Transport(), loggers: loggers, time: null, meters: null)
        {
            Resolver = (_, _) => Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound)),
        };
        await listener.StartingAsync(TestContext.Current.CancellationToken);
        await listener.StartAsync(TestContext.Current.CancellationToken);
        var bound = (IPEndPoint)listener.LocalEndPoint!;
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(async () => await ZabbixFrames.Connect(new ZabbixServerNode("127.0.0.1", bound.Port))("x"u8.ToArray(), TestContext.Current.CancellationToken));
            await Assert.ThrowsAnyAsync<Exception>(async () => await ZabbixFrames.Connect(new ZabbixServerNode("127.0.0.1", bound.Port))("x"u8.ToArray(), TestContext.Current.CancellationToken));   // a DNS outage fails every connection
        }
        finally { await StopAsync(listener); }

        FakeLogRecord unresolved = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 6006);
        Assert.Equal(LogLevel.Warning, unresolved.Level);
        Assert.Contains("zabbix.invalid", unresolved.Message, StringComparison.Ordinal);
        Assert.Contains(logs.GetSnapshot(), record => record.Id.Id == 6003);
    }

    [Fact]
    public async Task Disposing_a_listener_waits_for_the_connections_in_flight_before_it_releases_what_they_use()
    {
        using ServiceProvider metrics = Metrics();
        using var duration = new MetricCollector<double>(metrics.GetRequiredService<IMeterFactory>(), ZabbixListenerTelemetry.Name, ZabbixListenerTelemetry.ConnectionDuration);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);                         // in flight until the listener goes
            return default;
        }, meters: metrics.GetRequiredService<IMeterFactory>());
        Task<ReadOnlyMemory<byte>> pending = ZabbixFrames.Connect(node)("{}"u8.ToArray(), TestContext.Current.CancellationToken).AsTask();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        await listener.DisposeAsync();

        Assert.Single(duration.GetMeasurementSnapshot());                               // the connection ended, and was measured, before disposal returned
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
    }

    [Fact]
    public async Task A_served_connection_leaves_a_server_span_and_a_duration_sample_that_agree()
    {
        using ServiceProvider metrics = Metrics();
        using var duration = new MetricCollector<double>(metrics.GetRequiredService<IMeterFactory>(), ZabbixListenerTelemetry.Name, ZabbixListenerTelemetry.ConnectionDuration);
        using var active = new MetricCollector<long>(metrics.GetRequiredService<IMeterFactory>(), ZabbixListenerTelemetry.Name, ZabbixListenerTelemetry.ActiveConnections);
        var spans = new ConcurrentQueue<Activity>();
        using var spy = new ActivityListener
        {
            ShouldListenTo = source => source.Name == ZabbixListenerTelemetry.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = spans.Enqueue,
        };
        ActivitySource.AddActivityListener(spy);
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync((_, _) => throw new InvalidOperationException("boom"), meters: metrics.GetRequiredService<IMeterFactory>());
        try
        {
            await ZabbixFrames.Connect(node)("x"u8.ToArray(), TestContext.Current.CancellationToken);
        }
        finally { await StopAsync(listener); }

        Activity span = Assert.Single(spans, activity => activity.GetTagItem("server.port") is int port && port == node.Port);
        Assert.Equal(ZabbixListenerTelemetry.RequestSpan, span.OperationName);
        Assert.Equal(ActivityKind.Server, span.Kind);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal(typeof(InvalidOperationException).FullName, span.GetTagItem("error.type"));
        Assert.Equal("unencrypted", span.GetTagItem(ZabbixListenerTelemetry.SecurityTag));
        CollectedMeasurement<double> sample = Assert.Single(duration.GetMeasurementSnapshot());
        Assert.Equal(typeof(InvalidOperationException).FullName, sample.Tags["error.type"]);
        Assert.Equal("unencrypted", sample.Tags[ZabbixListenerTelemetry.SecurityTag]);
        Assert.Equal([1L, -1L], active.GetMeasurementSnapshot().Select(measurement => measurement.Value));
    }

    [Fact]
    public async Task A_psk_listener_serves_the_same_words_and_refuses_plain_text()
    {
        var logs = new FakeLogCollector();
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(Echo, o => { o.TlsAccept = ZabbixTlsAcceptModes.Psk; o.Tls = new ZabbixTlsOptions { PskIdentity = "edge-01", Psk = Psk }; }, new FakeLogger<ZabbixListener>(logs));
        ReadOnlyMemory<byte> reply;
        try
        {
            reply = await PskClient(node, "edge-01", Psk)("secret"u8.ToArray(), TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<Exception>(async () => await ZabbixFrames.Connect(node)("secret"u8.ToArray(), TestContext.Current.CancellationToken));
        }
        finally { await StopAsync(listener); }

        Assert.Equal("secret", Encoding.UTF8.GetString(reply.Span));
        Assert.Contains(logs.GetSnapshot(), record => ListenerRefusals.Is(record, ZabbixListenerRejection.UnencryptedNotAllowed));
    }

    [Fact]
    public async Task A_listener_accepting_unencrypted_and_psk_serves_each_connection_as_it_arrives()
    {
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(Echo, o => { o.TlsAccept = ZabbixTlsAcceptModes.Unencrypted | ZabbixTlsAcceptModes.Psk; o.Tls = new ZabbixTlsOptions { PskIdentity = "edge-01", Psk = Psk }; });
        ReadOnlyMemory<byte> secured;
        ReadOnlyMemory<byte> plain;
        try
        {
            secured = await PskClient(node, "edge-01", Psk)("psk"u8.ToArray(), TestContext.Current.CancellationToken);
            plain = await ZabbixFrames.Connect(node)("plain"u8.ToArray(), TestContext.Current.CancellationToken);
        }
        finally { await StopAsync(listener); }

        Assert.Equal("psk", Encoding.UTF8.GetString(secured.Span));
        Assert.Equal("plain", Encoding.UTF8.GetString(plain.Span));
    }

    [Fact]
    public async Task A_psk_listener_answers_each_identity_with_its_own_key_and_refuses_an_unknown_one()
    {
        const string other = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
        var logs = new FakeLogCollector();
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(Echo, o => { o.TlsAccept = ZabbixTlsAcceptModes.Psk; o.PreSharedKeys["host-a"] = Psk; o.PreSharedKeys["host-b"] = other; }, new FakeLogger<ZabbixListener>(logs));
        ReadOnlyMemory<byte> a;
        ReadOnlyMemory<byte> b;
        try
        {
            a = await PskClient(node, "host-a", Psk)("a"u8.ToArray(), TestContext.Current.CancellationToken);
            b = await PskClient(node, "host-b", other)("b"u8.ToArray(), TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<Exception>(async () => await PskClient(node, "stranger", Psk)("c"u8.ToArray(), TestContext.Current.CancellationToken));
        }
        finally { await StopAsync(listener); }

        Assert.Equal("a", Encoding.UTF8.GetString(a.Span));
        Assert.Equal("b", Encoding.UTF8.GetString(b.Span));
        FakeLogRecord failed = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 6002);
        Assert.Equal(LogLevel.Warning, failed.Level);
        Assert.IsType<AuthenticationException>(failed.Exception);
    }

    [Fact]
    public async Task A_psk_handshake_that_stalls_is_cut_at_the_deadline()
    {
        var logs = new FakeLogCollector();
        var time = new WatchedClock();
        (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(Echo, o => { o.Timeout = TimeSpan.FromSeconds(7); o.TlsAccept = ZabbixTlsAcceptModes.Psk; o.Tls = new ZabbixTlsOptions { PskIdentity = "edge-01", Psk = Psk }; }, new FakeLogger<ZabbixListener>(logs), time: time);
        using var client = new TcpClient();
        int read;
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, node.Port, TestContext.Current.CancellationToken);
            await client.GetStream().WriteAsync(new byte[] { 0x16, 0x03, 0x01, 0x00, 0x40 }, TestContext.Current.CancellationToken);    // a handshake record header, then nothing
            await time.WaitForTimerAsync(TimeSpan.FromSeconds(7), TestContext.Current.CancellationToken);                              // the connection's deadline exists
            time.Advance(TimeSpan.FromSeconds(7));
            read = await ListenerCuts.ReadOrResetAsync(client.GetStream());                 // the listener closes at its deadline
        }
        finally { await StopAsync(listener); }

        Assert.Equal(0, read);
        Assert.Contains(logs.GetSnapshot(), record => ListenerRefusals.Is(record, ZabbixListenerRejection.Timeout));
    }

    [Fact]
    public async Task A_listener_accepting_psk_and_cert_routes_each_hello_to_its_stack()
    {
        string directory = Directory.CreateTempSubdirectory("zbx-listener-").FullName;
        try
        {
            using RSA key = RSA.Create(2048);
            var request = new CertificateRequest("CN=zabbix-listener-test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            var names = new SubjectAlternativeNameBuilder();
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using X509Certificate2 certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
            string certificateFile = Path.Combine(directory, "server.crt");
            string keyFile = Path.Combine(directory, "server.key");
            await File.WriteAllTextAsync(certificateFile, certificate.ExportCertificatePem(), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(keyFile, key.ExportPkcs8PrivateKeyPem(), TestContext.Current.CancellationToken);
            (ZabbixListener listener, ZabbixServerNode node) = await StartAsync(Echo, o =>
            {
                o.TlsAccept = ZabbixTlsAcceptModes.Psk | ZabbixTlsAcceptModes.Certificate;
                o.Tls = new ZabbixTlsOptions { PskIdentity = "edge-01", Psk = Psk, CertificateFile = certificateFile, KeyFile = keyFile, CaFile = certificateFile };
            });
            ReadOnlyMemory<byte> psk;
            ReadOnlyMemory<byte> cert;
            try
            {
                psk = await PskClient(node, "edge-01", Psk)("psk"u8.ToArray(), TestContext.Current.CancellationToken);
                cert = await ZabbixFrames.Connect(node, new ZabbixConnectionOptions { Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.Certificate, CaFile = certificateFile, CertificateFile = certificateFile, KeyFile = keyFile } })("cert"u8.ToArray(), TestContext.Current.CancellationToken);
                await Assert.ThrowsAnyAsync<Exception>(async () => await ZabbixFrames.Connect(node)("plain"u8.ToArray(), TestContext.Current.CancellationToken));
            }
            finally { await StopAsync(listener); }

            Assert.Equal("psk", Encoding.UTF8.GetString(psk.Span));
            Assert.Equal("cert", Encoding.UTF8.GetString(cert.Span));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void The_psk_acceptor_offers_the_client_side_suites()
        => Assert.Equal(ZabbixTls.s_pskCipherSuites, ZabbixTlsAcceptor.s_cipherSuites);

    [Fact]
    public void A_connect_mode_on_a_listener_s_tls_words_is_refused_in_favour_of_the_accept_set()
    {
        var options = new ZabbixListenerOptions { Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = "edge", Psk = Psk } };
        options.AllowedPeers.Add("127.0.0.1");

        ValidateOptionsResult result = new ZabbixListenerOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("TlsAccept", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void An_accept_set_outside_unencrypted_psk_and_cert_is_refused(int accept)
    {
        var options = new ZabbixListenerOptions { TlsAccept = (ZabbixTlsAcceptModes)accept };
        options.AllowedPeers.Add("127.0.0.1");

        Assert.True(new ZabbixListenerOptionsValidator().Validate(null, options).Failed);
    }

    [Fact]
    public void Psk_accepted_without_a_key_is_refused_at_validation()
    {
        var options = new ZabbixListenerOptions { TlsAccept = ZabbixTlsAcceptModes.Psk };
        options.AllowedPeers.Add("127.0.0.1");
        options.PreSharedKeys["edge"] = "not-hex";

        ValidateOptionsResult result = new ZabbixListenerOptionsValidator().Validate(null, options);

        Assert.Contains(result.Failures!, failure => failure.Contains("\"edge\"", StringComparison.Ordinal));
    }
}
