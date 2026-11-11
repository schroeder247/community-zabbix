// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Community.Zabbix.Diagnostics;
using Community.Zabbix.Hosting;
using Community.Zabbix.Protocol;
using Community.Zabbix.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// The SDK's pipelines, composed end to end: each stage is the platform's own shape (<see cref="IAsyncEnumerable{T}"/>, a <see cref="ZabbixStore{T}"/> that is
/// a <c>ChannelWriter</c>, a delegate destination), so a pipeline is the stages written one after the other, and what one
/// stage promises — order, back-pressure, cancellation reaching the socket, a fault surfacing after what came before it,
/// cancellation never counted as an error — must still hold at the far end. Every wait is on an observable.
/// </summary>
public sealed class PipelineCompositionTests
{
    private const long T0 = 1_700_000_000;

    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(30);                      // a safety net for a hung test, never a pacing sleep

    private const string Item = """[{"itemid":"1","hostid":"10","key_":"cpu","name":"CPU","delay":"10s","value_type":"0","lastvalue":"0","lastclock":"1700000000","lastns":"0","hosts":[{"hostid":"10","host":"web-01"}]}]""";

    private const string NeverCollected = """[{"itemid":"1","hostid":"10","key_":"cpu","name":"CPU","delay":"10s","value_type":"0","lastvalue":"","lastclock":"0","lastns":"0","hosts":[{"hostid":"10","host":"web-01"}]}]""";

    private const string TwoTypes = """
        [
          {"itemid":"1","hostid":"10","key_":"cpu","value_type":"0","lastvalue":"0","lastclock":"1700000000","lastns":"0","hosts":[{"hostid":"10","host":"web-01"}]},
          {"itemid":"2","hostid":"10","key_":"mem","value_type":"3","lastvalue":"0","lastclock":"1700000000","lastns":"0","hosts":[{"hostid":"10","host":"web-01"}]}
        ]
        """;

    private sealed record Composed<T>(ServiceProvider Provider, FakeLogCollector Logs) : IDisposable
        where T : HttpMessageHandler
    {
        public ZabbixClient Server => Provider.GetRequiredService<ZabbixClient>();

        public T Api => Provider.GetRequiredService<T>();

        public void Dispose() => Provider.Dispose();
    }

    /// <summary>A server over the API double <paramref name="api"/> makes; the container owns the double.</summary>
    private static Composed<T> Build<T>(Func<T> api, TimeProvider? time = null)
        where T : HttpMessageHandler
    {
        var services = new ServiceCollection();
        if (time is not null) services.AddSingleton(time);
        services.AddSingleton(_ => api());
        services.AddFakeLogging().AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace));
        services.AddZabbixClient().Configure(options => { options.Endpoint = new Uri("https://zbx.example/api_jsonrpc.php"); options.ApiToken = "tok"; });
        services.AddHttpClient(ZabbixHttp.GetClientName()).ConfigurePrimaryHttpMessageHandler(provider => provider.GetRequiredService<T>());
        ServiceProvider provider = services.BuildValidated();
        return new Composed<T>(provider, provider.GetFakeLogCollector());
    }

    private static string Rows(string itemId, IEnumerable<(long Clock, int Ns)> rows)
        => "[" + string.Join(",", rows.Select(row => string.Create(CultureInfo.InvariantCulture, $$"""{"itemid":"{{itemId}}","clock":"{{row.Clock}}","ns":"{{row.Ns}}","value":"{{row.Clock}}.{{row.Ns}}"}"""))) + "]";

    /// <summary>
    /// An API double that answers <c>item.get</c> and the first <c>history.get</c> page, then holds every later request until
    /// its token is cancelled or <see cref="Next"/> answers it: the observable that a consumer's cancellation reached the
    /// request on the wire.
    /// </summary>
    private sealed class HeldHistoryApi(string items, string firstPage) : HttpMessageHandler
    {
        private int _pages;

        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Func<long, string>? Next { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using JsonDocument envelope = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            string method = envelope.RootElement.GetProperty("method").GetString()!;
            long id = envelope.RootElement.GetProperty("id").GetInt64();
            string member;
            if (method == "item.get")
            {
                member = $"\"result\":{items}";
            }
            else if (Interlocked.Increment(ref _pages) == 1)
            {
                member = $"\"result\":{firstPage}";
            }
            else if (Next is { } next)
            {
                member = next(id);
            }
            else
            {
                Held.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    Cancelled.TrySetResult();
                    throw;
                }
                member = "\"result\":[]";
            }
            string reply = string.Create(CultureInfo.InvariantCulture, $$"""{"jsonrpc":"2.0",{{member}},"id":{{id}}}""");
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }

    // A first page that saturates (5,000 rows, ZabbixHistoryRead.PageSize) and ends inside second T0 + 9, so the read must
    // ask for that second whole before it can go on: the second request is always made, mid-stream.
    private static readonly string SaturatedPage = Rows("1", Enumerable.Range(0, 5000).Select(n => (T0 + n / 500, n % 500)));

    [Fact]
    public async Task A_watcher_drains_through_a_store_and_the_send_loop_to_a_trapper_in_clock_order()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(T0 + 5));
        var history = new FakeHistory();
        for (int n = 1; n <= 6; n++) history.Add("1", ZabbixItemValueType.Float, T0 + n * 10, 0, n.ToString(CultureInfo.InvariantCulture));
        using Composed<ScriptedApi> composed = Build(() => new ScriptedApi().On("item.get", NeverCollected).On("history.get", history.Answer), time);   // no seeded value: six in, six out
        IMeterFactory meters = composed.Provider.GetRequiredService<IMeterFactory>();
        using var polls = new MetricCollector<long>(meters, ZabbixTelemetryNames.Tree, ZabbixTreeTelemetry.WatcherPolls);
        (int port, Task<int> received) = MockTrapper.Collect(6);
        using var sender = new ZabbixSender("127.0.0.1", port);
        await using var store = new ZabbixMemoryStore<ZabbixValue>();
        var sent = new List<ZabbixValue>();

        IZabbixWatcher watcher = composed.Server.Hosts["web-01"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(10)));
        IAsyncEnumerator<ZabbixValue> reader = watcher.GetAsyncEnumerator(TestContext.Current.CancellationToken);   // attached before the clock moves
        Task pumping = Task.Run(async () =>
        {
            await using (reader)
            {
                while (await reader.MoveNextAsync()) await store.WriteAsync(reader.Current, TestContext.Current.CancellationToken);
            }
            store.Complete();                                                                   // the watcher ended: so does the store
        }, TestContext.Current.CancellationToken);
        Task<long> sending = ZabbixSendLoop.RunAsync(store, async (batch, token) =>
        {
            lock (sent) sent.AddRange(batch);
            return await sender.DeliverAsync(batch, token);
        }, new ZabbixSendLoopOptions { Send = new ZabbixSendOptions { BatchSize = 3, FlushInterval = TimeSpan.FromHours(1) }, Time = time }, cancellationToken: TestContext.Current.CancellationToken);

        await polls.WaitForMeasurementsAsync(1, TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);   // seeded
        for (int tick = 2; tick <= 7; tick++)
        {
            time.Advance(TimeSpan.FromSeconds(10));
            await polls.WaitForMeasurementsAsync(tick, TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);
        }
        await received.WaitAsync(Wait, TestContext.Current.CancellationToken);                   // two full batches of three reached the wire
        await watcher.DisposeAsync();                                                          // end of the line, front first
        await pumping.WaitAsync(Wait, TestContext.Current.CancellationToken);
        long taken = await sending.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal(["1", "2", "3", "4", "5", "6"], sent.Select(value => value.Value));
        Assert.Equal(sent.Select(value => value.Clock).Order(), sent.Select(value => value.Clock));   // clock order, end to end
        Assert.Equal(sent.Count, taken);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public async Task Cancelling_a_history_read_mid_stream_cancels_the_request_on_the_wire_and_is_not_an_error()
    {
        using var capture = new TelemetryCapture(ZabbixTelemetryNames.Tree);
        using Composed<HeldHistoryApi> composed = Build(() => new HeldHistoryApi(Item, SaturatedPage));
        HeldHistoryApi api = composed.Api;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        int read = 0;

        Task reading = Task.Run(async () =>
        {
            await foreach (ZabbixValue _ in composed.Server.Hosts["web-01"].GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(T0), cancellationToken: stop.Token))
                read++;
        }, TestContext.Current.CancellationToken);
        await api.Held.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);           // the whole-second read is on the wire
        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading.WaitAsync(Wait, TestContext.Current.CancellationToken));
        await api.Cancelled.Task.WaitAsync(Wait, TestContext.Current.CancellationToken);      // the request itself saw the cancellation
        Assert.Equal(4500, read);                                                               // every row before the held second, none after
        Activity[] pages = [.. capture.Spans.Where(span => span.OperationName == ZabbixTreeTelemetry.HistoryPageSpan)];
        Assert.Equal(2, pages.Length);
        Assert.All(pages, span => Assert.Null(span.GetTagItem("error.type")));                 // cancellation is not an error
        Assert.DoesNotContain(pages, span => span.Status == ActivityStatusCode.Error);
        Assert.DoesNotContain(composed.Logs.GetSnapshot(), record => record.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task An_api_error_mid_history_surfaces_after_the_rows_already_read_and_fails_only_its_page()
    {
        using var capture = new TelemetryCapture(ZabbixTelemetryNames.Tree);
        using Composed<HeldHistoryApi> composed = Build(() => new HeldHistoryApi(Item, SaturatedPage)
        {
            Next = _ => "\"error\":" + """{"code":-32500,"message":"Application error.","data":"Database error occurred."}""",
        });
        int read = 0;

        ZabbixApiException failed = await Assert.ThrowsAsync<ZabbixApiException>(async () =>
        {
            await foreach (ZabbixValue _ in composed.Server.Hosts["web-01"].GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(T0), cancellationToken: TestContext.Current.CancellationToken))
                read++;
        });

        Assert.Equal(ZabbixApiErrorKind.Database, failed.Kind);
        Assert.Equal(4500, read);                                                               // what came before the fault was yielded first
        Activity[] pages = [.. capture.Spans.Where(span => span.OperationName == ZabbixTreeTelemetry.HistoryPageSpan)];
        Assert.Equal([ActivityStatusCode.Ok, ActivityStatusCode.Error], pages.Select(span => span.Status));
        Assert.Equal("-32500", pages[1].GetTagItem("error.type"));
    }

    [Fact]
    public async Task A_watcher_reader_that_cancels_leaves_without_stopping_the_poll_or_the_other_reader()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(T0 + 5));
        var history = new FakeHistory().Add("1", ZabbixItemValueType.Float, T0 + 10, 0, "1").Add("1", ZabbixItemValueType.Float, T0 + 20, 0, "2");
        using Composed<ScriptedApi> composed = Build(() => new ScriptedApi().On("item.get", Item).On("history.get", history.Answer), time);
        using var polls = new MetricCollector<long>(composed.Provider.GetRequiredService<IMeterFactory>(), ZabbixTelemetryNames.Tree, ZabbixTreeTelemetry.WatcherPolls);
        await using IZabbixWatcher watcher = composed.Server.Hosts["web-01"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(10)));
        using var leaving = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using IAsyncEnumerator<ZabbixValue> quitter = watcher.GetAsyncEnumerator(leaving.Token);
        await using IAsyncEnumerator<ZabbixValue> stayer = watcher.GetAsyncEnumerator(TestContext.Current.CancellationToken);
        await polls.WaitForMeasurementsAsync(1, TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);

        Task<bool> pending = quitter.MoveNextAsync().AsTask();
        if (pending.IsCompleted)                                                                // the seeded value, if the seed reached this reader
        {
            Assert.True(await pending);
            pending = quitter.MoveNextAsync().AsTask();
        }
        await leaving.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        await quitter.DisposeAsync();
        time.Advance(TimeSpan.FromSeconds(10));
        await polls.WaitForMeasurementsAsync(2, TestContext.Current.CancellationToken).WaitAsync(Wait, TestContext.Current.CancellationToken);

        var values = new List<string>();
        while (values.Count < 1 || values[^1] != "1")
        {
            Assert.True(await stayer.MoveNextAsync().AsTask().WaitAsync(Wait, TestContext.Current.CancellationToken));
            values.Add(stayer.Current.Value);
        }
        Assert.Equal(0, watcher.DroppedCount);
        Assert.Equal(2, watcher.SucceededPolls);
    }

    [Fact]
    public async Task A_disposed_server_refuses_a_new_watch()
    {
        using Composed<ScriptedApi> composed = Build(() => new ScriptedApi().On("item.get", Item).On("history.get", "[]"));
        ZabbixClient client = composed.Server;

        await composed.Provider.DisposeAsync();                                                       // the container that shares the client

        Assert.Throws<ObjectDisposedException>(() => client.Hosts["web-01"].Watch(new ZabbixWatchPlan(TimeSpan.FromSeconds(10))));
    }

    [Fact]
    public async Task A_null_result_where_a_list_is_expected_is_an_invalid_response_on_every_signal()
    {
        using Composed<ScriptedApi> composed = Build(() => new ScriptedApi().On("host.get", "null"));

        JsonException failure = await Assert.ThrowsAsync<JsonException>(() => composed.Server.Hosts.GetAsync(cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("The host.get reply carries a null result", failure.Message, StringComparison.Ordinal);
        Assert.Contains(composed.Logs.GetSnapshot(), record => record.Id.Id == 4003 && record.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task An_empty_first_page_ends_a_history_read_with_one_request_per_value_type()
    {
        using Composed<ScriptedApi> composed = Build(() => new ScriptedApi().On("item.get", TwoTypes).On("history.get", "[]"));
        ScriptedApi api = composed.Api;

        List<ZabbixValue> values = await composed.Server.Hosts["web-01"].GetHistoryAsync(DateTimeOffset.FromUnixTimeSeconds(T0), cancellationToken: TestContext.Current.CancellationToken)
            .ToListAsync(TestContext.Current.CancellationToken);

        Assert.Empty(values);
        Assert.Equal(2, api.ParamsOf("history.get").Count());
    }
}
