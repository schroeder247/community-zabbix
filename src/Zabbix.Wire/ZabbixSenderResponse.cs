// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;

namespace Community.Zabbix;

/// <summary>
/// The trapper's successful reply, its <c>info</c> counters parsed (<c>processed: N; failed: N; total: N; seconds spent:
/// X</c>, <c>src/libs/zbxtrapper/trapper.c</c>). Only a success is a response; anything else is a
/// <see cref="ZabbixTrapperRefusedException"/>, or, for a redirect the exchange did not follow, an <see cref="IOException"/>. A
/// send of several frames answers with the sum of their replies: the counters add up, <see cref="Frames"/> says how many
/// there were, and <see cref="Info"/> reads as one reply of the server's.
/// </summary>
/// <param name="Processed">Values the server took.</param>
/// <param name="Failed">Values the server refused (an unknown host or key, a wrong type, a denied <c>trapper_hosts</c>).</param>
/// <param name="Total">Values in the request.</param>
/// <param name="TimeSpent">How long the server took over the values, its own timing (<c>seconds spent</c>).</param>
/// <param name="Info">The raw <c>info</c> line; for several frames, the summed counters written in the server's format.</param>
public sealed record ZabbixSenderResponse(int Processed, int Failed, int Total, TimeSpan TimeSpent, string Info)
{
    /// <summary>Gets how many frames, each answered by the server, this answer sums; 1 for one reply.</summary>
    public int Frames { get; init; } = 1;

    /// <summary>
    /// Gets a value that indicates whether the server refused any value while it took the request (zabbix_sender's
    /// <c>SUCCEED_PARTIAL</c>): some, or all of them when <see cref="Processed"/> is 0.
    /// </summary>
    public bool HasFailures => Failed > 0;

    /// <summary>The answers of several frames as one: one frame's answer as it is, several summed.</summary>
    /// <param name="frames">The frames' answers, at least one.</param>
    internal static ZabbixSenderResponse Sum(IReadOnlyList<ZabbixSenderResponse> frames)
    {
        if (frames.Count == 1) return frames[0];
        int processed = frames.Sum(frame => frame.Processed), failed = frames.Sum(frame => frame.Failed), total = frames.Sum(frame => frame.Total);
        TimeSpan spent = TimeSpan.FromTicks(frames.Sum(frame => frame.TimeSpent.Ticks));
        string info = string.Create(CultureInfo.InvariantCulture, $"processed: {processed}; failed: {failed}; total: {total}; seconds spent: {spent.TotalSeconds:0.000000}");
        return new ZabbixSenderResponse(processed, failed, total, spent, info) { Frames = frames.Sum(frame => frame.Frames) };
    }
}
