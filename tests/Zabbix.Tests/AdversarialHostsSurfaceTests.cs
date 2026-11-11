// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using Community.Zabbix.Hosting;
using Community.Zabbix.Listening;
using Community.Zabbix.Protocol;
using Community.Zabbix.Proxy;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The hosting packages' surface: started-state objects instead of nullable fields, a log line per
/// distinct problem instead of per occurrence, handlers off the builder, the dictionary and formatting contracts.
/// </summary>
public sealed class AdversarialHostsSurfaceTests
{
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(30);

    private static ZabbixListenerOptions Loopback()
    {
        var options = new ZabbixListenerOptions { Address = "127.0.0.1", Port = 0, Timeout = TimeSpan.FromSeconds(5) };
        options.AllowedPeers.Add("127.0.0.1");
        return options;
    }

    private static ValueTask<ReadOnlyMemory<byte>> Never(ZabbixRequest request, CancellationToken cancellationToken) => ValueTask.FromResult<ReadOnlyMemory<byte>>("never"u8.ToArray());

    [Fact]
    public async Task A_synchronous_disposal_whose_unbind_then_fails_logs_the_failure_instead_of_leaving_it_unobserved()
    {
        var logs = new FakeLogCollector();
        using var loggers = new SingleLoggerFactory(new FakeLogger<ZabbixListener>(logs));
        var listener = new ZabbixListener(Loopback(), Never, new FailingUnbindTransport(), loggers: loggers, time: null, meters: null);
        await listener.StartingAsync(TestContext.Current.CancellationToken);

#pragma warning disable CA1849 // the synchronous disposal is the path under test
        listener.Dispose();
#pragma warning restore CA1849

        using var waited = new CancellationTokenSource(Failsafe);
        await foreach (FakeLogRecord record in logs.GetLogsAsync(waited.Token))
            if (record.Id.Id == 6009) break;
        FakeLogRecord failed = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 6009);
        Assert.IsType<IOException>(failed.Exception);
    }

    [Fact]
    public async Task The_zabbix_socket_transport_is_keyed_and_leaves_the_application_s_own_transport_alone()
    {
        var services = new ServiceCollection();
        var web = new FailingUnbindTransport();                                               // what a web host registers (Kestrel's, named pipes, QUIC)
        services.AddSingleton<IConnectionListenerFactory>(web);
        services.AddZabbixSocketTransport();
        services.AddZabbixSocketTransport();
        await using ServiceProvider provider = services.BuildServiceProvider();

        Assert.Same(web, provider.GetRequiredService<IConnectionListenerFactory>());
        Assert.IsType<Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets.SocketTransportFactory>(provider.GetRequiredKeyedService<IConnectionListenerFactory>(ZabbixListener.TransportKey));
        await using ServiceProvider bare = new ServiceCollection().AddZabbixSocketTransport().BuildServiceProvider();
        Assert.Null(bare.GetService<IConnectionListenerFactory>());                            // nothing unkeyed of its own
    }

    [Fact]
    public async Task A_listener_started_before_it_is_bound_says_so_and_a_second_bind_keeps_the_first_port()
    {
        var services = new ServiceCollection();
        services.AddZabbixSocketTransport();
        await using ServiceProvider provider = services.BuildServiceProvider();
        await using var listener = new ZabbixListener(Loopback(), Never, provider.GetRequiredKeyedService<IConnectionListenerFactory>(ZabbixListener.TransportKey));

        await Assert.ThrowsAsync<InvalidOperationException>(() => listener.StartAsync(TestContext.Current.CancellationToken));
        await listener.StartingAsync(TestContext.Current.CancellationToken);
        EndPoint? first = listener.LocalEndPoint;
        await listener.StartingAsync(TestContext.Current.CancellationToken);

        Assert.Equal(first, listener.LocalEndPoint);
    }

    [Fact]
    public async Task A_peer_refused_again_and_again_is_logged_once_and_counted_every_time()
    {
        var logs = new FakeLogCollector();
        var services = new ServiceCollection();
        services.AddZabbixSocketTransport();
        services.AddMetrics();
        await using ServiceProvider provider = services.BuildServiceProvider();
        using var rejected = new MetricCollector<long>(provider.GetRequiredService<IMeterFactory>(), ZabbixListenerTelemetry.Name, ZabbixListenerTelemetry.RejectedConnections);
        ZabbixListenerOptions options = Loopback();
        options.AllowedPeers.Clear();
        options.AllowedPeers.Add("10.0.0.0/8");
        using var loggers = new SingleLoggerFactory(new FakeLogger<ZabbixListener>(logs));
        var listener = new ZabbixListener(options, Never, provider.GetRequiredKeyedService<IConnectionListenerFactory>(ZabbixListener.TransportKey), loggers: loggers, time: null, meters: provider.GetRequiredService<IMeterFactory>());
        await listener.StartingAsync(TestContext.Current.CancellationToken);
        await listener.StartAsync(TestContext.Current.CancellationToken);
        var bound = (IPEndPoint)listener.LocalEndPoint!;
        try
        {
            ZabbixExchangeCallback exchange = ZabbixFrames.Connect(new ZabbixServerNode(bound.Address.ToString(), bound.Port));
            for (int i = 0; i < 5; i++)
                await Assert.ThrowsAnyAsync<Exception>(async () => await exchange("x"u8.ToArray(), TestContext.Current.CancellationToken));
        }
        finally
        {
            await listener.StoppingAsync(TestContext.Current.CancellationToken);
            await listener.StopAsync(TestContext.Current.CancellationToken);
            await listener.StoppedAsync(TestContext.Current.CancellationToken);
            await listener.DisposeAsync();
        }

        Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 6003);
        Assert.Equal(5, rejected.GetMeasurementSnapshot().Count);
    }

    [Fact]
    public void A_value_refused_again_and_again_is_logged_once_per_host_key_and_reason_and_counted_every_time()
    {
        var logs = new FakeLogCollector();
        var services = new ServiceCollection().AddMetrics();
        using ServiceProvider provider = services.BuildServiceProvider();
        using var telemetry = new ZabbixProxyTelemetry(provider.GetRequiredService<IMeterFactory>(), new FakeTimeProvider());
        using var values = new MetricCollector<long>(provider.GetRequiredService<IMeterFactory>(), ZabbixProxyTelemetry.Name, ZabbixProxyTelemetry.ValuesInstrument);
        Action<ZabbixValue, string> refused = ZabbixProxyAdmissionLogging.Refused(new FakeLogger(logs), "proxy-01", IPAddress.Loopback, telemetry);

        for (int i = 0; i < 5; i++) refused(new ZabbixValue("web-01", "cpu", "1", DateTimeOffset.UnixEpoch), "no such host and key in the configuration");
        refused(new ZabbixValue("web-01", "mem", "1", DateTimeOffset.UnixEpoch), "no such host and key in the configuration");
        refused(new ZabbixValue("web-01", "cpu", "1", DateTimeOffset.UnixEpoch), "item is disabled");

        Assert.Equal(3, logs.GetSnapshot().Count(record => record.Id.Id == 7020));
        Assert.Equal(7, values.GetMeasurementSnapshot().Sum(measurement => measurement.Value));
    }

    [Fact]
    public void A_problem_that_comes_back_after_the_window_is_said_again_and_past_capacity_the_rest_are_sampled()
    {
        var time = new FakeTimeProvider();
        var gate = new ZabbixLogOnce<int>(capacity: 2, TimeSpan.FromHours(1), time);

        Assert.True(gate.ShouldLog(1));
        Assert.False(gate.ShouldLog(1));
        Assert.True(gate.ShouldLog(2));
        bool[] beyond = [.. Enumerable.Range(3, 10).Select(gate.ShouldLog)];             // the 1st and the 10th past capacity
        time.Advance(TimeSpan.FromHours(1));

        Assert.Equal([true, false, false, false, false, false, false, false, false, true], beyond);
        Assert.True(gate.ShouldLog(1));
    }

    [Fact]
    public void Item_handlers_are_a_dictionary_whose_add_refuses_a_name_taken_and_whose_indexer_replaces()
    {
        IDictionary<string, ZabbixItemCallback> handlers = new ZabbixItemDictionary();
        ZabbixItemCallback one = (key, _) => ValueTask.FromResult(new ZabbixAgentAnswer(key, "1", null));
        ZabbixItemCallback two = (key, _) => ValueTask.FromResult(new ZabbixAgentAnswer(key, "2", null));

        handlers.Add("app.one", one);
        Assert.Throws<ArgumentException>(() => handlers.Add("app.one", two));
        handlers["app.one"] = two;
        handlers.Add(new KeyValuePair<string, ZabbixItemCallback>("app.two", one));
        var copied = new KeyValuePair<string, ZabbixItemCallback>[2];
        handlers.CopyTo(copied, 0);

        Assert.Same(two, handlers["app.one"]);
        Assert.Contains(new KeyValuePair<string, ZabbixItemCallback>("app.two", one), handlers);
        Assert.Equal(["app.one", "app.two"], copied.Select(pair => pair.Key).Order(StringComparer.Ordinal));
        Assert.False(handlers.IsReadOnly);
        Assert.True(handlers.Remove(new KeyValuePair<string, ZabbixItemCallback>("app.two", one)));
        handlers.Clear();
        Assert.Empty(handlers);
    }

    [Fact]
    public void Handlers_added_to_an_agent_in_two_calls_are_two_keyed_sets_the_builder_does_not_hold()
    {
        var services = new ServiceCollection();
        services.AddZabbixPassiveAgent("edge").AddItem("app.one", _ => "1");
        services.AddZabbixPassiveAgent("edge").AddItem("app.two", _ => "2");
        services.AddZabbixPassiveAgent("other").AddItem("app.three", _ => "3");
        using ServiceProvider provider = services.BuildServiceProvider();

        ZabbixItemDictionary[] edge = [.. provider.GetKeyedServices<ZabbixItemDictionary>(new ZabbixItemsKey("edge"))];

        Assert.Equal(["app.one", "app.two"], edge.SelectMany(set => set.Keys).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(typeof(IZabbixPassiveAgentBuilder).GetProperties(), property => property.PropertyType == typeof(ZabbixItemDictionary));
        Assert.DoesNotContain(typeof(IZabbixActiveAgentBuilder).GetProperties(), property => property.PropertyType == typeof(ZabbixItemDictionary));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("G")]
    [InlineData("g")]
    public void A_delay_formats_as_written_in_the_general_format(string? format)
    {
        var delay = ZabbixItemDelay.Parse("1m;wd1-5h9", CultureInfo.InvariantCulture);

        Assert.Equal("1m;wd1-5h9", delay.ToString(format, CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_delay_refuses_a_format_it_does_not_have()
    {
        var delay = ZabbixItemDelay.Parse("30s", CultureInfo.InvariantCulture);
        var buffer = new char[16];

        Assert.Throws<FormatException>(() => delay.ToString("N", CultureInfo.InvariantCulture));
        Assert.Throws<FormatException>(() => delay.TryFormat(buffer, out _, "x", CultureInfo.InvariantCulture));
        Assert.Throws<FormatException>(() => string.Format(CultureInfo.InvariantCulture, "{0:D}", delay));
    }

    [Fact]
    public void A_negative_item_id_seed_phases_as_the_unsigned_id_upstream_reads()
    {
        var delay = ZabbixItemDelay.Parse("10s", CultureInfo.InvariantCulture);
        DateTimeOffset now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);              // a multiple of ten

        DateTimeOffset? next = delay.GetNextCheck(now, TimeZoneInfo.Utc, seed: -1);

        Assert.Equal(now.AddSeconds((double)(ulong.MaxValue % 10)), next);                    // 18446744073709551615 % 10 = 5
    }

    [Fact]
    public void A_delta_that_removes_several_hosts_removes_exactly_their_items_and_active_checks_answer_a_host_from_its_own()
    {
        const string Full = """
            {"full_sync":1,"config_revision":1,"data":{
              "hosts":{"fields":["hostid","host","status"],"data":[[10,"a",0],[11,"b",0],[12,"c",0]]},
              "items":{"fields":["itemid","type","hostid","key_","status","delay"],"data":[[100,7,10,"a.one",0,"30s"],[101,7,11,"b.one",0,"30s"],[102,7,12,"c.one",0,"30s"],[103,7,12,"c.two",0,"1m"]]}}}
            """;
        using JsonDocument full = JsonDocument.Parse(Full);
        using JsonDocument delta = JsonDocument.Parse("""{"config_revision":2,"del_hostids":[10,11]}""");
        ZabbixProxyConfig held = ZabbixProxyConfig.Empty.Merge(full.RootElement);

        ZabbixProxyConfig removed = held.Merge(delta.RootElement);
        ReadOnlyMemory<byte> answer = ZabbixProxyPaging.ActiveChecks(new ZabbixRequest("""{"request":"active checks","host":"c"}"""u8.ToArray(), null), removed);

        Assert.Equal<ulong>([102, 103], removed.Items.Keys.Order());
        Assert.Equal([12L], removed.ItemsByHost.Keys);
        using JsonDocument reply = JsonDocument.Parse(answer);
        Assert.Equal(["c.one", "c.two"], reply.RootElement.GetProperty("data").EnumerateArray().Select(item => item.GetProperty("key").GetString()).Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("psk-unaccepted", "the key would never be used")]
    [InlineData("table-unaccepted", "the key would never be used")]
    [InlineData("cert-unaccepted", "the certificate would never be used")]
    [InlineData("identity-alone", "set together or not at all")]
    [InlineData("crl-alone", "used only with Tls.CertificateFile")]
    public void A_listener_refuses_credentials_it_would_never_use_or_that_lack_their_pair(string shape, string failure)
    {
        ZabbixListenerOptions options = Loopback();
        switch (shape)
        {
            case "psk-unaccepted": options.Tls = new ZabbixTlsOptions { PskIdentity = "edge", Psk = new string('a', 32) }; break;
            case "table-unaccepted": options.PreSharedKeys["edge"] = new string('a', 32); break;
            case "cert-unaccepted": options.Tls = new ZabbixTlsOptions { CertificateFile = "/x.crt", KeyFile = "/x.key" }; break;
            case "identity-alone": options.TlsAccept = ZabbixTlsAcceptModes.Psk; options.PreSharedKeys["other"] = new string('b', 32); options.Tls = new ZabbixTlsOptions { PskIdentity = "edge" }; break;
            case "crl-alone": options.Tls = new ZabbixTlsOptions { CrlFile = "/x.crl" }; break;
        }

        Microsoft.Extensions.Options.ValidateOptionsResult result = new ZabbixListenerOptionsValidator().Validate(null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, message => message.Contains(failure, StringComparison.Ordinal));
    }

    [Fact]
    public void A_listener_that_accepts_what_its_credentials_are_for_is_valid()
    {
        ZabbixListenerOptions options = Loopback();
        options.TlsAccept = ZabbixTlsAcceptModes.Unencrypted | ZabbixTlsAcceptModes.Psk;
        options.PreSharedKeys["edge"] = new string('a', 32);

        Assert.True(new ZabbixListenerOptionsValidator().Validate(null, options).Succeeded);
    }

    public static TheoryData<string, System.Threading.Channels.BoundedChannelFullMode> StoreKinds => new()
    {
        { "memory", System.Threading.Channels.BoundedChannelFullMode.DropOldest },
        { "memory", System.Threading.Channels.BoundedChannelFullMode.Wait },
        { "durable", System.Threading.Channels.BoundedChannelFullMode.DropOldest },
        { "durable", System.Threading.Channels.BoundedChannelFullMode.Wait },
    };

    /// <summary>
    /// Eight writers at once into a store capped at 64 while one reader leases and acknowledges: every record written is
    /// either delivered or counted dropped, none twice, none lost; under Wait nothing is dropped at all.
    /// </summary>
    [Theory]
    [MemberData(nameof(StoreKinds))]
    public async Task Eight_writers_against_one_reader_account_for_every_record(string kind, System.Threading.Channels.BoundedChannelFullMode overflow)
    {
        const int Writers = 8, Each = 500;
        var policy = new Zabbix.Storage.ZabbixStorePolicy<int> { MaxItems = 64, Overflow = overflow };
        await using Community.Zabbix.Storage.ZabbixStore<int> store = kind == "memory" ? new Zabbix.Storage.ZabbixMemoryStore<int>(policy) : new AdversarialRowStore<int>(policy);
        using var failsafe = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        failsafe.CancelAfter(Failsafe);
        var delivered = new List<int>();
        Task reading = Task.Run(async () =>
        {
            while (await store.WaitToLeaseAsync(failsafe.Token))
            {
                long last = -1;
                while (store.TryLease(out Zabbix.Storage.ZabbixStoreEntry<int> entry))
                {
                    delivered.Add(entry.Record);
                    last = entry.Sequence;
                }
                if (last >= 0) store.Acknowledge(last);
            }
        }, failsafe.Token);

        await Task.WhenAll(Enumerable.Range(0, Writers).Select(writer => Task.Run(async () =>
        {
            for (int i = 0; i < Each; i++) await store.WriteAsync((writer * Each) + i, failsafe.Token);
        }, failsafe.Token)));
        Assert.True(store.TryComplete());
        await reading;

        Assert.Equal(delivered.Count, delivered.Distinct().Count());                          // none twice
        Assert.Equal(Writers * Each, delivered.Count + store.DroppedCount);                  // none lost uncounted
        if (overflow == System.Threading.Channels.BoundedChannelFullMode.Wait) Assert.Equal(0, store.DroppedCount);
    }

    [Fact]
    public void A_poll_refuses_a_buffer_it_cannot_hold()
        => Assert.Throws<ArgumentOutOfRangeException>(() => ZabbixPoll.Every(TimeSpan.FromSeconds(1), _ => new ValueTask<IEnumerable<int>>([1]), TimeProvider.System, NullLogger.Instance, -1));

    /// <summary>A transport whose listener binds, never accepts, and fails its unbind once the disposal is under way.</summary>
    private sealed class FailingUnbindTransport : IConnectionListenerFactory
    {
#pragma warning disable CA2000 // the listener under test owns what it binds
        public ValueTask<IConnectionListener> BindAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IConnectionListener>(new Listener(endpoint));
#pragma warning restore CA2000

        private sealed class Listener(EndPoint endPoint) : IConnectionListener
        {
            public EndPoint EndPoint { get; } = endPoint;

            public ValueTask<ConnectionContext?> AcceptAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<ConnectionContext?>(null);

            public ValueTask UnbindAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

            public ValueTask DisposeAsync() => new(FailLaterAsync());

            private static async Task FailLaterAsync()
            {
                await Task.Yield();
                throw new IOException("the socket would not close");
            }
        }
    }
}
