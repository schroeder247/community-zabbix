// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Community.Zabbix.Listening;
using Community.Zabbix.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Tests;

/// <summary>The trapper: <c>sender data</c> into a writer, answered as the server answers; counted by instruments; hosted as an agent's one-reader source.</summary>
public sealed class TrapperTests
{
    private static readonly byte[] TwoValues = """{"request":"sender data","data":[{"host":"web01","key":"a","value":"1"},{"host":"web01","key":"b","value":"2"}]}"""u8.ToArray();

    [Fact]
    public async Task The_trapper_takes_sender_data_into_its_edge_and_answers_as_the_server_does()
    {
        var options = new ZabbixListenerOptions { Address = "127.0.0.1", Port = 0 };
        options.AllowedPeers.Add("127.0.0.1");
        await using var source = new ZabbixTrapperSource(options, ListenerTests.Transport());
        await source.StartingAsync(TestContext.Current.CancellationToken);
        await source.StartAsync(TestContext.Current.CancellationToken);
        var bound = (IPEndPoint)source.Listener.LocalEndPoint!;
        using var sender = new ZabbixSender(bound.Address.ToString(), bound.Port);

        ZabbixSenderResponse response = await sender.SendAsync([new ZabbixValue("web01", "cpu", "0.5", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000)), new ZabbixValue("web01", "mem", "42", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);
        List<ZabbixValue> got = await source.ReadAllAsync(TestContext.Current.CancellationToken).Take(2).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, response.Processed);
        Assert.Equal(0, response.Failed);
        Assert.Equal(["cpu", "mem"], got.Select(value => value.Key.ToString()));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), got[0].Clock);
        Assert.Equal(2, source.Trapper.Received);
        await source.StoppingAsync(TestContext.Current.CancellationToken);
        await source.StopAsync(TestContext.Current.CancellationToken);
        await source.StoppedAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task An_agent_with_a_trapper_source_is_a_sender_relay()
    {
        var (port, received) = MockTrapper.Collect(2);
        var services = new ServiceCollection();
        services.AddZabbixPushAgent("relay").AddTrapper(0, o => { o.Address = "127.0.0.1"; o.AllowedPeers.Add("127.0.0.1"); }).AddServer(new DnsEndPoint("127.0.0.1", port)).Configure(o => o.Send.BatchSize = 2);
        using ServiceProvider provider = services.BuildValidated();
        IHostedLifecycleService[] hosted = [.. provider.GetServices<IHostedService>().Cast<IHostedLifecycleService>()];
        foreach (IHostedLifecycleService service in hosted) await service.StartingAsync(TestContext.Current.CancellationToken);
        foreach (IHostedLifecycleService service in hosted) await service.StartAsync(TestContext.Current.CancellationToken);
        var bound = (IPEndPoint)provider.GetRequiredKeyedService<ZabbixTrapperSource>(new ZabbixTrapperKey("relay")).Listener.LocalEndPoint!;

        using ZabbixSender sender2 = new ZabbixSender(bound.Address.ToString(), bound.Port);
        ZabbixSenderResponse response = await sender2.SendAsync([new ZabbixValue("web01", "a", "1", DateTimeOffset.UnixEpoch), new ZabbixValue("web01", "b", "2", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);
        int relayed = await received;

        Assert.Equal(2, response.Processed);
        Assert.Equal(2, relayed);
        foreach (IHostedLifecycleService service in hosted.Reverse()) await service.StoppingAsync(TestContext.Current.CancellationToken);
        foreach (IHostedLifecycleService service in hosted.Reverse()) await service.StopAsync(TestContext.Current.CancellationToken);
        foreach (IHostedLifecycleService service in hosted.Reverse()) await service.StoppedAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task The_trapper_counts_values_by_outcome_on_its_instrument()
    {
        using ServiceProvider metrics = new ServiceCollection().AddMetrics().BuildServiceProvider();
        IMeterFactory meters = metrics.GetRequiredService<IMeterFactory>();
        using var values = new MetricCollector<long>(meters, ZabbixListenerTelemetry.Name, ZabbixTrapper.ValuesInstrument);
        Channel<ZabbixValue> edge = Channel.CreateBounded<ZabbixValue>(1);
        var trapper = new ZabbixTrapper(edge.Writer, loggers: null, time: null, meters);

        await trapper.Handler(new ZabbixRequest(TwoValues, null), TestContext.Current.CancellationToken);                   // room for one: one processed, one failed

        Assert.Equal(1, values.GetMeasurementSnapshot().Single(m => (string?)m.Tags[ZabbixTrapper.OutcomeTag] == "processed").Value);
        Assert.Equal(1, values.GetMeasurementSnapshot().Single(m => (string?)m.Tags[ZabbixTrapper.OutcomeTag] == "failed").Value);
        Assert.Equal((1, 1), (trapper.Received, trapper.Failed));
    }

    private static readonly byte[] ThreeValues = """{"request":"sender data","data":[{"host":"web01","key":"a","value":"1"},{"host":"web01","key":"b","value":"2"},{"host":"web01","key":"c","value":"3"}]}"""u8.ToArray();

    [Fact]
    public async Task A_full_trapper_edge_answers_the_values_it_could_not_take_as_failed()
    {
        var options = new ZabbixListenerOptions();
        options.AllowedPeers.Add("127.0.0.1");
        await using var source = new ZabbixTrapperSource(options, maxItems: 1, ListenerTests.Transport(), loggers: null, time: null, meters: null);

        await source.Trapper.Handler(new ZabbixRequest(ThreeValues, null), TestContext.Current.CancellationToken);

        Assert.Equal((1, 2), (source.Trapper.Received, source.Trapper.Failed));          // one taken; the two the full edge could not take are failed
    }

    [Fact]
    public async Task A_store_that_sheds_the_incoming_value_has_the_trapper_answer_it_failed()
    {
        await using var store = new ZabbixMemoryStore<ZabbixValue>(new ZabbixStorePolicy<ZabbixValue> { MaxItems = 1, Overflow = BoundedChannelFullMode.DropWrite });
        var trapper = new ZabbixTrapper(store);

        await trapper.Handler(new ZabbixRequest(ThreeValues, null), TestContext.Current.CancellationToken);

        Assert.Equal((1, 2), (trapper.Received, trapper.Failed));
        Assert.Equal(1, store.Count);
    }

    /// <summary>
    /// The limitation docs/KNOWN-LIMITATIONS.md records: a <see cref="ChannelWriter{T}"/> does not say its full mode, so
    /// the trapper cannot refuse a drop-mode channel, and that channel's <c>TryWrite</c> answers <see langword="true"/>
    /// for the value it drops. A channel in <see cref="BoundedChannelFullMode.Wait"/> mode, or a store, answers the truth.
    /// </summary>
    [Fact]
    public async Task A_drop_mode_channel_reports_a_shed_value_as_processed_and_a_wait_mode_one_as_failed()
    {
        Channel<ZabbixValue> dropping = Channel.CreateBounded<ZabbixValue>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        Channel<ZabbixValue> waiting = Channel.CreateBounded<ZabbixValue>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.Wait });
        var overDropping = new ZabbixTrapper(dropping.Writer);
        var overWaiting = new ZabbixTrapper(waiting.Writer);

        await overDropping.Handler(new ZabbixRequest(ThreeValues, null), TestContext.Current.CancellationToken);
        await overWaiting.Handler(new ZabbixRequest(ThreeValues, null), TestContext.Current.CancellationToken);

        Assert.Equal((3, 0), (overDropping.Received, overDropping.Failed));             // two of the three were dropped
        Assert.Equal(1, dropping.Reader.Count);
        Assert.Equal((1, 2), (overWaiting.Received, overWaiting.Failed));
    }

    [Fact]
    public async Task Received_and_failed_totals_are_exact_under_concurrent_frames()
    {
        Channel<ZabbixValue> edge = Channel.CreateUnbounded<ZabbixValue>();
        var trapper = new ZabbixTrapper(edge.Writer);

        await Parallel.ForAsync(0, 5_000, async (_, token) => await trapper.Handler(new ZabbixRequest(TwoValues, null), token));

        Assert.Equal(10_000, trapper.Received);
    }

    [Fact]
    public async Task A_frame_that_is_not_sender_data_is_answered_failed_and_logged()
    {
        var logs = new FakeLogCollector();
        using ILoggerFactory loggers = LoggerFactory.Create(logging => logging.AddProvider(new FakeLoggerProvider(logs)));
        var trapper = new ZabbixTrapper(Channel.CreateUnbounded<ZabbixValue>().Writer, loggers, time: null, meters: null);

        ReadOnlyMemory<byte> reply = await trapper.Handler(new ZabbixRequest("""{"request":"proxy data"}"""u8.ToArray(), null), TestContext.Current.CancellationToken);

        using JsonDocument answer = JsonDocument.Parse(reply);
        Assert.Equal("failed", answer.RootElement.GetProperty("response").GetString());
        FakeLogRecord rejected = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 6101);
        Assert.Equal(LogLevel.Warning, rejected.Level);
    }

    /// <summary>A writer whose backend throws on every write after the first (a disk that fills mid-frame).</summary>
    private sealed class FullDiskWriter : ChannelWriter<ZabbixValue>
    {
        public int Kept { get; private set; }

        public override bool TryWrite(ZabbixValue item)
        {
            if (Kept > 0) throw new IOException("No space left on device");
            Kept++;
            return true;
        }

        public override ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(true);
    }

    [Fact]
    public async Task A_writer_that_throws_mid_frame_answers_the_kept_values_processed_and_logs_the_fault_once()
    {
        // The fault does not escape: the listener answering "failed" for the whole frame would make the sender re-send
        // the value that was kept.
        var logs = new FakeLogCollector();
        using ILoggerFactory loggers = LoggerFactory.Create(logging => logging.AddProvider(new FakeLoggerProvider(logs)));
        var trapper = new ZabbixTrapper(new FullDiskWriter(), loggers, time: null, meters: null);

        ReadOnlyMemory<byte> reply = await trapper.Handler(new ZabbixRequest(TwoValues, null), TestContext.Current.CancellationToken);

        using JsonDocument answer = JsonDocument.Parse(reply);
        Assert.StartsWith("processed: 1; failed: 1;", answer.RootElement.GetProperty("info").GetString(), StringComparison.Ordinal);
        FakeLogRecord faulted = Assert.Single(logs.GetSnapshot(), record => record.Id.Id == 6102);
        Assert.Equal(LogLevel.Error, faulted.Level);
        Assert.IsType<IOException>(faulted.Exception);
    }

    [Fact]
    public void A_trapper_source_s_edge_has_one_reader()
    {
        var options = new ZabbixListenerOptions();
        options.AllowedPeers.Add("127.0.0.1");
        using var source = new ZabbixTrapperSource(options, ListenerTests.Transport());
        _ = source.ReadAllAsync(TestContext.Current.CancellationToken);

        Assert.Throws<InvalidOperationException>(() => source.ReadAllAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Adding_a_trapper_twice_hosts_one_listener_and_the_last_word_wins()
    {
        var services = new ServiceCollection();
        services.AddZabbixPushAgent("relay")
            .AddTrapper(0, o => { o.AllowedPeers.Add("127.0.0.1"); o.MaxConcurrentConnections = 5; })
            .AddTrapper(0, o => o.MaxConcurrentConnections = 50)
            .AddServer(new DnsEndPoint("127.0.0.1", 10051));
        using ServiceProvider provider = services.BuildValidated();

        Assert.Single(provider.GetServices<IHostedService>(), service => service is ZabbixTrapperSource);
        Assert.Equal(50, provider.GetRequiredService<IOptionsMonitor<ZabbixListenerOptions>>().Get("relay").MaxConcurrentConnections);
    }

    [Fact]
    public async Task A_relay_s_intake_holds_as_many_values_as_each_of_its_stores()
    {
        var services = new ServiceCollection();
        services.AddZabbixPushAgent("relay").AddServer("zbx-1").AddTrapper(0, o => o.AllowedPeers.Add("127.0.0.1")).Configure(o => o.MaxItems = 1);
        await using ServiceProvider provider = services.BuildValidated();
        ZabbixTrapperSource source = provider.GetRequiredKeyedService<ZabbixTrapperSource>(new ZabbixTrapperKey("relay"));

        await source.Trapper.Handler(new ZabbixRequest(ThreeValues, null), TestContext.Current.CancellationToken);

        Assert.Equal((1, 2), (source.Trapper.Received, source.Trapper.Failed));          // the agent's cap, 1, is the intake's
    }

    [Fact]
    public async Task A_built_agent_starts_its_trapper_after_itself_relays_what_it_takes_and_stops_it_first()
    {
        var (port, received) = MockTrapper.Collect(2);
        await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("relay")
            .AddServer(new DnsEndPoint("127.0.0.1", port))
            .AddTrapper(0, o => { o.Address = "127.0.0.1"; o.AllowedPeers.Add("127.0.0.1"); })
            .Configure(o => o.Send.BatchSize = 2)
            .Build();
        ZabbixTrapperSource trapper = Assert.IsType<ZabbixTrapperSource>(Assert.Single(agent.Companions));
        Assert.Null(trapper.Listener.LocalEndPoint);                                          // nothing listens before the agent starts

        await agent.StartAsync(TestContext.Current.CancellationToken);
        var bound = (IPEndPoint)trapper.Listener.LocalEndPoint!;
        using var sender = new ZabbixSender(bound.Address.ToString(), bound.Port);
        ZabbixSenderResponse response = await sender.SendAsync([new ZabbixValue("web01", "a", "1", DateTimeOffset.UnixEpoch), new ZabbixValue("web01", "b", "2", DateTimeOffset.UnixEpoch)], TestContext.Current.CancellationToken);

        Assert.Equal(2, response.Processed);
        Assert.Equal(2, await received.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        await agent.StopAsync(TestContext.Current.CancellationToken);
        Assert.Null(trapper.Listener.LocalEndPoint);                                          // the port closed with the stop
    }
}
