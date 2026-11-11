// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Threading.Channels;
using Community.Zabbix.Storage;

namespace Community.Zabbix.Hosting;

/// <summary>
/// Writes one value to every destination: the writer an agent with several <c>ServerActive</c> destinations pumps into.
/// Each store keeps its own policy (its drops are its own, counted by it); the value is taken when any store took it.
/// A write offers the value to every store without waiting first and then waits only on the stores that could not take it
/// now, together, so one full destination never keeps a value from the others. Owned because no BCL writer broadcasts:
/// a <see cref="Channel"/> has one reader per item, and the Dataflow <c>BroadcastBlock</c> keeps only the latest item for
/// a slow target, which is a drop policy the stores already state.
/// </summary>
internal sealed class ZabbixFanOutWriter<T>(IReadOnlyList<ChannelWriter<T>> writers) : ChannelWriter<T>
{
    public override bool TryWrite(T item)
    {
        bool taken = false;
        foreach (ChannelWriter<T> writer in writers) taken |= Take(writer, item);
        return taken;
    }

    public override ValueTask WriteAsync(T item, CancellationToken cancellationToken = default)
    {
        List<ChannelWriter<T>>? waiting = null;
        foreach (ChannelWriter<T> writer in writers)
            if (!Take(writer, item)) (waiting ??= []).Add(writer);
        return waiting is null ? ValueTask.CompletedTask : new ValueTask(Task.WhenAll(waiting.Select(writer => writer.WriteAsync(item, cancellationToken).AsTask())));
    }

    public override async ValueTask<bool> WaitToWriteAsync(CancellationToken cancellationToken = default)
    {
        foreach (ChannelWriter<T> writer in writers)
            if (!await writer.WaitToWriteAsync(cancellationToken).ConfigureAwait(false)) return false;
        return true;
    }

    public override bool TryComplete(Exception? error = null)
    {
        bool completed = false;
        foreach (ChannelWriter<T> writer in writers) completed |= writer.TryComplete(error);
        return completed;
    }

    // A store says "taken" for a record its policy dropped, so a refused record is not offered to it a second time.
    private static bool Take(ChannelWriter<T> writer, T item) => writer is IZabbixTakingWriter<T> taking ? taking.TryTake(item) : writer.TryWrite(item);
}
