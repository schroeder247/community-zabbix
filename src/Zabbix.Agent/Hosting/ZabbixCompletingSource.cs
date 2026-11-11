// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Threading.Channels;

namespace Community.Zabbix.Hosting;

/// <summary>
/// Represents a source that ends by completion, not by the agent's stop: a channel's reader, such as a trapper's edge. What
/// is in the channel was already promised to someone (a trapper told its senders "processed"), so at stop the agent's
/// pump reads it to its end, which comes when the producer completes the channel (the trapper stops first: hosted
/// services stop in reverse registration order), bounded by the drain window. Every other source is stopped at once.
/// Built by a push agent builder's <c>AddSource</c> that takes a channel's reader, and by its trapper.
/// </summary>
/// <typeparam name="T">The record.</typeparam>
/// <param name="reader">The channel's reader.</param>
internal sealed class ZabbixCompletingSource<T>(ChannelReader<T> reader) : IAsyncEnumerable<T>
{
    // The token reaches the channel's iterator once, through ReadAllAsync; handing it to its enumerator as well would link it twice.
    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => reader.ReadAllAsync(cancellationToken).GetAsyncEnumerator(CancellationToken.None);

    /// <summary>Takes what the channel still holds and answers how many records that was: the pump's drain window closed on them.</summary>
    public int Discard()
    {
        int count = 0;
        while (reader.TryRead(out _)) count++;
        return count;
    }
}
