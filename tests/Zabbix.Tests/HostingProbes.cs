// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Community.Zabbix.Tests;

/// <summary>
/// A source whose consumer can be watched: every time the consumer asks for an item and none is ready, a wait is
/// signalled on <see cref="Waits"/>, so a test knows the consumer is idle on the source (wait for the
/// condition, never for a duration). It is an async iterator, like the store's own stream, so disposing it with a read
/// pending throws <see cref="NotSupportedException"/> just as <c>ReadAllAsync</c> does.
/// </summary>
internal sealed class ProbeSource<T> : IAsyncEnumerable<T>
{
    private readonly Channel<T> _items = Channel.CreateUnbounded<T>();
    private readonly Channel<bool> _waits = Channel.CreateUnbounded<bool>();
    private int _moves;

    /// <summary>One entry per time the consumer found nothing ready and started waiting.</summary>
    public ChannelReader<bool> Waits => _waits.Reader;

    /// <summary>How many times the consumer asked for the next item.</summary>
    public int Moves => Volatile.Read(ref _moves);

    public void Write(T item) => _items.Writer.TryWrite(item);

    public void Complete() => _items.Writer.TryComplete();

    public async IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Interlocked.Increment(ref _moves);
            T item;
            while (!_items.Reader.TryRead(out item!))
            {
                _waits.Writer.TryWrite(true);
                if (!await _items.Reader.WaitToReadAsync(cancellationToken)) yield break;
            }
            yield return item;
        }
    }
}

/// <summary>
/// A fake clock that says when a timer is created on it, so a test can advance time only once the component under test
/// is actually waiting: advanced earlier, time can move past a timer that does not exist yet.
/// </summary>
internal sealed class WatchedClock : FakeTimeProvider
{
    private readonly Channel<TimeSpan> _created = Channel.CreateUnbounded<TimeSpan>();

    /// <summary>The due time of every timer created, in creation order.</summary>
    public ChannelReader<TimeSpan> Created => _created.Reader;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ITimer timer = base.CreateTimer(callback, state, dueTime, period);
        _created.Writer.TryWrite(dueTime);
        return timer;
    }

    /// <summary>Waits until a timer due in exactly <paramref name="dueTime"/> is created.</summary>
    public async Task WaitForTimerAsync(TimeSpan dueTime, CancellationToken cancellationToken = default)
    {
        await foreach (TimeSpan created in _created.Reader.ReadAllAsync(cancellationToken))
            if (created == dueTime) return;
    }

    /// <summary>
    /// Advances the clock by <paramref name="step"/> each time something new is waiting on it, until <paramref name="until"/>
    /// completes: event-driven stepping, for flows whose timers are created at moments the test cannot name (a jittered
    /// retry, a refresh). The clock only moves after a timer was created, never on a real-time guess.
    /// </summary>
    public async Task StepUntilAsync(Task until, TimeSpan step, CancellationToken cancellationToken = default)
    {
        while (!until.IsCompleted)
        {
            Advance(step);
            Task<bool> created = _created.Reader.WaitToReadAsync(cancellationToken).AsTask();
            await Task.WhenAny(until, created);
            while (_created.Reader.TryRead(out _)) { }
        }
        await until;
    }
}

/// <summary>
/// A logger provider that makes log events awaitable: <see cref="WaitForAsync"/> completes when an event with the id is
/// logged (or was already), so a test waits for the failure path it asserts instead of sleeping and reading a snapshot.
/// </summary>
internal sealed class LogProbe : Microsoft.Extensions.Logging.ILoggerProvider
{
    private readonly List<(int Id, Microsoft.Extensions.Logging.LogLevel Level, Exception? Exception)> _events = [];
    private readonly List<(int Id, TaskCompletionSource Seen)> _waiters = [];

    public IReadOnlyList<(int Id, Microsoft.Extensions.Logging.LogLevel Level, Exception? Exception)> Events
    {
        get { lock (_events) return [.. _events]; }
    }

    public Task WaitForAsync(int id)
    {
        lock (_events)
        {
            if (_events.Exists(entry => entry.Id == id)) return Task.CompletedTask;
            var seen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((id, seen));
            return seen.Task;
        }
    }

    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName) => new Logger(this);

    public void Dispose()
    {
    }

    private void Add(int id, Microsoft.Extensions.Logging.LogLevel level, Exception? exception)
    {
        lock (_events)
        {
            _events.Add((id, level, exception));
            foreach ((int Id, TaskCompletionSource Seen) waiter in _waiters.Where(waiter => waiter.Id == id)) waiter.Seen.TrySetResult();
            _waiters.RemoveAll(waiter => waiter.Id == id);
        }
    }

    private sealed class Logger(LogProbe probe) : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => probe.Add(eventId.Id, logLevel, exception);
    }
}
