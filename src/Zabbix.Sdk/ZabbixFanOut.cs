// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Threading.Channels;

namespace Community.Zabbix;

/// <summary>
/// Broadcasts each value to every attached channel. The set of channels is a copy-on-write array: attaching and detaching
/// replace it under a lock, and a write reads it once with <see cref="Volatile.Read{T}(ref readonly T)"/>, with no lock and
/// no allocation, so the per-value cost does not depend on how often readers come and go.
/// </summary>
/// <typeparam name="T">The value type.</typeparam>
internal sealed class ZabbixFanOut<T>
{
    private readonly Lock _gate = new();
    private Channel<T>[] _channels = [];

    /// <summary>How many channels are attached.</summary>
    public int Count => Volatile.Read(ref _channels).Length;

    /// <summary>Attaches a channel; it receives every value written from now on.</summary>
    public void Attach(Channel<T> channel)
    {
        lock (_gate) Volatile.Write(ref _channels, [.. _channels, channel]);
    }

    /// <summary>Detaches a channel; it receives nothing more.</summary>
    public void Detach(Channel<T> channel)
    {
        lock (_gate) Volatile.Write(ref _channels, Array.FindAll(_channels, attached => attached != channel));
    }

    /// <summary>Offers a value to every attached channel.</summary>
    /// <remarks>
    /// A dropping bounded channel takes every write (the shed value is counted by its drop callback); a write fails only
    /// once the reader left and completed the channel, when there is nobody to give the value to.
    /// </remarks>
    public void Write(T value)
    {
        foreach (Channel<T> channel in Volatile.Read(ref _channels)) _ = channel.Writer.TryWrite(value);
    }

    /// <summary>Completes every attached channel.</summary>
    public void Complete()
    {
        foreach (Channel<T> channel in Volatile.Read(ref _channels)) _ = channel.Writer.TryComplete();   // false: the reader left first
    }
}
