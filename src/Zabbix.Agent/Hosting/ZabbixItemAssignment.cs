// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json.Serialization;

namespace Community.Zabbix.Hosting;

/// <summary>
/// One item the server assigns: its id, its key and delay as the server resolved them, its timeout, and where a log item
/// left off. <c>lastlogsize</c> and <c>mtime</c> are always sent (<c>active.c</c>: "The agent expects ALWAYS to have
/// lastlogsize and mtime tags"): a C agent skips an item without <c>lastlogsize</c> and Agent 2 refuses the whole list
/// without either (<c>active_checks.c</c> 7.0:577-582, <c>serverconnector.go</c> 8.0:278-290). The delay is a
/// <see cref="ZabbixInterval"/> and the timeout a <see cref="ZabbixDuration"/>, each kept as the server wrote it, and the
/// key stays the server's text, not a <see cref="ZabbixItemKey"/>: the agent reads each when it schedules the item, so one
/// it cannot read is that item's "not supported", as upstream's agent answers it, and never a refusal of the whole list.
/// </summary>
public sealed record ZabbixItemAssignment
{
    /// <summary>Creates the assignment of an item that is not a log item: nothing read yet.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="key">The key, macros resolved.</param>
    /// <param name="delay">The update interval, macros resolved.</param>
    /// <param name="timeout">The check timeout; <see langword="null"/> leaves the agent's own <c>Timeout</c>.</param>
    public ZabbixItemAssignment(ulong itemId, string key, ZabbixInterval delay, ZabbixDuration? timeout)
        : this(itemId, key, delay, timeout, lastLogSize: 0, mtime: 0)
    {
    }

    /// <summary>Creates the assignment of an item with where its log has been read to.</summary>
    /// <param name="itemId">The item.</param>
    /// <param name="key">The key, macros resolved.</param>
    /// <param name="delay">The update interval, macros resolved.</param>
    /// <param name="timeout">The check timeout; <see langword="null"/> leaves the agent's own <c>Timeout</c>.</param>
    /// <param name="lastLogSize">How far into its log a log item has read; 0 otherwise.</param>
    /// <param name="mtime">The log file's modification time a log item has read to; 0 otherwise.</param>
    [JsonConstructor]
    public ZabbixItemAssignment(ulong itemId, string key, ZabbixInterval delay, ZabbixDuration? timeout, long lastLogSize, int mtime)
    {
        ItemId = itemId;
        Key = key;
        Delay = delay;
        Timeout = timeout;
        LastLogSize = lastLogSize;
        Mtime = mtime;
    }

    /// <summary>The item.</summary>
    [JsonPropertyName("itemid")]
    public ulong ItemId { get; init; }

    /// <summary>The key, macros resolved.</summary>
    [JsonPropertyName("key")]
    public string Key { get; init; }

    /// <summary>The update interval, macros resolved.</summary>
    [JsonPropertyName("delay")]
    public ZabbixInterval Delay { get; init; }

    /// <summary>The check timeout; absent leaves the agent's own <c>Timeout</c>.</summary>
    [JsonPropertyName("timeout")]
    public ZabbixDuration? Timeout { get; init; }

    /// <summary>How far into its log a log item has read; 0 otherwise.</summary>
    [JsonPropertyName("lastlogsize")]
    public long LastLogSize { get; init; }

    /// <summary>The log file's modification time a log item has read to; 0 otherwise.</summary>
    [JsonPropertyName("mtime")]
    public int Mtime { get; init; }
}
