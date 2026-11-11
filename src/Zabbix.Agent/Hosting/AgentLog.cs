// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Hosting;

/// <summary>
/// Provides the agent layer's log events. Id ranges:
/// <list type="bullet">
/// <item><c>30xx</c> an agent's pump and its loops, and a poll source's cadence;</item>
/// <item><c>31xx</c> the send loop's delivery outcomes: retries, rejections, faults, holds and returns;</item>
/// <item><c>32xx</c> the active agent's conversation with its server;</item>
/// <item><c>33xx</c> the runner that hosts them without an application host.</item>
/// </list>
/// Counts and durations are not here: they are the <see cref="ZabbixAgentTelemetry"/> instruments.
/// </summary>
internal static partial class AgentLog
{
    // ----- 30xx: an agent and its loops ---------------------------------------------------------------------------------

    /// <summary>An agent started pumping its sources.</summary>
    [LoggerMessage(EventId = 3000, Level = LogLevel.Information, Message = "Agent started with {SourceCount} source(s)")]
    public static partial void AgentStarted(ILogger logger, int sourceCount);

    /// <summary>Every source ended; the store is completed so a downstream sender drains and stops.</summary>
    [LoggerMessage(EventId = 3001, Level = LogLevel.Debug, Message = "All sources drained; completing storage")]
    public static partial void AgentSourcesDrained(ILogger logger);

    /// <summary>One source faulted; the others keep running.</summary>
    [LoggerMessage(EventId = 3002, Level = LogLevel.Error, Message = "A source faulted; the agent's other sources continue")]
    public static partial void AgentSourceFailed(ILogger logger, Exception exception);

    /// <summary>
    /// One pass of an agent loop threw something no contract names (a defect, not a network failure): logged, shown by
    /// the agent's health until the loop's next good pass, and the cadence continues.
    /// </summary>
    [LoggerMessage(EventId = 3003, Level = LogLevel.Error, Message = "Agent {Agent}: the {Loop} loop faulted; it continues on its cadence")]
    public static partial void AgentLoopFaulted(ILogger logger, string agent, string loop, Exception exception);

    /// <summary>A poll source started its cadence.</summary>
    [LoggerMessage(EventId = 3004, Level = LogLevel.Debug, Message = "Poll started (interval {IntervalSeconds} s)")]
    public static partial void PollStarted(ILogger logger, double intervalSeconds);

    /// <summary>One poll cycle failed; the tick is skipped and the cadence kept: a tick is never retried.</summary>
    [LoggerMessage(EventId = 3005, Level = LogLevel.Error, Message = "Poll cycle failed; skipping this tick")]
    public static partial void PollCycleFailed(ILogger logger, Exception exception);

    /// <summary>A poll's reader is losing records to the poll's buffer: logged at the 1st, 10th, 100th, … drop.</summary>
    [LoggerMessage(EventId = 3006, Level = LogLevel.Warning, Message = "Poll has dropped {Dropped} record(s): its reader is slower than the cadence")]
    public static partial void PollDropping(ILogger logger, long dropped);

    /// <summary>A poll stopped with records collected that its reader never took: they are gone with the poll.</summary>
    [LoggerMessage(EventId = 3007, Level = LogLevel.Warning, Message = "Poll stopped with {Count} collected record(s) its reader never took")]
    public static partial void PollAbandoned(ILogger logger, int count);

    /// <summary>The pump's drain window closed on values a source still held, or on the value in hand: they never reached the store.</summary>
    [LoggerMessage(EventId = 3008, Level = LogLevel.Warning, Message = "The agent abandoned {Count} value(s) at stop: the drain window closed before they were written to the store")]
    public static partial void AgentAbandoned(ILogger logger, int count);

    // ----- 31xx: the send loop's outcomes --------------------------------------------------------------------------------

    /// <summary>The destination threw instead of answering: the batch is kept and retried like a transient failure.</summary>
    [LoggerMessage(EventId = 3100, Level = LogLevel.Error, Message = "Delivery of {Count} value(s) threw (attempt {Attempt}); the batch is kept and retried")]
    public static partial void DeliveryFaulted(ILogger logger, int count, int attempt, Exception exception);

    /// <summary>The destination paused uploads (Zabbix's <c>upload: disabled</c>): the batch is held, not failed.</summary>
    [LoggerMessage(EventId = 3101, Level = LogLevel.Information, Message = "Delivery of {Count} value(s) held: the destination has paused uploads")]
    public static partial void DeliveryHeld(ILogger logger, int count);

    /// <summary>A stopped loop handed undelivered values back to its source, which keeps them.</summary>
    [LoggerMessage(EventId = 3102, Level = LogLevel.Information, Message = "{Count} value(s) returned to the source at stop, undelivered")]
    public static partial void DeliveryReturned(ILogger logger, int count);

    /// <summary>A delivery handler threw something other than an exchange failure on every attempt: its own defect. The batch is rejected (3105) so the loop goes on.</summary>
    [LoggerMessage(EventId = 3103, Level = LogLevel.Error, Message = "Delivery of {Count} value(s) threw on all {Attempts} attempt(s), not an exchange failure; the batch is rejected")]
    public static partial void DeliveryDefect(ILogger logger, int count, int attempts, Exception exception);

    /// <summary>A batch was not delivered and will be retried; the loop holds it.</summary>
    [LoggerMessage(EventId = 3104, Level = LogLevel.Warning, Message = "Delivery of {Count} value(s) not acknowledged (attempt {Attempt}); retrying in {DelaySeconds:F1} s")]
    public static partial void DeliveryRetrying(ILogger logger, int count, int attempt, double delaySeconds);

    /// <summary>A batch was refused for good and is dropped.</summary>
    [LoggerMessage(EventId = 3105, Level = LogLevel.Warning, Message = "Delivery of {Count} value(s) rejected; dropping the batch")]
    public static partial void DeliveryRejected(ILogger logger, int count);

    /// <summary>A stopped loop could neither deliver nor hand back what it had taken within the drain window: lost.</summary>
    [LoggerMessage(EventId = 3106, Level = LogLevel.Warning, Message = "Delivery of {Count} value(s) abandoned at stop: the drain window passed and the source cannot take them back")]
    public static partial void DeliveryAbandoned(ILogger logger, int count);

    /// <summary>A source's reader, released after the stop because a read was still pending, faulted when it was released; nobody awaited it.</summary>
    [LoggerMessage(EventId = 3107, Level = LogLevel.Warning, Message = "A source's reader faulted when it was released after the stop")]
    public static partial void SourceReleaseFaulted(ILogger logger, Exception exception);

    // ----- 32xx: the active agent ---------------------------------------------------------------------------------------

    /// <summary>The server handed the agent its item list.</summary>
    [LoggerMessage(EventId = 3200, Level = LogLevel.Information, Message = "Active agent {Agent} received {Count} item(s), revision {Revision}")]
    public static partial void ActiveChecksReceived(ILogger logger, string agent, int count, long revision);

    /// <summary>The server answered the item request with something other than success.</summary>
    [LoggerMessage(EventId = 3201, Level = LogLevel.Warning, Message = "Active agent {Agent}: the server refused the active checks request: {Reason}")]
    public static partial void ActiveChecksRefused(ILogger logger, string agent, string reason);

    /// <summary>The item request did not reach the server, or its answer was not one; the held list stays.</summary>
    [LoggerMessage(EventId = 3202, Level = LogLevel.Warning, Message = "Active agent {Agent} could not refresh its items; keeping the list held")]
    public static partial void ActiveChecksFailed(ILogger logger, string agent, Exception exception);

    /// <summary>The server answered a batch with something other than success: the batch is kept and retried, as upstream's agents do.</summary>
    [LoggerMessage(EventId = 3203, Level = LogLevel.Warning, Message = "Active agent {Agent}: the server did not accept agent data; the batch is kept and retried: {Reason}")]
    public static partial void AgentDataRefused(ILogger logger, string agent, string reason);

    /// <summary>A heartbeat did not reach the server.</summary>
    [LoggerMessage(EventId = 3204, Level = LogLevel.Warning, Message = "Active agent {Agent}: heartbeat failed")]
    public static partial void HeartbeatFailed(ILogger logger, string agent, Exception exception);

    /// <summary>An assigned item's delay is not a Zabbix interval: the item is reported not supported and not scheduled.</summary>
    [LoggerMessage(EventId = 3205, Level = LogLevel.Warning, Message = "Active agent {Agent}: item {ItemId} ({Key}) cannot be scheduled: {Reason}")]
    public static partial void ItemDelayInvalid(ILogger logger, string agent, ulong itemId, [ZabbixItemKeyData] string key, string reason);

    /// <summary>The server's word on history upload changed (<c>upload: disabled</c> pauses it, anything else resumes it).</summary>
    [LoggerMessage(EventId = 3206, Level = LogLevel.Information, Message = "Active agent {Agent}: the server has {State} history upload")]
    public static partial void UploadStateChanged(ILogger logger, string agent, string state);

    /// <summary>An item with no handler, or whose handler failed, is reported to the server as not supported.</summary>
    [LoggerMessage(EventId = 3207, Level = LogLevel.Debug, Message = "Active agent {Agent}: {Key} is not supported: {Reason}")]
    public static partial void CheckNotSupported(ILogger logger, string agent, [ZabbixItemKeyData] ZabbixItemKey key, string reason);

    /// <summary>The item list repeats an item id; the last row is taken, as upstream's <c>add_check</c> merges it.</summary>
    [LoggerMessage(EventId = 3208, Level = LogLevel.Warning, Message = "Active agent {Agent}: the item list repeats item {ItemId} ({Repeats} repeated row(s)); the last row of each id is taken")]
    public static partial void ActiveChecksRepeatItem(ILogger logger, string agent, ulong itemId, int repeats);

    /// <summary>The wall clock went back; the check schedule moves with it (upstream active_checks.c <c>update_schedule</c>).</summary>
    [LoggerMessage(EventId = 3209, Level = LogLevel.Warning, Message = "Active agent {Agent}: the system time has been pushed back by {Step}; adjusting the check schedule")]
    public static partial void ClockSteppedBack(ILogger logger, string agent, TimeSpan step);

    /// <summary>A check finished after the store was completed (the agent is stopping): its value is not kept.</summary>
    [LoggerMessage(EventId = 3210, Level = LogLevel.Debug, Message = "Active agent {Agent}: item {ItemId} was collected after the store closed; the value is not kept")]
    public static partial void CollectedAfterClose(ILogger logger, string agent, ulong itemId);

    /// <summary>An item handler threw; the check answered not supported with the exception's message. Logged once per key name.</summary>
    [LoggerMessage(EventId = 3211, Level = LogLevel.Warning, Message = "The handler for item key {KeyName} threw; the check is answered not supported with its message")]
    public static partial void ItemHandlerFaulted(ILogger logger, string keyName, Exception exception);

    // ----- 33xx: the runner -------------------------------------------------------------------------------------------

    /// <summary>The lifecycle of a component (<see cref="ZabbixComponent"/>).</summary>
    [LoggerMessage(EventId = 3010, Level = LogLevel.Warning, Message = "Instrument {Instrument} of meter {Meter} is a histogram, which has no one value to send; it is left out")]
    public static partial void MeterHistogramLeftOut(ILogger logger, string instrument, string meter);

    [LoggerMessage(EventId = 3011, Level = LogLevel.Warning, Message = "Instrument {Instrument} of meter {Meter} has a name or tag values an item key can't hold; it is left out")]
    public static partial void MeterKeyInvalid(ILogger logger, string instrument, string meter);

    [LoggerMessage(EventId = 3300, Level = LogLevel.Debug, Message = "{Component} started")]
    public static partial void ComponentStarted(ILogger logger, string component);

    /// <summary>The lifecycle of a component (<see cref="ZabbixComponent"/>).</summary>
    [LoggerMessage(EventId = 3301, Level = LogLevel.Debug, Message = "{Component} failed to start; stopping what started")]
    public static partial void ComponentStartFailed(ILogger logger, string component, Exception exception);

    /// <summary>After a failed start, a service that had started failed to stop; the start's failure is the one thrown.</summary>
    [LoggerMessage(EventId = 3302, Level = LogLevel.Error, Message = "{Component} failed to stop after its start failed")]
    public static partial void ComponentRollbackStopFailed(ILogger logger, string component, Exception exception);

    /// <summary>The lifecycle of a component (<see cref="ZabbixComponent"/>).</summary>
    [LoggerMessage(EventId = 3303, Level = LogLevel.Debug, Message = "{Component} stopped; {FailureCount} phase(s) failed")]
    public static partial void ComponentStopped(ILogger logger, string component, int failureCount);

    /// <summary>A hosted service failed to stop; the others are still stopped, and the caller gets the failure.</summary>
    [LoggerMessage(EventId = 3304, Level = LogLevel.Debug, Message = "{Component} failed in a phase of its stop")]
    public static partial void ComponentStopFailed(ILogger logger, string component, Exception exception);

    /// <summary>The lifecycle of a component (<see cref="ZabbixComponent"/>).</summary>
    [LoggerMessage(EventId = 3305, Level = LogLevel.Error, Message = "{Component} failed to stop while it was disposed")]
    public static partial void ComponentDisposeStopFailed(ILogger logger, string component, Exception exception);
}
