// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Hosting;

/// <summary>
/// Provides the one fault contract of an agent's loops, written once instead of a filter copied into every loop.
/// Two kinds of failure exist in a pass of a loop (a refresh, a round of checks, a delivery):
/// <list type="number">
/// <item>the exchange with the server failed (<see cref="IsExchangeFailure"/>): expected, answered by the loop's own
/// contract (retry the batch, keep the item list) and logged by the loop's own Warning event;</item>
/// <item>anything else: nothing translated it, so it is a defect. <see cref="PassAsync"/> logs it at Error (3003), shows it
/// on the agent's <see cref="ZabbixAgentStatus.Fault"/> (so its health says so) until the loop's next good pass, and lets
/// the cadence continue: one bad pass must not stop the loop.</item>
/// </list>
/// The loop's own stop is neither: it propagates, because cancellation is not an error.
/// </summary>
internal static class ZabbixAgentLoop
{
    /// <summary>
    /// Whether <paramref name="failure"/> is the exchange with a server failing: the wire's own contract
    /// (<see cref="Protocol.ZabbixFrames.IsExchangeFailure"/>: the network, the peer, TLS, a frame that is not one, a
    /// deadline), plus a reply that is not the protocol's (<see cref="JsonException"/>) and a cancellation that is not
    /// <paramref name="stopping"/> (a deadline inside the exchange).
    /// </summary>
    /// <param name="failure">What was thrown.</param>
    /// <param name="stopping">The loop's own stop.</param>
    public static bool IsExchangeFailure(Exception failure, CancellationToken stopping) => failure switch
    {
        OperationCanceledException => !stopping.IsCancellationRequested,
        JsonException => true,
        _ => Protocol.ZabbixFrames.IsExchangeFailure(failure),
    };

    /// <summary>Runs one pass of <paramref name="loop"/>; a failure its own contract did not translate is logged, shown on the status, and does not end the loop.</summary>
    /// <param name="agent">The agent's name.</param>
    /// <param name="loop">The loop's name (<c>refresh</c>, <c>checks</c>, <c>send</c>).</param>
    /// <param name="status">Where the fault shows.</param>
    /// <param name="logger">Where it is logged.</param>
    /// <param name="pass">The pass.</param>
    /// <param name="stopping">The loop's stop, which propagates.</param>
    public static async Task PassAsync(string agent, string loop, ZabbixAgentStatus status, ILogger logger, Func<CancellationToken, Task> pass, CancellationToken stopping)
    {
        try
        {
            await pass(stopping).ConfigureAwait(false);
            status.Recovered(loop);
        }
        catch (Exception failure) when (!(failure is OperationCanceledException && stopping.IsCancellationRequested))
        {
            AgentLog.AgentLoopFaulted(logger, agent, loop, failure);
            status.Faulted(loop, failure);
        }
    }
}
