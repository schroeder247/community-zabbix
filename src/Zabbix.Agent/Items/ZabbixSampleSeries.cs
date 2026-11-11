// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Items;

/// <summary>
/// The samples of one thing a bundled key measures over time (a processor, a process query, a block device): the last
/// fifteen minutes of its counters, one sample a second, as Zabbix's collector keeps them
/// (<c>ZBX_MAX_COLLECTOR_HISTORY</c>).
/// </summary>
/// <param name="width">How many counters each sample holds.</param>
/// <param name="time">The clock samples are stamped on.</param>
/// <param name="state">What the series' family keeps beside the samples.</param>
internal sealed class ZabbixSampleSeries(int width, TimeProvider time, object? state)
{
    private const int History = (15 * 60) + 1;

    // A sample less than half a period after the last is not one: a tick that lands just after a series' first sample.
    private static readonly TimeSpan s_closest = TimeSpan.FromSeconds(0.5);

    private readonly Lock _gate = new();
    private readonly ulong[] _counters = new ulong[History * width];
    private readonly long[] _stamps = new long[History];
    private readonly bool[] _valid = new bool[History];
    private readonly TaskCompletionSource _twoSamples = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _first;
    private int _count;
    private int _validCount;
    private long _lastAsked;

    /// <summary>What the series' family keeps beside the samples.</summary>
    public object? State { get; } = state;

    /// <summary>Completes when the series holds two samples, the fewest a difference is taken from.</summary>
    public Task TwoSamples => _twoSamples.Task;

    /// <summary>When a key last asked for the series.</summary>
    public long LastAsked
    {
        get => Volatile.Read(ref _lastAsked);
        set => Volatile.Write(ref _lastAsked, value);
    }

    /// <summary>Whether the series' latest sample could not be taken, or none has been.</summary>
    public bool LatestFailed
    {
        get
        {
            lock (_gate) return _count == 0 || !_valid[(_first + _count - 1) % History];
        }
    }

    /// <summary>Appends a sample stamped <paramref name="stamp"/>, or a failed one when <paramref name="counters"/> is empty.</summary>
    public void Add(long stamp, ReadOnlySpan<ulong> counters)
    {
        lock (_gate)
        {
            if (_count > 0 && time.GetElapsedTime(_stamps[(_first + _count - 1) % History], stamp) < s_closest) return;
            int index = (_first + _count) % History;
            if (_count == History)
            {
                if (_valid[_first]) _validCount--;
                _first = (_first + 1) % History;
            }
            else
            {
                _count++;
            }
            _stamps[index] = stamp;
            _valid[index] = !counters.IsEmpty;
            if (counters.IsEmpty) return;
            counters.CopyTo(_counters.AsSpan(index * width, width));
            if (++_validCount >= 2) _twoSamples.TrySetResult();
        }
    }

    /// <summary>
    /// The latest sample and the one <paramref name="seconds"/> samples before it, or the oldest when the series holds
    /// fewer; a failed sample is passed over (<c>get_cpustat</c>). <see langword="null"/> when the latest sample failed.
    /// </summary>
    public (ulong[] Latest, ulong[] Start, TimeSpan Elapsed)? Window(int seconds)
    {
        lock (_gate)
        {
            if (_count == 0) return null;
            int latest = (_first + _count - 1) % History;
            if (!_valid[latest]) return null;
            int start = (latest - Math.Min(_count - 1, seconds) + History) % History;
            while (!_valid[start]) start = (start + 1) % History;
            return (_counters.AsSpan(latest * width, width).ToArray(), _counters.AsSpan(start * width, width).ToArray(), time.GetElapsedTime(_stamps[start], _stamps[latest]));
        }
    }
}
