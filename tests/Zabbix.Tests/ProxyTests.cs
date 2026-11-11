// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.Protocol;
using Community.Zabbix.Proxy;
using Community.Zabbix.Storage.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The active proxy end to end against <see cref="FakeZabbixServer"/>, a server shaped on its source: the configuration
/// pulled and merged, values trapped by Zabbix's admission rules, history uploaded by item id with the store's
/// sequences as ids, held while the server has uploads disabled, re-sent with the same ids until acknowledged.
/// </summary>
public sealed class ProxyTests
{
    private const string Hosts = "[10,\"web-01\",0]";
    private const string Items = "[100,2,10,\"cpu\",0,\"\",0]";

    private sealed record Running(ServiceProvider Provider, ZabbixActiveProxy Proxy, FakeLogCollector Logs) : IAsyncDisposable
    {
        public ZabbixSender Sender()
        {
            var bound = (IPEndPoint)Proxy.Trapper!.LocalEndPoint!;
            return new ZabbixSender(bound.Address.ToString(), bound.Port);
        }

        public async ValueTask DisposeAsync()
        {
            var hosted = (IHostedLifecycleService)Provider.GetServices<IHostedService>().Single();
            await hosted.StoppingAsync(default);
            await hosted.StopAsync(default);
            await hosted.StoppedAsync(default);
            await Provider.DisposeAsync();
        }
    }

    private static async Task<Running> StartAsync(ZabbixServerNode server, Action<ZabbixProxyOptions>? configure = null, Action<IZabbixActiveProxyBuilder>? compose = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.AddFakeLogging().SetMinimumLevel(LogLevel.Debug));
        services.AddMetrics();
        IZabbixActiveProxyBuilder proxy = services.AddZabbixActiveProxy("proxy-01")
            .WithServer(server.ToDnsEndPoint())
            .AddTrapper(0, o => o.Address = "127.0.0.1")
            .AllowPeer(IPAddress.Loopback)
            .Configure(o => { o.DataInterval = TimeSpan.FromMilliseconds(20); o.ConfigInterval = TimeSpan.FromMinutes(1); configure?.Invoke(o); });
        compose?.Invoke(proxy);
        ServiceProvider provider = services.BuildValidated();
        var hosted = (IHostedLifecycleService)provider.GetServices<IHostedService>().Single();
        await hosted.StartingAsync(default);
        await hosted.StartAsync(default);
        return new Running(provider, provider.GetRequiredKeyedService<ZabbixActiveProxy>("proxy-01"), provider.GetFakeLogCollector());
    }

    private static async Task<FakeZabbixServer> ServerAsync(string version = "7.4.15")
    {
        var server = new FakeZabbixServer(version);
        server.Configure(Hosts, Items);
        await server.StartAsync();
        return server;
    }

    private static async Task<JsonElement> NextPageAsync(FakeZabbixServer server, string? notFrom = null)
    {
        while (true)
        {
            JsonElement frame = await server.NextAsync("proxy data");
            if (frame.TryGetProperty("history data", out _) && frame.GetProperty("session").GetString() != notFrom) return frame;
        }
    }

    [Fact]
    public async Task Values_trapped_for_a_configured_item_are_uploaded_by_item_id_and_an_unknown_host_is_refused_at_the_door()
    {
        await using FakeZabbixServer server = await ServerAsync();
        await using Running proxy = await StartAsync(server.Node);
        Assert.Single(proxy.Proxy.Config.Items);                                              // pulled before the trapper opened

        using ZabbixSender sender = proxy.Sender();
        ZabbixSenderResponse response = await sender.SendAsync([new ZabbixValue("web-01", "cpu", "0.5", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)), new ZabbixValue("nobody", "cpu", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);
        JsonElement page = await NextPageAsync(server);

        Assert.Equal((1, 1), (response.Processed, response.Failed));
        Assert.Equal("proxy-01", page.GetProperty("host").GetString());
        JsonElement item = Assert.Single(page.GetProperty("history data").EnumerateArray());
        Assert.Equal(100, item.GetProperty("itemid").GetInt64());
        Assert.Equal("0.5", item.GetProperty("value").GetString());
        Assert.Equal(1_700_000_000, item.GetProperty("clock").GetInt64());
        Assert.Contains(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7020 && record.Level == LogLevel.Warning && record.Message.Contains("nobody", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_uplink_s_exchanges_report_on_the_host_s_wire_telemetry()
    {
        await using FakeZabbixServer server = await ServerAsync();
        await using Running proxy = await StartAsync(server.Node);
        using var exchanges = new MetricCollector<double>(proxy.Provider.GetRequiredService<IMeterFactory>(), ZabbixTelemetryNames.Wire, ZabbixWireTelemetry.DurationInstrument);

        proxy.Proxy.DiscoveryStore!.TryWrite(new ZabbixProxyDiscoveryItem(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), NetworkDiscoveryRuleId: 5, NetworkDiscoveryCheckId: 6, Ip: "10.0.0.1", Dns: null, Port: 22, Value: "ssh", Status: 0));
        await exchanges.WaitForMeasurementsAsync(1, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);   // the upload it causes

        Assert.All(exchanges.GetMeasurementSnapshot(), sample => Assert.True(sample.Value >= 0));
    }

    [Fact]
    public async Task Uploaded_records_are_counted_by_the_proxy_data_kind_they_travel_as()
    {
        await using FakeZabbixServer server = await ServerAsync();
        await using Running proxy = await StartAsync(server.Node);
        using var records = new MetricCollector<long>(proxy.Provider.GetRequiredService<IMeterFactory>(), ZabbixTelemetryNames.Proxy, ZabbixProxyTelemetry.ValuesInstrument);

        proxy.Proxy.DiscoveryStore!.TryWrite(new ZabbixProxyDiscoveryItem(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), NetworkDiscoveryRuleId: 5, NetworkDiscoveryCheckId: 6, Ip: "10.0.0.1", Dns: null, Port: 22, Value: "ssh", Status: 0));
        using ZabbixSender sender = proxy.Sender();
        await sender.SendAsync([new ZabbixValue("web-01", "cpu", "0.5", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000))], TestContext.Current.CancellationToken);
        while (true)
        {
            int seen = records.GetMeasurementSnapshot().Count;                                  // read before the check (after it, a
            if (Counted(ZabbixProxyTelemetry.Discovery) && Counted(ZabbixProxyTelemetry.History)) break;   // sample landing between the two was never waited for)
            await records.WaitForMeasurementsAsync(seen + 1, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }

        Assert.All(records.GetMeasurementSnapshot(), sample => Assert.NotNull(sample.Tags[ZabbixProxyTelemetry.KindTag]));

        bool Counted(string kind) => records.GetMeasurementSnapshot().Any(sample =>
            (string?)sample.Tags[ZabbixProxyTelemetry.KindTag] == kind && (string?)sample.Tags["zabbix.proxy.outcome"] == ZabbixProxyTelemetry.Uploaded && sample.Value == 1);
    }

    [Fact]
    public async Task Discovery_and_autoregistration_entries_written_to_their_stores_are_uploaded_under_their_own_wire_keys()
    {
        await using FakeZabbixServer server = await ServerAsync();
        await using Running proxy = await StartAsync(server.Node);

        proxy.Proxy.DiscoveryStore!.TryWrite(new ZabbixProxyDiscoveryItem(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), NetworkDiscoveryRuleId: 5, NetworkDiscoveryCheckId: 6, Ip: "10.0.0.1", Dns: null, Port: 22, Value: "ssh", Status: 0));
        proxy.Proxy.AutoregistrationStore!.TryWrite(new ZabbixProxyAutoregistrationItem(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), "new-host", Ip: "10.0.0.2", Dns: null, Port: 10050, HostMetadata: "linux", Flags: 0, TlsAccepted: 1));

        JsonElement page = await FrameAsync(server, "discovery data");

        Assert.Equal(5, page.GetProperty("discovery data")[0].GetProperty("drule").GetInt64());
        Assert.Equal("new-host", page.GetProperty("auto registration")[0].GetProperty("host").GetString());
        Assert.Single(server.StoredDiscovery);
        Assert.Single(server.StoredAutoregistration);
    }

    private static async Task<JsonElement> FrameAsync(FakeZabbixServer server, string key)
    {
        while (true)
        {
            JsonElement frame = await server.NextAsync("proxy data");
            if (frame.TryGetProperty(key, out _)) return frame;
        }
    }

    /// <summary>
    /// The typed write is the row protocol the server reads (<c>proxy.c</c> <c>process_services</c>): the address's
    /// service rows, then the row with <c>dcheck</c> 0 that closes the host — without it the server parks the services in
    /// <c>proxy_dhistory</c> and waits. The closing row is upstream's <c>zbx_pb_discovery_write_host</c>: port 0, value
    /// "", the host up when any service is.
    /// </summary>
    [Fact]
    public async Task A_discovered_host_is_uploaded_as_its_service_rows_then_the_row_that_closes_it()
    {
        await using FakeZabbixServer server = await ServerAsync();
        await using Running proxy = await StartAsync(server.Node);
        var found = new ZabbixProxyDiscoveredHost(NetworkDiscoveryRuleId: 5, Ip: "10.0.0.1", Clock: DateTimeOffset.FromUnixTimeSeconds(1_700_000_000),
            Services: [new ZabbixProxyDiscoveredService(NetworkDiscoveryCheckId: 6, Port: 22, IsUp: true, Value: "ssh"), new ZabbixProxyDiscoveredService(NetworkDiscoveryCheckId: 7, Port: 80, IsUp: false)],
            Dns: "web-01.example");

        await proxy.Proxy.DiscoveryStore!.WriteAsync(found, TestContext.Current.CancellationToken);
        var rows = new List<JsonElement>();
        while (!rows.Any(row => row.GetProperty("dcheck").GetInt64() == 0))
            rows.AddRange((await FrameAsync(server, "discovery data")).GetProperty("discovery data").EnumerateArray());

        Assert.Equal([6L, 7L, 0L], rows.Select(row => row.GetProperty("dcheck").GetInt64()));
        JsonElement closing = rows[^1];
        Assert.Equal((5L, "10.0.0.1", "web-01.example", 0, "", 0, 1_700_000_000L),
            (closing.GetProperty("drule").GetInt64(), closing.GetProperty("ip").GetString(), closing.GetProperty("dns").GetString(), closing.GetProperty("port").GetInt32(),
             closing.GetProperty("value").GetString(), closing.GetProperty("status").GetInt32(), closing.GetProperty("clock").GetInt64()));
        Assert.Equal(1, rows[1].GetProperty("status").GetInt32());                               // the service that is down
    }

    /// <summary>
    /// Every server refuses a whole <c>proxy data</c> message with a discovery row it cannot read, after storing the
    /// message's history; a proxy that keeps such a page re-sends it for ever and, once its history page is full, uploads
    /// no later value. The page the server calls invalid (it takes the proxy's history on its own) is dropped, logged
    /// (7040, with the server's words) and counted; the history behind it flows. Bounded by frames, not time.
    /// </summary>
    [Fact]
    public async Task A_discovery_page_the_server_cannot_read_is_dropped_and_counted_and_the_history_behind_it_still_flows()
    {
        await using FakeZabbixServer server = await ServerAsync();
        await using Running proxy = await StartAsync(server.Node, o => o.PageSize = 1);
        using var records = new MetricCollector<long>(proxy.Provider.GetRequiredService<IMeterFactory>(), ZabbixTelemetryNames.Proxy, ZabbixProxyTelemetry.ValuesInstrument);

        // With NetworkDiscoveryCheckId a long, only null! still writes a row the server cannot read: here, one without its ip.
        proxy.Proxy.DiscoveryStore!.TryWrite(new ZabbixProxyDiscoveryItem(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), NetworkDiscoveryRuleId: 5, NetworkDiscoveryCheckId: 6, Ip: null!, Dns: null, Port: 22, Value: "ssh", Status: 0));
        using ZabbixSender sender = proxy.Sender();
        await sender.SendAsync([new ZabbixValue("web-01", "cpu", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);
        using ZabbixSender second = proxy.Sender();
        await second.SendAsync([new ZabbixValue("web-01", "cpu", "2", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);
        for (int frames = 0; server.Stored.Count < 2 && frames < 200; frames++) await server.NextAsync("proxy data");

        Assert.Equal(["1", "2"], server.Stored.Select(value => value.Value));
        Assert.Equal(0, proxy.Proxy.DiscoveryStore.Count);
        Assert.Empty(server.StoredDiscovery);
        FakeLogRecord dropped = Assert.Single(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7040);
        Assert.Equal(LogLevel.Error, dropped.Level);
        // The server's words echo item keys and names: classified peer text, erased from the log by the fallback redactor.
        Assert.DoesNotContain("cannot find pair with name \"ip\"", dropped.Message, StringComparison.Ordinal);
        Assert.Equal(1, records.GetMeasurementSnapshot().Where(m => (string?)m.Tags["zabbix.proxy.outcome"] == ZabbixProxyTelemetry.Invalid && (string?)m.Tags[ZabbixProxyTelemetry.KindTag] == ZabbixProxyTelemetry.Discovery).Sum(m => m.Value));
    }

    /// <summary>
    /// The other half of the refused-page rule: a refusal the server gives the proxy itself (its probe is refused too: unknown, not
    /// allowed, shutting down) says nothing about the page, so every kind is kept and sent again, as upstream's data sender
    /// does; nothing is dropped.
    /// </summary>
    [Fact]
    public async Task A_refusal_of_the_proxy_itself_keeps_every_page_for_the_next_turn()
    {
        await using FakeZabbixServer server = await ServerAsync();
        server.RefuseProxy = true;
        await using Running proxy = await StartAsync(server.Node);

        proxy.Proxy.DiscoveryStore!.TryWrite(new ZabbixProxyDiscoveryItem(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), NetworkDiscoveryRuleId: 5, NetworkDiscoveryCheckId: 6, Ip: "10.0.0.1", Dns: null, Port: 22, Value: "ssh", Status: 0));
        using ZabbixSender sender2 = proxy.Sender();
        await sender2.SendAsync([new ZabbixValue("web-01", "cpu", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);
        JsonElement both;
        do both = await server.NextAsync("proxy data");
        while (!both.TryGetProperty("history data", out _) || !both.TryGetProperty("discovery data", out _));
        await server.NextAsync("proxy data");                                                 // what the proxy sent after that refusal: refused too
        server.RefuseProxy = false;
        for (int frames = 0; (server.Stored.Count < 1 || server.StoredDiscovery.Count < 1) && frames < 200; frames++) await server.NextAsync("proxy data");

        Assert.Equal(["1"], server.Stored.Select(value => value.Value));
        Assert.Single(server.StoredDiscovery);
        Assert.DoesNotContain(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7040);
        Assert.Contains(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7011 && record.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Values_are_counted_by_outcome_uploaded_and_refused()
    {
        await using FakeZabbixServer server = await ServerAsync();
        await using Running proxy = await StartAsync(server.Node);
        using var values = new Microsoft.Extensions.Diagnostics.Metrics.Testing.MetricCollector<long>(
            proxy.Provider.GetRequiredService<System.Diagnostics.Metrics.IMeterFactory>(), ZabbixProxyTelemetry.Name, ZabbixProxyTelemetry.ValuesInstrument);

        using ZabbixSender sender2 = proxy.Sender();
        await sender2.SendAsync([new ZabbixValue("web-01", "cpu", "1", DateTimeOffset.UnixEpoch), new ZabbixValue("web-01", "nokey", "2", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);
        await values.WaitForMeasurementsAsync(2, TimeSpan.FromSeconds(30));

        Assert.Equal(1, values.GetMeasurementSnapshot().Where(m => Equals(m.Tags["zabbix.proxy.outcome"], ZabbixProxyTelemetry.Refused)).Sum(m => m.Value));
        Assert.Equal(1, values.GetMeasurementSnapshot().Where(m => Equals(m.Tags["zabbix.proxy.outcome"], ZabbixProxyTelemetry.Uploaded)).Sum(m => m.Value));
    }

    /// <summary>
    /// Upstream's proxy receives its configuration with <c>zbx_tcp_recv_large</c> (commshigh.c 8.0:169), so a server
    /// may send it as a large frame (flag <c>0x04</c>, 64-bit lengths, up to 16 GiB); the active pull must read one, as the
    /// passive path already does.
    /// </summary>
    [Fact]
    public async Task A_configuration_the_server_sends_as_a_large_frame_is_received()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        listener.Start();
        Task serving = ServeLargeAsync(listener, stop.Token);
        try
        {
            await using Running proxy = await StartAsync(new ZabbixServerNode("127.0.0.1", ((IPEndPoint)listener.LocalEndpoint).Port));

            Assert.Equal(100UL, Assert.Single(proxy.Proxy.Config.Items).Key);
            Assert.DoesNotContain(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7001);
        }
        finally
        {
            await stop.CancelAsync();
            await serving;
            listener.Dispose();
            stop.Dispose();
        }

        // A server that answers every request with a large frame (flags 0x05: protocol and large; 8-byte length and reserved).
        static async Task ServeLargeAsync(TcpListener listener, CancellationToken stopping)
        {
            const string Config = """{"full_sync":1,"data":{"hosts":{"fields":["hostid","host","status"],"data":[[10,"web-01",0]]},"items":{"fields":["itemid","type","hostid","key_","status","trapper_hosts","allow_traps"],"data":[[100,2,10,"cpu",0,"",0]]}},"config_revision":1}""";
            try
            {
                while (true)
                {
                    using TcpClient client = await listener.AcceptTcpClientAsync(stopping);
                    NetworkStream stream = client.GetStream();
                    ReadOnlyMemory<byte> request = await ZabbixHeader.ReadAsync(stream, stopping);
                    string? kind = JsonDocument.Parse(request).RootElement.GetProperty("request").GetString();
                    byte[] body = System.Text.Encoding.UTF8.GetBytes(kind == "proxy config" ? Config : """{"upload":"enabled","response":"success"}""");
                    byte[] frame = new byte[5 + 16 + body.Length];
                    "ZBXD"u8.CopyTo(frame);
                    frame[4] = 0x05;
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(frame.AsSpan(5), (ulong)body.Length);
                    body.CopyTo(frame.AsSpan(21));
                    await stream.WriteAsync(frame, stopping);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                // the test is done
            }
        }
    }

    [Fact]
    public async Task A_proxy_refused_for_its_version_announces_the_server_s_and_receives_the_configuration()
    {
        await using FakeZabbixServer server = await ServerAsync("7.0.31");
        await using Running proxy = await StartAsync(server.Node);                           // announces 7.4.0 first

        Assert.Equal(new Version(7, 0, 31), proxy.Proxy.Uplink!.Version);
        Assert.Single(proxy.Proxy.Config.Items);
        FakeLogRecord refused = Assert.Single(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7002);
        Assert.Equal(LogLevel.Warning, refused.Level);
        // The server's words echo item keys and names: classified peer text, erased from the log by the fallback redactor.
        Assert.DoesNotContain("proxy and server major versions do not match", refused.Message, StringComparison.Ordinal);
        Assert.Contains(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7003);
    }

    [Fact]
    public async Task Upload_disabled_holds_the_page_probes_without_history_and_loses_nothing()
    {
        await using FakeZabbixServer server = await ServerAsync();
        server.UploadDisabled = true;
        await using Running proxy = await StartAsync(server.Node);
        using ZabbixSender sender = proxy.Sender();
        await sender.SendAsync([new ZabbixValue("web-01", "cpu", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);

        JsonElement refused = await NextPageAsync(server);                                   // failed + upload disabled: held
        JsonElement probe = await server.NextAsync("proxy data");
        Assert.True(proxy.Proxy.IsHeld);
        server.UploadDisabled = false;
        JsonElement sent = await NextPageAsync(server);

        Assert.False(probe.TryGetProperty("history data", out _));                             // a held proxy asks without history
        Assert.Equal(refused.GetProperty("history data")[0].GetProperty("id").GetInt64(), sent.GetProperty("history data")[0].GetProperty("id").GetInt64());
        Assert.Equal(["1"], server.Stored.Select(value => value.Value));
        Assert.Contains(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7012 && record.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_page_the_server_refused_is_sent_again_with_the_same_ids_and_stored_once()
    {
        await using FakeZabbixServer server = await ServerAsync();
        server.RefusePages = 1;
        await using Running proxy = await StartAsync(server.Node);
        using ZabbixSender sender = proxy.Sender();
        await sender.SendAsync([new ZabbixValue("web-01", "cpu", "1", DateTimeOffset.UnixEpoch), new ZabbixValue("web-01", "cpu", "2", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);

        JsonElement first = await NextPageAsync(server);
        JsonElement again = await NextPageAsync(server);

        long[] Ids(JsonElement page) => [.. page.GetProperty("history data").EnumerateArray().Select(row => row.GetProperty("id").GetInt64())];
        // The refused page is kept with its ids and topped up (the upload loop may have leased it before the second value
        // arrived), so the re-sent page begins with exactly the refused one's ids.
        Assert.Equal(Ids(first), Ids(again).Take(Ids(first).Length));
        Assert.Equal(["1", "2"], server.Stored.Select(value => value.Value));
        Assert.Contains(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7011 && record.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_page_whose_exchange_failed_is_kept_and_sent_again()
    {
        await using FakeZabbixServer server = await ServerAsync();
        server.DropPages = 1;
        await using Running proxy = await StartAsync(server.Node);
        using ZabbixSender sender = proxy.Sender();
        await sender.SendAsync([new ZabbixValue("web-01", "cpu", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);

        await NextPageAsync(server);                                                          // cut with no reply
        await NextPageAsync(server);

        Assert.Equal(["1"], server.Stored.Select(value => value.Value));
        Assert.Contains(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7016 && record.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task A_restarted_proxy_on_a_durable_store_sends_the_unacknowledged_page_again_with_its_ids_and_continues_above_them()
    {
        string file = Path.Combine(Path.GetTempPath(), $"zbx-proxy-{Guid.NewGuid():N}.db");
        Action<IZabbixActiveProxyBuilder> durable = proxy => proxy.UseEntityFrameworkStore(o => o.UseSqlite($"Data Source={file};Pooling=False"));
        try
        {
            await using FakeZabbixServer server = await ServerAsync();
            long inFlight;
            string crashed;
            server.RefusePages = int.MaxValue;                                               // nothing is acknowledged before the "crash"
            await using (Running before = await StartAsync(server.Node, o => o.DrainTimeout = TimeSpan.Zero, durable))
            {
                using ZabbixSender sender = before.Sender();
                await sender.SendAsync([new ZabbixValue("web-01", "cpu", "1", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);
                JsonElement page = await NextPageAsync(server);
                inFlight = page.GetProperty("history data")[0].GetProperty("id").GetInt64();
                // Its last frame can still be in the server's hands when it stops, and must not pass for the restarted proxy's.
                crashed = page.GetProperty("session").GetString()!;
                server.RefusedSession = crashed;
            }

            server.RefusePages = 0;
            server.Drain();
            await using Running after = await StartAsync(server.Node, compose: durable);
            JsonElement resent = await NextPageAsync(server, notFrom: crashed);
            using ZabbixSender resumed = after.Sender();
            await resumed.SendAsync([new ZabbixValue("web-01", "cpu", "2", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);
            JsonElement next = await NextPageAsync(server, notFrom: crashed);

            Assert.Equal(inFlight, resent.GetProperty("history data")[0].GetProperty("id").GetInt64());
            Assert.True(next.GetProperty("history data")[0].GetProperty("id").GetInt64() > inFlight);
            Assert.Equal(["1", "2"], server.Stored.Select(value => value.Value));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    [Fact]
    public async Task An_unreachable_server_leaves_the_held_configuration_and_logs_a_warning()
    {
        using var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        int port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();                                                                        // nothing listens there now

        await using Running proxy = await StartAsync(new ZabbixServerNode("127.0.0.1", port), o => o.Connection.Timeout = TimeSpan.FromSeconds(5));

        Assert.Equal(0, proxy.Proxy.Config.ConfigRevision);
        Assert.Contains(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7001 && record.Level == LogLevel.Warning);
    }

    [Fact]
    public void An_invalid_version_or_offline_buffer_fails_at_start()
    {
        var services = new ServiceCollection();
        services.AddZabbixActiveProxy("proxy-01").WithServer("zbx").Configure(o => { o.Version = "latest"; o.OfflineBuffer = TimeSpan.FromMinutes(5); });
        using ServiceProvider provider = services.BuildValidated();

        var failure = Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<ZabbixProxyOptions>>().Get("proxy-01"));

        Assert.Contains(failure.Failures, message => message.Contains("Version 'latest'", StringComparison.Ordinal));
        Assert.Contains(failure.Failures, message => message.Contains("OfflineBuffer", StringComparison.Ordinal));
    }

    // ----- agent data: the host at the frame, rows by item id, the session's ids de-duplicated (active.c, proxy.c) ----

    private const string AgentItemFields = "\"itemid\",\"type\",\"hostid\",\"key_\",\"status\",\"trapper_hosts\",\"allow_traps\",\"delay\"";

    // web-01 holds a trapper item (100) and an active agent item (101); web-02 is not monitored and holds one (102).
    private static async Task<FakeZabbixServer> AgentServerAsync()
    {
        var server = new FakeZabbixServer { ItemFields = AgentItemFields };
        server.Configure("[10,\"web-01\",0],[11,\"web-02\",1]", "[100,2,10,\"cpu\",0,\"\",0,\"0\"],[101,7,10,\"agent.answer\",0,\"\",0,\"1s\"],[102,7,11,\"agent.off\",0,\"\",0,\"1s\"]");
        await server.StartAsync();
        return server;
    }

    private static readonly string AgentSession = new('a', 32);

    private static string AgentData(string rows, string? session = null, string host = "web-01")
        => $$"""{"request":"agent data","session":"{{session ?? AgentSession}}","host":"{{host}}","version":"7.4.15","variant":1,"data":[{{rows}}]}""";

    /// <summary>One frame to the proxy's trapper and its reply, as an agent exchanges it.</summary>
    private static async Task<JsonElement> ExchangeAsync(Running proxy, string json)
    {
        var bound = (IPEndPoint)proxy.Proxy.Trapper!.LocalEndPoint!;
        using var client = new TcpClient();
        await client.ConnectAsync(bound.Address, bound.Port, TestContext.Current.CancellationToken);
        await using NetworkStream stream = client.GetStream();
        await ZabbixHeader.WriteAsync(stream, Encoding.UTF8.GetBytes(json), TestContext.Current.CancellationToken);
        return JsonDocument.Parse(await ZabbixHeader.ReadAsync(stream, TestContext.Current.CancellationToken)).RootElement.Clone();
    }

    private static string Info(JsonElement reply) => reply.GetProperty("info").GetString()!;

    private static string Counts(JsonElement reply) => Info(reply)[..Info(reply).IndexOf("; seconds", StringComparison.Ordinal)];

    /// <summary>The uploaded history rows until <paramref name="count"/> of <paramref name="itemId"/> have arrived.</summary>
    private static async Task<List<JsonElement>> UploadedAsync(FakeZabbixServer server, long itemId, int count)
    {
        var rows = new List<JsonElement>();
        while (rows.Count < count)
        {
            JsonElement page = await NextPageAsync(server).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            rows.AddRange(page.GetProperty("history data").EnumerateArray().Where(row => row.GetProperty("itemid").GetInt64() == itemId));
        }
        return rows;
    }

    [Fact]
    public async Task The_sdk_active_agent_s_values_reach_the_proxy_s_store_and_its_upload_by_item_id()
    {
        await using FakeZabbixServer server = await AgentServerAsync();
        await using Running proxy = await StartAsync(server.Node);
        var bound = (IPEndPoint)proxy.Proxy.Trapper!.LocalEndPoint!;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMetrics();
        services.AddZabbixActiveAgent("edge").AddServer(new DnsEndPoint(bound.Address.ToString(), bound.Port)).AddItem("agent.answer", _ => "44")
            .Configure(o => { o.HostName = "web-01"; o.RefreshInterval = TimeSpan.FromMinutes(1); o.HeartbeatInterval = TimeSpan.Zero; o.Send.FlushInterval = TimeSpan.FromMilliseconds(50); });
        await using ServiceProvider agent = services.BuildValidated();
        var hosted = (IHostedLifecycleService)agent.GetServices<IHostedService>().Single();
        await hosted.StartingAsync(TestContext.Current.CancellationToken);
        await hosted.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            JsonElement row = (await UploadedAsync(server, itemId: 101, count: 1))[0];

            Assert.Equal("44", row.GetProperty("value").GetString());
            Assert.False(row.TryGetProperty("state", out _));
            Assert.Contains(server.Stored, stored => stored.ItemId == 101 && stored.Value == "44");
            Assert.DoesNotContain(proxy.Logs.GetSnapshot(), record => record.Id.Id == 7020);  // nothing the agent sent was refused
        }
        finally
        {
            await hosted.StoppingAsync(TestContext.Current.CancellationToken);
            await hosted.StopAsync(TestContext.Current.CancellationToken);
            await hosted.StoppedAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task A_not_supported_agent_value_is_uploaded_with_its_state_and_its_reason_as_the_value()
    {
        await using FakeZabbixServer server = await AgentServerAsync();
        await using Running proxy = await StartAsync(server.Node);

        JsonElement reply = await ExchangeAsync(proxy, AgentData("""{"id":1,"itemid":101,"value":"Unsupported item key.","clock":1700000000,"ns":5,"state":1}"""));
        JsonElement row = (await UploadedAsync(server, itemId: 101, count: 1))[0];

        Assert.Equal(("success", "processed: 1; failed: 0; total: 1"), (reply.GetProperty("response").GetString(), Counts(reply)));
        Assert.Equal(1, row.GetProperty("state").GetInt32());
        Assert.Equal("Unsupported item key.", row.GetProperty("value").GetString());
        Assert.Equal((1_700_000_000L, 5), (row.GetProperty("clock").GetInt64(), row.GetProperty("ns").GetInt32()));
    }

    [Fact]
    public async Task Agent_data_ids_the_session_already_took_are_answered_failed_and_stored_once()
    {
        await using FakeZabbixServer server = await AgentServerAsync();
        await using Running proxy = await StartAsync(server.Node);
        static string Row(int id) => $$"""{"id":{{id}},"itemid":101,"value":"v{{id}}","clock":1700000000,"ns":{{id}}}""";

        JsonElement first = await ExchangeAsync(proxy, AgentData($"{Row(1)},{Row(2)}"));
        JsonElement resent = await ExchangeAsync(proxy, AgentData($"{Row(1)},{Row(2)},{Row(3)}"));        // an upload whose reply was lost, and one more
        JsonElement restarted = await ExchangeAsync(proxy, AgentData(Row(1), session: new string('b', 32)));   // a restarted agent: a new session, ids from 1
        List<JsonElement> rows = await UploadedAsync(server, itemId: 101, count: 4);

        Assert.Equal("processed: 2; failed: 0; total: 2", Counts(first));
        Assert.Equal("processed: 1; failed: 2; total: 3", Counts(resent));
        Assert.Equal("processed: 1; failed: 0; total: 1", Counts(restarted));
        Assert.Equal(["v1", "v2", "v3", "v1"], rows.Select(row => row.GetProperty("value").GetString()));
    }

    [Fact]
    public async Task Agent_data_whose_last_id_is_behind_the_session_s_is_refused_and_logged_as_upstream_logs_it()
    {
        await using FakeZabbixServer server = await AgentServerAsync();
        await using Running proxy = await StartAsync(server.Node);

        await ExchangeAsync(proxy, AgentData("""{"id":5,"itemid":101,"value":"v5"}"""));
        JsonElement behind = await ExchangeAsync(proxy, AgentData("""{"id":3,"itemid":101,"value":"v3"}"""));

        // active.c process_agent_history_data: "received id … is less than last id" is a network anomaly, logged; the row is a duplicate.
        Assert.Equal("processed: 0; failed: 1; total: 1", Counts(behind));
        Assert.Contains(proxy.Logs.GetSnapshot(), record => record.Id.Id == 6103);
    }

    [Fact]
    public async Task Agent_data_is_answered_at_the_frame_as_upstream_answers_it()
    {
        await using FakeZabbixServer server = await AgentServerAsync();
        await using Running proxy = await StartAsync(server.Node);

        JsonElement unknown = await ExchangeAsync(proxy, AgentData("""{"id":1,"itemid":101,"value":"1"}""", host: "nobody"));
        JsonElement shortSession = await ExchangeAsync(proxy, AgentData("""{"id":1,"itemid":101,"value":"1"}""", session: "abc"));
        JsonElement hostless = await ExchangeAsync(proxy, $$"""{"request":"agent data","session":"{{AgentSession}}","version":"7.4.15","data":[{"id":1,"itemid":101,"value":"1"}]}""");
        JsonElement mixed = await ExchangeAsync(proxy, AgentData("""{"id":1,"itemid":100,"value":"1"},{"id":2,"itemid":999,"value":"1"},{"id":3,"itemid":102,"value":"1"},{"id":4,"value":"1"},{"id":5,"itemid":101,"value":"ok"}"""));

        Assert.Equal(("success", "unknown host 'nobody'"), (unknown.GetProperty("response").GetString(), Info(unknown)));     // success: the agent does not retry
        Assert.Equal(("failed", "invalid session token length 3"), (shortSession.GetProperty("response").GetString(), Info(shortSession)));
        Assert.Equal(("failed", "cannot find \"host\" token"), (hostless.GetProperty("response").GetString(), Info(hostless)));
        // A trapper item, an unknown item, an item of a host not monitored, a row without an item id: failed; the rest processed.
        Assert.Equal(("success", "processed: 1; failed: 4; total: 5"), (mixed.GetProperty("response").GetString(), Counts(mixed)));
        Assert.Equal("ok", (await UploadedAsync(server, itemId: 101, count: 1))[0].GetProperty("value").GetString());
    }

    [Fact]
    public async Task Pre_4_4_agent_data_by_host_and_key_is_still_taken_by_an_active_item()
    {
        await using FakeZabbixServer server = await AgentServerAsync();
        await using Running proxy = await StartAsync(server.Node);

        JsonElement reply = await ExchangeAsync(proxy, """{"request":"agent data","data":[{"host":"web-01","key":"agent.answer","value":"7","clock":1700000000,"ns":0},{"host":"web-01","key":"cpu","value":"8"}]}""");

        Assert.Equal("processed: 1; failed: 1; total: 2", Counts(reply));
        Assert.Equal("7", (await UploadedAsync(server, itemId: 101, count: 1))[0].GetProperty("value").GetString());
    }
}
