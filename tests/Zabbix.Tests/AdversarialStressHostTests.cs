// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Community.Zabbix.Hosting;
using Community.Zabbix.Listening;
using Community.Zabbix.Protocol;
using Community.Zabbix.Storage;
using Community.Zabbix.Storage.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Community.Zabbix.Tests;

/// <summary>
/// Every hosted service of the libraries, enumerated by reflection so a new one fails the
/// enumeration until it has a scenario, run in a real <see cref="IHost"/>: started, fed by many producers, stopped in the
/// middle of the traffic. No exception escapes the host, the host's shutdown completes within a failsafe bound (no loop
/// is left running), and nothing a service acknowledged is lost or duplicated. The same services are also disposed and
/// stopped without ever being started.
/// </summary>
[Collection("Adversarial hosts stress")]
public sealed class AdversarialStressHostTests
{
    private static readonly TimeSpan Failsafe = TimeSpan.FromSeconds(20);
    private const int Producers = 8;
    private const int StopAfter = 200;

    /// <summary>Which scenario covers which hosted service: the enumeration below fails on a type missing here.</summary>
    private static readonly Dictionary<Type, string> Scenarios = new()
    {
        [typeof(ZabbixSourcePump<>)] = nameof(A_bare_agent_pump_stopped_mid_traffic_completes_its_storage_and_loses_nothing_it_wrote),
        [typeof(ZabbixHostedComponent)] = nameof(An_agent_fed_by_a_trapper_stopped_mid_traffic_delivers_every_value_the_trapper_acknowledged),
        [typeof(ZabbixPushAgent)] = nameof(An_agent_fed_by_a_trapper_stopped_mid_traffic_delivers_every_value_the_trapper_acknowledged),
        [typeof(ZabbixTrapperSource)] = nameof(An_agent_fed_by_a_trapper_stopped_mid_traffic_delivers_every_value_the_trapper_acknowledged),
        [typeof(ZabbixActiveAgentDestination)] = nameof(An_active_agent_with_two_destinations_stopped_mid_traffic_never_sends_an_id_twice_after_success),
        [typeof(ZabbixActiveAgent)] = nameof(An_active_agent_with_two_destinations_stopped_mid_traffic_never_sends_an_id_twice_after_success),
        [typeof(ZabbixActiveProxy)] = nameof(An_active_proxy_on_a_durable_store_stopped_mid_traffic_keeps_or_delivers_every_acknowledged_value),
        [typeof(ZabbixPassiveProxy)] = nameof(A_passive_proxy_pulled_while_trapping_and_stopped_mid_traffic_never_hands_a_value_out_twice),
        [typeof(ZabbixPassiveAgent)] = nameof(A_passive_agent_under_many_getters_stopped_mid_traffic_ends_every_conversation),
        [typeof(ZabbixListener)] = nameof(A_listener_under_many_clients_stopped_mid_traffic_ends_every_conversation),
    };

    // Every library the test project references (Conformance.Libraries reads them from deps.json), so a new library's
    // hosted service is under this rule the day it is added.
    private static readonly IReadOnlyList<Assembly> Libraries = Conformance.Libraries.All;

    [Fact]
    public void Every_hosted_service_in_the_libraries_has_a_stress_scenario()
    {
        Type[] hosted = [.. Libraries.SelectMany(assembly => assembly.GetTypes())
            .Where(type => type is { IsClass: true, IsAbstract: false } && (typeof(IHostedService).IsAssignableFrom(type) || typeof(ZabbixComponent).IsAssignableFrom(type)))
            .Select(type => type.IsGenericType ? type.GetGenericTypeDefinition() : type)
            .Distinct()];

        Assert.Equal(Scenarios.Keys.Select(type => type.FullName).Order(StringComparer.Ordinal), hosted.Select(type => type.FullName).Order(StringComparer.Ordinal));
        Assert.All(Scenarios.Values, name => Assert.NotNull(typeof(AdversarialStressHostTests).GetMethod(name)));
    }

    // ----- the host ---------------------------------------------------------------------------------------------------

    private static HostApplicationBuilder Builder()
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true, EnvironmentName = Environments.Production });
        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = TimeSpan.FromSeconds(10));
        return builder;
    }

    /// <summary>Stops the host while <paramref name="traffic"/> still runs: the host's own stop, bounded by the failsafe.</summary>
    private static async Task StopMidTrafficAsync(IHost host, CancellationTokenSource producers, Task traffic)
    {
        Task stopping = host.StopAsync(CancellationToken.None);
        await stopping.WaitAsync(Failsafe, TestContext.Current.CancellationToken);                   // no loop left running past the bound
        await producers.CancelAsync();
        await traffic.WaitAsync(Failsafe, TestContext.Current.CancellationToken);
    }

    /// <summary>A signal raised once <paramref name="target"/> acknowledgements were counted: the moment to stop mid-traffic.</summary>
    private sealed class Midpoint(int target)
    {
        private readonly TaskCompletionSource _reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _count;

        public Task Reached => _reached.Task.WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        public void Add(int count)
        {
            if (Interlocked.Add(ref _count, count) >= target) _reached.TrySetResult();
        }
    }

    /// <summary>Runs <paramref name="producer"/> <see cref="Producers"/> times at once until cancelled; the failures a stop causes are the producer's to swallow.</summary>
    private static Task Produce(Func<int, CancellationToken, Task> producer, CancellationToken stop)
        => Task.WhenAll(Enumerable.Range(0, Producers).Select(index => Task.Run(() => producer(index, stop), CancellationToken.None)));

    private static bool IsStopFailure(Exception failure)
        => failure is OperationCanceledException or IOException or SocketException or ZabbixTrapperRefusedException or InvalidDataException or TimeoutException or ObjectDisposedException
           or ZabbixConnectionRefusedException;

    /// <summary>A producer that traps values on <paramref name="node"/> in frames of five, recording those the trapper said it took.</summary>
    private static async Task TrapAsync(ZabbixServerNode node, int producer, ConcurrentBag<string> acknowledged, Midpoint midpoint, string host, string key, CancellationToken stop)
    {
        using var sender = new ZabbixSender([node]);
        for (int frame = 0; !stop.IsCancellationRequested; frame++)
        {
            ZabbixValue[] values = [.. Enumerable.Range(0, 5).Select(n => new ZabbixValue(host, key, $"{producer}:{frame}:{n}", DateTimeOffset.FromUnixTimeSeconds(1_700_000_000 + frame)))];
            try
            {
                ZabbixSenderResponse response = await sender.SendAsync(values, stop);
                if (response.Processed != values.Length) continue;                                   // refused: not acknowledged, not counted
                foreach (ZabbixValue value in values) acknowledged.Add(value.Value);
                midpoint.Add(values.Length);
            }
            catch (Exception failure) when (IsStopFailure(failure))
            {
                if (stop.IsCancellationRequested) return;
            }
        }
    }

    private static string SenderSuccess(JsonElement frame)
    {
        int count = frame.TryGetProperty("data", out JsonElement data) ? data.GetArrayLength() : 0;
        return MockTrapper.Success(count, 0);
    }

    // ----- the scenarios ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_agent_fed_by_a_trapper_stopped_mid_traffic_delivers_every_value_the_trapper_acknowledged()
    {
        await using AdversarialScriptedServer destination = await AdversarialScriptedServer.StartAsync((_, _, frame) => SenderSuccess(frame));
        HostApplicationBuilder builder = Builder();
        builder.Services.AddZabbixPushAgent("stress")
            .AddTrapper(0, o => { o.Address = "127.0.0.1"; o.AllowedPeers.Add("127.0.0.1"); })                    // its intake holds the agent's MaxItems, 100,000
            .AddServer(new DnsEndPoint(destination.Node.Host, destination.Node.Port))
            .Configure(o => { o.Send.BatchSize = 50; o.Send.FlushInterval = TimeSpan.FromMilliseconds(20); o.Send.DrainTimeout = TimeSpan.FromSeconds(5); });
        using IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var bound = (IPEndPoint)host.Services.GetRequiredKeyedService<ZabbixTrapperSource>(new ZabbixTrapperKey("stress")).Listener!.LocalEndPoint!;
        var trapper = new ZabbixServerNode(bound.Address.ToString(), bound.Port);
        var acknowledged = new ConcurrentBag<string>();
        var midpoint = new Midpoint(StopAfter);
        using var producers = new CancellationTokenSource();
        Task traffic = Produce((index, stop) => TrapAsync(trapper, index, acknowledged, midpoint, "web-01", "cpu", stop), producers.Token);
        await midpoint.Reached;

        await StopMidTrafficAsync(host, producers, traffic);

        var delivered = new List<string>();
        while (destination.Frames("sender data").TryRead(out JsonElement frame))
            delivered.AddRange(frame.GetProperty("data").EnumerateArray().Select(value => value.GetProperty("value").GetString()!));
        Assert.Equal(delivered.Count, delivered.Distinct(StringComparer.Ordinal).Count());           // nothing sent twice
        Assert.Empty(acknowledged.Except(delivered, StringComparer.Ordinal));                        // nothing the trapper took was lost
    }

    [Fact]
    public async Task An_active_agent_with_two_destinations_stopped_mid_traffic_never_sends_an_id_twice_after_success()
    {
        string checks = "{\"response\":\"success\",\"config_revision\":\"1\",\"data\":["
            + string.Join(",", Enumerable.Range(0, 60).Select(n => $$"""{"itemid":"{{100 + n}}","key":"stress.value[{{n}}]","delay":"1s"}"""))
            + "]}";
        var midpoint = new Midpoint(StopAfter);                                                       // sixty values a second to each destination
        string Script(string kind, JsonElement frame)
        {
            if (kind == "active checks") return checks;
            if (kind == "agent data") midpoint.Add(frame.GetProperty("data").GetArrayLength());
            return """{"response":"success","info":"processed: 1; failed: 0; total: 1; seconds spent: 0.000100"}""";
        }
        await using AdversarialScriptedServer first = await AdversarialScriptedServer.StartAsync((kind, _, frame) => Script(kind, frame));
        await using AdversarialScriptedServer second = await AdversarialScriptedServer.StartAsync((kind, _, frame) => Script(kind, frame));
        HostApplicationBuilder builder = Builder();
        builder.Services.AddZabbixActiveAgent("stress")
            .AddServer(new DnsEndPoint(first.Node.Host, first.Node.Port)).AddServer(new DnsEndPoint(second.Node.Host, second.Node.Port))
            .AddItem("stress.value", key => key.Parameters.Count > 0 ? key.Parameters[0] : "0")
            .Configure(o => { o.HostName = "stress-01"; o.HeartbeatInterval = TimeSpan.Zero; o.Send.BatchSize = 10; o.Send.FlushInterval = TimeSpan.FromMilliseconds(20); });
        using IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        using var producers = new CancellationTokenSource();                                          // the agent's own checks are the producers here
        await midpoint.Reached;

        await StopMidTrafficAsync(host, producers, Task.CompletedTask);

        foreach (AdversarialScriptedServer destination in new[] { first, second })
        {
            var ids = new List<long>();
            while (destination.Frames("agent data").TryRead(out JsonElement frame))
                ids.AddRange(frame.GetProperty("data").EnumerateArray().Select(value => value.GetProperty("id").GetInt64()));
            Assert.NotEmpty(ids);
            Assert.Equal(ids.Count, ids.Distinct().Count());                                         // every reply was success: no id re-sent
        }
    }

    [Fact]
    public async Task An_active_proxy_on_a_durable_store_stopped_mid_traffic_keeps_or_delivers_every_acknowledged_value()
    {
        string file = Path.Combine(Path.GetTempPath(), $"zbx-adv-stress-{Guid.NewGuid():N}.db");
        try
        {
            await using var server = new FakeZabbixServer();
            server.Configure("[10,\"web-01\",0]", "[100,2,10,\"cpu\",0,\"\",0]");
            await server.StartAsync();
            HostApplicationBuilder builder = Builder();
            builder.Services.AddZabbixActiveProxy("stress-proxy")
                .WithServer(server.Node.ToDnsEndPoint())
                .AddTrapper(0, o => o.Address = "127.0.0.1")
                .AllowPeer(IPAddress.Loopback)
                .UseEntityFrameworkStore(o => o.UseSqlite($"Data Source={file};Pooling=False"))
                .Configure(o => { o.DataInterval = TimeSpan.FromMilliseconds(20); o.PageSize = 25; o.DrainTimeout = TimeSpan.FromSeconds(2); });
            var acknowledged = new ConcurrentBag<string>();
            using (IHost host = builder.Build())
            {
                await host.StartAsync(TestContext.Current.CancellationToken);
                var bound = (IPEndPoint)host.Services.GetRequiredKeyedService<ZabbixActiveProxy>("stress-proxy").Trapper!.LocalEndPoint!;
                var trapper = new ZabbixServerNode(bound.Address.ToString(), bound.Port);
                var midpoint = new Midpoint(StopAfter);
                using var producers = new CancellationTokenSource();
                Task traffic = Produce((index, stop) => TrapAsync(trapper, index, acknowledged, midpoint, "web-01", "cpu", stop), producers.Token);
                await midpoint.Reached;

                await StopMidTrafficAsync(host, producers, traffic);
            }

            List<string> stored;
            lock (server.Stored) stored = [.. server.Stored.Select(value => value.Value)];
            await using var reopened = new ZabbixEntityFrameworkStore<ZabbixValue>(o => o.UseSqlite($"Data Source={file};Pooling=False"), services: new ZabbixStoreServices { Name = "stress-proxy" });
            var kept = new List<string>();
            while (reopened.TryLease(out ZabbixStoreEntry<ZabbixValue> entry)) kept.Add(entry.Record.Value);
            Assert.Equal(stored.Count, stored.Distinct(StringComparer.Ordinal).Count());
            Assert.Empty(acknowledged.Except(stored.Concat(kept), StringComparer.Ordinal));           // uploaded, or waiting for the next start
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            File.Delete(file);
        }
    }

    private const string PassiveConfig = """
        {"full_sync":1,"config_revision":9,"data":{"hosts":{"fields":["hostid","host","status"],"data":[[10,"web-01",0]]},
         "items":{"fields":["itemid","type","hostid","key_","status","trapper_hosts","allow_traps"],"data":[[100,2,10,"cpu",0,"",0]]}}}
        """;

    private static async Task<JsonElement> ConverseAsync(ZabbixServerNode node, string request, string? then, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(node.Host, node.Port, cancellationToken);
        await using NetworkStream stream = client.GetStream();
        await ZabbixHeader.WriteAsync(stream, Encoding.UTF8.GetBytes(request), cancellationToken);
        JsonElement reply = JsonDocument.Parse(await ZabbixHeader.ReadAsync(stream, cancellationToken)).RootElement.Clone();
        if (then is not null)
        {
            await ZabbixHeader.WriteAsync(stream, Encoding.UTF8.GetBytes(then), cancellationToken);
            try { await ZabbixHeader.ReadAsync(stream, cancellationToken); }
            catch (Exception failure) when (IsStopFailure(failure)) { /* the proxy closes after an ack */ }
        }
        return reply;
    }

    [Fact]
    public async Task A_passive_proxy_pulled_while_trapping_and_stopped_mid_traffic_never_hands_a_value_out_twice()
    {
        HostApplicationBuilder builder = Builder();
        builder.Services.AddZabbixPassiveProxy("stress-passive").AddTrapper(0, o => o.Address = "127.0.0.1").AllowPeer(IPAddress.Loopback).WithVersion("7.4.0").Configure(o => o.PageSize = 25);
        using IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var bound = (IPEndPoint)host.Services.GetRequiredKeyedService<ZabbixPassiveProxy>("stress-passive").Listener!.LocalEndPoint!;
        var node = new ZabbixServerNode(bound.Address.ToString(), bound.Port);
        await ConverseAsync(node, """{"request":"proxy config"}""", PassiveConfig, TestContext.Current.CancellationToken);
        var acknowledged = new ConcurrentBag<string>();
        var pulled = new ConcurrentBag<string>();
        var midpoint = new Midpoint(StopAfter);
        using var producers = new CancellationTokenSource();
        Task pulling = Task.Run(async () =>
        {
            while (!producers.IsCancellationRequested)
            {
                try
                {
                    JsonElement page = await ConverseAsync(node, """{"request":"proxy data"}""", """{"response":"success"}""", producers.Token);
                    if (page.TryGetProperty("history data", out JsonElement rows))
                        foreach (JsonElement row in rows.EnumerateArray()) pulled.Add(row.GetProperty("value").GetString()!);
                }
                catch (Exception failure) when (IsStopFailure(failure) || failure is JsonException) { /* stopped mid-conversation */ }
            }
        }, CancellationToken.None);
        Task traffic = Task.WhenAll(pulling, Produce((index, stop) => TrapAsync(node, index, acknowledged, midpoint, "web-01", "cpu", stop), producers.Token));
        await midpoint.Reached;

        await StopMidTrafficAsync(host, producers, traffic);

        Assert.NotEmpty(pulled);
        Assert.Equal(pulled.Count, pulled.Distinct(StringComparer.Ordinal).Count());                  // every page was acknowledged: no value twice
        Assert.Empty(pulled.Except(acknowledged, StringComparer.Ordinal));
    }

    [Fact]
    public async Task A_passive_agent_under_many_getters_stopped_mid_traffic_ends_every_conversation()
    {
        HostApplicationBuilder builder = Builder();
        builder.Services.AddZabbixPassiveAgent("stress").Configure(o => { o.Address = "127.0.0.1"; o.Port = 0; o.HostName = "stress-01"; o.AllowedPeers.Add("127.0.0.1"); });
        using IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var bound = (IPEndPoint)host.Services.GetRequiredKeyedService<ZabbixPassiveAgent>("stress").Listener!.LocalEndPoint!;
        using var getter = new ZabbixGetter(new ZabbixServerNode(bound.Address.ToString(), bound.Port));
        var wrong = new ConcurrentBag<string>();
        var midpoint = new Midpoint(StopAfter);
        using var producers = new CancellationTokenSource();
        Task traffic = Produce(async (_, stop) =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    ZabbixAgentAnswer answer = await getter.GetAsync("agent.hostname", stop);
                    if (answer.Value != "stress-01") wrong.Add(answer.Value ?? answer.Error ?? "nothing");
                    midpoint.Add(1);
                }
                catch (Exception failure) when (IsStopFailure(failure)) { /* stopped */ }
            }
        }, producers.Token);
        await midpoint.Reached;

        await StopMidTrafficAsync(host, producers, traffic);

        Assert.Empty(wrong);
    }

    [Fact]
    public async Task A_listener_under_many_clients_stopped_mid_traffic_ends_every_conversation()
    {
        HostApplicationBuilder builder = Builder();
        builder.Services.AddSingleton(provider => new ZabbixListener(new ZabbixListenerOptions { Address = "127.0.0.1", Port = 0, AllowedPeers = { "127.0.0.1" } },
            static (request, _) => ValueTask.FromResult(request.Payload), ListenerTests.Transport()));
        builder.Services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ZabbixListener>());
        using IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var bound = (IPEndPoint)host.Services.GetRequiredService<ZabbixListener>().LocalEndPoint!;
        var node = new ZabbixServerNode(bound.Address.ToString(), bound.Port);
        var wrong = new ConcurrentBag<string>();
        var midpoint = new Midpoint(StopAfter);
        using var producers = new CancellationTokenSource();
        Task traffic = Produce(async (index, stop) =>
        {
            for (int n = 0; !stop.IsCancellationRequested; n++)
            {
                string request = $$"""{"request":"echo","n":"{{index}}:{{n}}"}""";
                try
                {
                    JsonElement echoed = await ConverseAsync(node, request, then: null, stop);
                    if (echoed.GetProperty("n").GetString() != $"{index}:{n}") wrong.Add(request);
                    midpoint.Add(1);
                }
                catch (Exception failure) when (IsStopFailure(failure) || failure is JsonException) { /* stopped */ }
            }
        }, producers.Token);
        await midpoint.Reached;

        await StopMidTrafficAsync(host, producers, traffic);

        Assert.Empty(wrong);
    }

    [Fact]
    public async Task A_bare_agent_pump_stopped_mid_traffic_completes_its_storage_and_loses_nothing_it_wrote()
    {
        Channel<string>[] sources = [.. Enumerable.Range(0, Producers).Select(_ => Channel.CreateUnbounded<string>())];
        var storage = Channel.CreateBounded<string>(new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.Wait });
        HostApplicationBuilder builder = Builder();
        builder.Services.AddHostedService(_ => new ZabbixSourcePump<string>(storage.Writer, sources.Select(source => source.Reader.ReadAllAsync())));
        using IHost host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        var midpoint = new Midpoint(StopAfter);
        var drained = new List<string>();
        Task draining = Task.Run(async () =>
        {
            await foreach (string value in storage.Reader.ReadAllAsync(CancellationToken.None))
            {
                drained.Add(value);
                midpoint.Add(1);
            }
        }, CancellationToken.None);
        using var producers = new CancellationTokenSource();
        Task traffic = Produce(async (index, stop) =>
        {
            for (int n = 0; !stop.IsCancellationRequested; n++)
            {
                sources[index].Writer.TryWrite($"{index}:{n}");
                await Task.Yield();
            }
        }, producers.Token);
        await midpoint.Reached;

        await StopMidTrafficAsync(host, producers, traffic);

        await draining.WaitAsync(Failsafe, TestContext.Current.CancellationToken);                   // the storage completed: the reader ended
        Assert.Equal(drained.Count, drained.Distinct(StringComparer.Ordinal).Count());
    }

    // ----- never started ------------------------------------------------------------------------------------------------

    public static TheoryData<string> Registrations => new("agent", "active agent", "proxy", "passive proxy", "passive agent");

    private static ServiceProvider Registered(string registration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        _ = registration switch
        {
            "agent" => (object)services.AddZabbixPushAgent("never").AddTrapper(0, o => o.AllowedPeers.Add("127.0.0.1")).AddServer(new DnsEndPoint("127.0.0.1", 1)),
            "active agent" => services.AddZabbixActiveAgent("never").AddServer(new DnsEndPoint("127.0.0.1", 1)),
            "proxy" => services.AddZabbixActiveProxy("never").WithServer(new DnsEndPoint("127.0.0.1", 1)).AddTrapper(0).AllowPeer(IPAddress.Loopback),
            "passive proxy" => services.AddZabbixPassiveProxy("never").AddTrapper(0).AllowPeer(IPAddress.Loopback).WithVersion("7.4.0"),
            "passive agent" => services.AddZabbixPassiveAgent("never").Configure(o => { o.Port = 0; o.AllowedPeers.Add("127.0.0.1"); }),
            _ => throw new ArgumentOutOfRangeException(nameof(registration), registration, "no such registration"),
        };
        return services.BuildValidated();
    }

    [Theory]
    [MemberData(nameof(Registrations))]
    public async Task A_hosted_service_resolved_and_disposed_without_a_start_disposes_cleanly(string registration)
    {
        ServiceProvider provider = Registered(registration);
        IHostedService[] hosted = [.. provider.GetServices<IHostedService>()];

        Exception? thrown = await Record.ExceptionAsync(async () => await provider.DisposeAsync());

        Assert.NotEmpty(hosted);
        Assert.Null(thrown);
    }

    [Theory]
    [MemberData(nameof(Registrations))]
    public async Task A_hosted_service_stopped_without_a_start_stops_at_once(string registration)
    {
        await using ServiceProvider provider = Registered(registration);
        IHostedLifecycleService[] hosted = [.. provider.GetServices<IHostedService>().Cast<IHostedLifecycleService>().Reverse()];

        Exception? thrown = await Record.ExceptionAsync(async () =>
        {
            foreach (IHostedLifecycleService service in hosted) await service.StoppingAsync(CancellationToken.None);
            foreach (IHostedLifecycleService service in hosted) await service.StopAsync(CancellationToken.None);
            foreach (IHostedLifecycleService service in hosted) await service.StoppedAsync(CancellationToken.None);
        }).AsTask().WaitAsync(Failsafe, TestContext.Current.CancellationToken);

        Assert.Null(thrown);
    }
}
