// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using System.Text;
using System.Text.Json;
using Community.Zabbix.Listening;
using Community.Zabbix.Storage;
using Community.Zabbix.Storage.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Community.Zabbix.Tests;

/// <summary>
/// Adversarial tests of the trapper's rows: ten thousand values in one sender frame, a nanosecond outside the second,
/// clocks at the ends of the range (one a durable store cannot hold), bytes that are not UTF-8, rows that are not
/// objects. Every frame is answered in the server's words, and the counts say exactly what was kept.
/// </summary>
public sealed class AdversarialTrapperRowTests
{
    private static readonly EndPoint Peer = new IPEndPoint(IPAddress.Loopback, 40000);

    private static async Task<(string Response, string Info)> SendAsync(ZabbixTrapper trapper, string frame)
        => await SendAsync(trapper, Encoding.UTF8.GetBytes(frame));

    private static async Task<(string Response, string Info)> SendAsync(ZabbixTrapper trapper, byte[] frame)
    {
        ReadOnlyMemory<byte> reply = await trapper.Handler(new ZabbixRequest(frame, Peer), TestContext.Current.CancellationToken);
        using JsonDocument answer = JsonDocument.Parse(reply);
        return (answer.RootElement.GetProperty("response").GetString()!, answer.RootElement.GetProperty("info").GetString()!);
    }

    private static string Counts(int processed, int failed) => $"processed: {processed}; failed: {failed}; total: {processed + failed};";

    [Fact]
    public async Task Ten_thousand_values_in_one_frame_are_all_taken_and_counted()
    {
        await using var store = new ZabbixMemoryStore<ZabbixValue>(new ZabbixStorePolicy<ZabbixValue> { MaxItems = 20_000 });
        var trapper = new ZabbixTrapper(store);
        string rows = string.Join(",", Enumerable.Range(0, 10_000).Select(i => $"{{\"host\":\"web-01\",\"key\":\"cpu[{i}]\",\"value\":\"{i}\",\"clock\":1700000000,\"ns\":{i}}}"));

        (string response, string info) = await SendAsync(trapper, $"{{\"request\":\"sender data\",\"data\":[{rows}]}}");

        Assert.Equal("success", response);
        Assert.StartsWith(Counts(10_000, 0), info, StringComparison.Ordinal);
        Assert.Equal(10_000, store.Count);
    }

    [Theory]
    [InlineData(1_000_000_000)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task A_nanosecond_outside_the_second_fails_its_row_alone(int ns)
    {
        await using var store = new ZabbixMemoryStore<ZabbixValue>();
        var trapper = new ZabbixTrapper(store);

        (string response, string info) = await SendAsync(trapper, $$"""{"request":"sender data","data":[{"host":"h","key":"a","value":"1","clock":1700000000,"ns":{{ns}}},{"host":"h","key":"b","value":"2","clock":1700000000,"ns":999999999}]}""");

        Assert.Equal("success", response);
        Assert.StartsWith(Counts(1, 1), info, StringComparison.Ordinal);
        Assert.True(store.TryLease(out ZabbixStoreEntry<ZabbixValue> kept));
        Assert.Equal(("b", 999_999_999), (kept.Record.Key.ToString(), kept.Record.Clock.Nanoseconds));
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    [InlineData(4_102_444_800L)]                                                                  // 2100-01-01
    [InlineData(long.MaxValue)]
    public async Task A_clock_far_in_the_past_or_the_future_is_kept_as_sent_by_a_memory_store(long clock)
    {
        await using var store = new ZabbixMemoryStore<ZabbixValue>();
        var trapper = new ZabbixTrapper(store);

        (string response, string info) = await SendAsync(trapper, $$"""{"request":"sender data","data":[{"host":"h","key":"a","value":"1","clock":{{clock}}}]}""");

        Assert.Equal("success", response);
        Assert.StartsWith(Counts(1, 0), info, StringComparison.Ordinal);
        Assert.True(store.TryLease(out ZabbixStoreEntry<ZabbixValue> kept));
        Assert.Equal(clock, kept.Record.Clock.Seconds);
    }

    [Fact]
    public async Task A_clock_a_durable_store_cannot_hold_fails_its_row_alone_and_the_rest_of_the_frame_is_committed()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"zbx-adv-trap-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await using (var store = new ZabbixEntityFrameworkStore<ZabbixValue>(options => options.UseSqlite($"Data Source={Path.Combine(directory, "store.db")};Pooling=False"), services: new ZabbixStoreServices { Name = "trap" }))
            {
                var trapper = new ZabbixTrapper(store);

                // A clock past the year 2262 has no nanoseconds-since-the-epoch column value (ZabbixClock.ToUnixNanoseconds overflows).
                (string response, string info) = await SendAsync(trapper, """{"request":"sender data","data":[{"host":"h","key":"a","value":"1","clock":1700000000},{"host":"h","key":"b","value":"2","clock":9999999999},{"host":"h","key":"c","value":"3","clock":1700000001}]}""");

                Assert.Equal("success", response);
                Assert.StartsWith(Counts(2, 1), info, StringComparison.Ordinal);
                Assert.Equal(2, store.Count);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Bytes_that_are_not_utf8_are_answered_as_not_a_sender_request_and_nothing_is_written()
    {
        await using var store = new ZabbixMemoryStore<ZabbixValue>();
        var trapper = new ZabbixTrapper(store);
        byte[] frame = [.. Encoding.UTF8.GetBytes("""{"request":"sender data","data":[{"host":"h","key":"a","value":" """), 0xC3, 0x28, 0xFF, .. Encoding.UTF8.GetBytes("\"}]}")];

        (string response, string info) = await SendAsync(trapper, frame);

        Assert.Equal("failed", response);
        Assert.Equal("not a sender data request", info);
        Assert.Equal(0, store.Count);
    }

    [Theory]
    [InlineData("""{"request":"sender data","data":[null,{"host":"h","key":"a","value":"1"}]}""", 1, 1)]
    [InlineData("""{"request":"sender data","data":[{"host":"","key":"a","value":"1"},{"host":"h","key":"","value":"1"}]}""", 0, 2)]
    [InlineData("""{"request":"sender data","data":[{"host":"h","key":"a[","value":"1"}]}""", 0, 1)]
    [InlineData("""{"request":"sender data","data":[{"host":"h","key":"a"}]}""", 1, 0)]
    [InlineData("""{"request":"sender data","data":[{"host":"h","key":"a","value":"x","state":1}]}""", 1, 0)]
    [InlineData("""{"request":"sender data","data":[{"host":"h","key":"a","state":1}]}""", 0, 1)]
    [InlineData("""{"request":"sender data","data":[]}""", 0, 0)]
    public async Task Rows_the_server_cannot_read_fail_alone_and_the_answer_counts_them(string frame, int processed, int failed)
    {
        await using var store = new ZabbixMemoryStore<ZabbixValue>();
        var trapper = new ZabbixTrapper(store);

        (string response, string info) = await SendAsync(trapper, frame);

        Assert.Equal("success", response);
        Assert.StartsWith(Counts(processed, failed), info, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"request":"sender data","data":[1,2]}""")]
    [InlineData("""{"request":"sender data","data":{"host":"h"}}""")]
    [InlineData("""{"request":"sender data"}""")]
    [InlineData("""{"request":"proxy config","host":"p"}""")]
    [InlineData("""[]""")]
    [InlineData("")]
    public async Task A_frame_that_is_not_a_sender_request_is_answered_failed_and_writes_nothing(string frame)
    {
        await using var store = new ZabbixMemoryStore<ZabbixValue>();
        var trapper = new ZabbixTrapper(store);

        (string response, _) = await SendAsync(trapper, frame);

        Assert.Equal("failed", response);
        Assert.Equal(0, store.Count);
    }
}
