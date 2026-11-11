// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Community.Zabbix.Listening;
using Community.Zabbix.Storage;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>The stress collection of the hosted-service adversarial tests: they run alone, one at a time.</summary>
[CollectionDefinition("Adversarial hosts stress", DisableParallelization = true)]
public sealed class AdversarialHostsStress
{
}

/// <summary>
/// A durable backend over a sorted list: the <see cref="ZabbixPersistentStore{T}"/> machinery with nothing of a database in the
/// way, so a failure it shows is the base class's own. Its rows can be handed to a second instance, which is what a
/// restart over the same table is. <see cref="FailAppends"/> makes every append throw (a full disk);
/// <see cref="FailLoads"/> every load (a backend that is down).
/// </summary>
internal class ListBackedStore<T>(ZabbixStorePolicy<T>? policy = null, ZabbixStoreServices? services = null, SortedList<long, (DateTimeOffset Stored, T Record)>? rows = null)
    : ZabbixPersistentStore<T>(policy, services)
{
    private readonly Lock _rows = new();

    /// <summary>The backend's rows; pass them to a new instance to restart over them.</summary>
    public SortedList<long, (DateTimeOffset Stored, T Record)> Rows { get; } = rows ?? [];

    /// <summary>The highest sequence ever given; a restarted backend continues above it.</summary>
    public long HighWater { get; set; }

    /// <summary>Every append throws <see cref="IOException"/> while set.</summary>
    public bool FailAppends { get; set; }

    /// <summary>Every load throws <see cref="InvalidOperationException"/> while set.</summary>
    public bool FailLoads { get; set; }

    /// <summary>A record this predicate answers true for fails its append (a value the backend cannot hold).</summary>
    public Func<T, bool>? Unholdable { get; set; }

    protected override ValueTask<int> OpenAsync(CancellationToken cancellationToken)
    {
        lock (_rows) return ValueTask.FromResult(Rows.Count);
    }

    protected override ValueTask<IReadOnlyList<long>> AppendAsync(IReadOnlyList<ZabbixStoreEntry<T>> records, CancellationToken cancellationToken)
    {
        if (FailAppends) throw new IOException("No space left on device");
        if (Unholdable is { } unholdable && records.Any(record => unholdable(record.Record))) throw new InvalidDataException("the backend cannot hold this value");
        lock (_rows)
        {
            var sequences = new List<long>(records.Count);
            foreach (ZabbixStoreEntry<T> record in records)
            {
                long sequence = Math.Max(HighWater, Rows.Count == 0 ? 0 : Rows.Keys[^1]) + 1;
                HighWater = sequence;
                Rows.Add(sequence, (record.Stored, record.Record));
                sequences.Add(sequence);
            }
            return ValueTask.FromResult<IReadOnlyList<long>>(sequences);
        }
    }

    protected override ValueTask<IReadOnlyList<ZabbixStoreEntry<T>>> LoadAfterAsync(long sequence, int count, CancellationToken cancellationToken)
    {
        if (FailLoads) throw new InvalidOperationException("backend down");
        lock (_rows)
        {
            IReadOnlyList<ZabbixStoreEntry<T>> loaded = [.. Rows.Where(row => row.Key > sequence).Take(count).Select(row => new ZabbixStoreEntry<T>(row.Key, row.Value.Stored, row.Value.Record))];
            return ValueTask.FromResult(loaded);
        }
    }

    protected override ValueTask<int> RemoveAsync(long first, long last, CancellationToken cancellationToken)
    {
        lock (_rows)
        {
            long[] gone = [.. Rows.Keys.Where(key => key >= first && key <= last)];
            foreach (long key in gone) Rows.Remove(key);
            return ValueTask.FromResult(gone.Length);
        }
    }
}

/// <summary>The sorted-list backend under the name the adversarial store tests use.</summary>
internal sealed class AdversarialRowStore<T>(ZabbixStorePolicy<T>? policy = null, ZabbixStoreServices? services = null) : ListBackedStore<T>(policy, services);

/// <summary>
/// A wall clock that can be stepped backwards or forwards (an NTP step, a restored VM) while the monotonic clock and every
/// timer keep running on the inner <see cref="FakeTimeProvider"/>, which itself refuses to go back in time. This is what a
/// real machine does: <c>GetUtcNow</c> jumps, <c>GetTimestamp</c> and the timers do not.
/// </summary>
internal sealed class AdversarialSteppedClock(DateTimeOffset start) : TimeProvider
{
    private readonly Channel<TimeSpan> _created = Channel.CreateUnbounded<TimeSpan>();
    private long _offsetTicks;

    public FakeTimeProvider Inner { get; } = new(start) { AutoAdvanceAmount = TimeSpan.Zero };

    /// <summary>The due time of every timer created, in creation order.</summary>
    public ChannelReader<TimeSpan> Created => _created.Reader;

    /// <summary>Steps the wall clock only; the monotonic clock and the timers do not move.</summary>
    public void Step(TimeSpan by) => Interlocked.Add(ref _offsetTicks, by.Ticks);

    /// <summary>Moves real (monotonic) time: the timers fire.</summary>
    public void Advance(TimeSpan by) => Inner.Advance(by);

    public override DateTimeOffset GetUtcNow() => Inner.GetUtcNow() + TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));

    public override long GetTimestamp() => Inner.GetTimestamp();

    public override long TimestampFrequency => Inner.TimestampFrequency;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ITimer timer = Inner.CreateTimer(callback, state, dueTime, period);
        _created.Writer.TryWrite(dueTime);
        return timer;
    }
}

/// <summary>
/// A Zabbix server double that answers every frame from a script (request kind, how many of that kind it has seen, the
/// frame) and hands every frame to the test on a channel, so a test awaits what the peer sent instead of sleeping.
/// </summary>
internal sealed class AdversarialScriptedServer : IAsyncDisposable
{
    private readonly ZabbixListener _listener;
    private readonly Func<string, int, JsonElement, Task<string>> _script;
    private readonly ConcurrentDictionary<string, int> _seen = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Channel<JsonElement>> _frames = new(StringComparer.Ordinal);

    private AdversarialScriptedServer(Func<string, int, JsonElement, Task<string>> script)
    {
        _script = script;
        _listener = new ZabbixListener(new ZabbixListenerOptions { Address = "127.0.0.1", Port = 0, AllowedPeers = { "127.0.0.1" } }, HandleAsync,
            new SocketTransportFactory(Options.Create(new SocketTransportOptions()), NullLoggerFactory.Instance));
    }

    public static Task<AdversarialScriptedServer> StartAsync(Func<string, int, JsonElement, string> script)
        => StartAsync((kind, nth, frame) => Task.FromResult(script(kind, nth, frame)));

    /// <summary>A server whose reply may wait (a server that hangs mid-exchange answers when the test lets it).</summary>
    public static async Task<AdversarialScriptedServer> StartAsync(Func<string, int, JsonElement, Task<string>> script)
    {
        var server = new AdversarialScriptedServer(script);
        await server._listener.StartingAsync(default);
        await server._listener.StartAsync(default);
        return server;
    }

    public ZabbixServerNode Node => new(((IPEndPoint)_listener.LocalEndPoint!).Address.ToString(), ((IPEndPoint)_listener.LocalEndPoint!).Port);

    /// <summary>How many frames of <paramref name="request"/> arrived.</summary>
    public int Seen(string request) => _seen.TryGetValue(request, out int count) ? count : 0;

    public ChannelReader<JsonElement> Frames(string request) => Channel(request).Reader;

    public async Task<JsonElement> NextAsync(string request) => await Channel(request).Reader.ReadAsync(TestContext.Current.CancellationToken);

    private Channel<JsonElement> Channel(string request) => _frames.GetOrAdd(request, _ => System.Threading.Channels.Channel.CreateUnbounded<JsonElement>());

    private async ValueTask<ReadOnlyMemory<byte>> HandleAsync(ZabbixRequest request, CancellationToken cancellationToken)
    {
        JsonElement frame;
        using (JsonDocument document = JsonDocument.Parse(request.Payload)) frame = document.RootElement.Clone();
        string kind = frame.GetProperty("request").GetString()!;
        int nth = _seen.AddOrUpdate(kind, 1, (_, count) => count + 1) - 1;
        Channel(kind).Writer.TryWrite(frame);
        string reply = await _script(kind, nth, frame).WaitAsync(cancellationToken);
        return reply.Length == 0 ? ReadOnlyMemory<byte>.Empty : Encoding.UTF8.GetBytes(reply);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (Channel<JsonElement> channel in _frames.Values) channel.Writer.TryComplete();
        await _listener.StoppingAsync(default);
        await _listener.StopAsync(default);
        await _listener.StoppedAsync(default);
        await _listener.DisposeAsync();
    }
}
