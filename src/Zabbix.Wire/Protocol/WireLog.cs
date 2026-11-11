// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Protocol;

/// <summary>
/// Provides the framed-TCP wire's log events. Ids:
/// the wire's block is 1000–1999: 1000–1099 the sender, 1100–1199 the exchange (connect, TLS, frames, deadline,
/// failover), 1200–1299 the getter, 1300–1399 the server wire.
/// </summary>
internal static partial class WireLog
{
    // ----- 1000: the sender -----------------------------------------------------------------------------------

    [LoggerMessage(EventId = 1000, Level = LogLevel.Debug, Message = "Sending {Count} value(s) to {Destination}")]
    public static partial void SenderSending(ILogger logger, int count, string destination);

    /// <summary>A batch was taken. Debug: a sender sends a batch every second or more often, and its counts are the meter's.</summary>
    [LoggerMessage(EventId = 1001, Level = LogLevel.Debug, Message = "Sent {Total} value(s) to {Destination}: processed {Processed}, failed {Failed}")]
    public static partial void SenderSent(ILogger logger, int total, string destination, int processed, int failed);

    /// <summary>
    /// The trapper took the frame but refused some values (an unknown host or key, a wrong type): zabbix_sender's
    /// SUCCEED_PARTIAL. The counts are the server's; its <c>info</c> text, which only repeats them, stays on the response.
    /// </summary>
    [LoggerMessage(EventId = 1002, Level = LogLevel.Warning, Message = "Zabbix reported {Failed} of {Total} value(s) failed at {Destination}")]
    public static partial void SenderReportedFailures(ILogger logger, int failed, int total, string destination);

    /// <summary>A send failed; the exception is the caller's, who decides what it means and logs it at the level it chooses.</summary>
    [LoggerMessage(EventId = 1003, Level = LogLevel.Debug, Message = "Send to {Destination} failed")]
    public static partial void SenderFailed(ILogger logger, string destination, Exception exception);

    /// <summary>The trapper answered, but not with a success it could count: <c>failed</c>, or an <c>info</c> line that is not upstream's.</summary>
    [LoggerMessage(EventId = 1007, Level = LogLevel.Warning, Message = "{Destination} refused the values: response {Response}, info {Info}")]
    public static partial void SenderRefused(ILogger logger, string destination, [ZabbixPeerText] string response, [ZabbixPeerText] string? info);

    /// <summary>A proxy group sent the values elsewhere and the sender's exchange does not follow redirects; nothing was taken.</summary>
    [LoggerMessage(EventId = 1008, Level = LogLevel.Warning, Message = "{Destination} redirected the values ({Redirect}) and the sender's exchange does not follow redirects; nothing was taken")]
    public static partial void SenderRedirected(ILogger logger, string destination, string redirect);

    /// <summary>A send of several frames stopped at a failed one (logged above it) after earlier frames were taken.</summary>
    [LoggerMessage(EventId = 1009, Level = LogLevel.Warning, Message = "Sending to {Destination} stopped after {Frames} frame(s) were taken ({Taken} value(s)); {Unconfirmed} value(s) were not confirmed")]
    public static partial void SenderStopped(ILogger logger, string destination, int frames, int taken, int unconfirmed);

    // ----- 1100: the exchange ---------------------------------------------------------------------------------

    [LoggerMessage(EventId = 1100, Level = LogLevel.Debug, Message = "Frame → {Node}: {Bytes} byte(s), TLS {Tls}")]
    public static partial void FrameSending(ILogger logger, ZabbixServerNode node, int bytes, ZabbixTlsMode tls);

    [LoggerMessage(EventId = 1101, Level = LogLevel.Debug, Message = "Frame ← {Node}: {Bytes} byte(s) in {ElapsedSeconds:F3} s")]
    public static partial void FrameReceived(ILogger logger, ZabbixServerNode node, int bytes, double elapsedSeconds);

    /// <summary>A node could not be connected to (or its handshake failed); the next node is tried, and stays first.</summary>
    [LoggerMessage(EventId = 1102, Level = LogLevel.Warning, Message = "Node {Node} could not be connected ({ErrorType}); failing over to {Next}")]
    public static partial void NodeFailed(ILogger logger, ZabbixServerNode node, string errorType, ZabbixServerNode next, Exception exception);

    /// <summary>The peer's certificate was refused: which check, and what was compared with what.</summary>
    [LoggerMessage(EventId = 1103, Level = LogLevel.Warning, Message = "TLS peer {Host} refused: {Reason}")]
    public static partial void TlsPeerRefused(ILogger logger, string host, string reason);

    /// <summary>A reply was not a frame this reader accepts. Debug: the façade that made the exchange logs the failure, once.</summary>
    [LoggerMessage(EventId = 1104, Level = LogLevel.Debug, Message = "Frame from {Node} refused: {Reason}")]
    public static partial void FrameRefused(ILogger logger, ZabbixServerNode node, string reason);

    /// <summary>
    /// The exchange's deadline passed; the connection was closed. Debug: a node that fails over is reported by 1102, and a
    /// failed exchange by the façade that made it.
    /// </summary>
    [LoggerMessage(EventId = 1105, Level = LogLevel.Debug, Message = "Exchange with {Node} passed its {TimeoutSeconds:F1} s deadline")]
    public static partial void DeadlineExpired(ILogger logger, ZabbixServerNode node, double timeoutSeconds);

    /// <summary>
    /// The exchange failed after the frame was written: nothing is re-sent, and the next call starts at the next node.
    /// Debug: the façade that made the exchange (the sender, getter or server wire) logs the failure itself, once, at its
    /// own level; this says what the node list did about it.
    /// </summary>
    [LoggerMessage(EventId = 1106, Level = LogLevel.Debug, Message = "Exchange with {Node} failed after the request was written ({ErrorType}); not re-sent, the next exchange starts at {Next}")]
    public static partial void ExchangeFailed(ILogger logger, ZabbixServerNode node, string errorType, ZabbixServerNode next, Exception exception);

    [LoggerMessage(EventId = 1107, Level = LogLevel.Debug, Message = "Notification → {Node}: {Bytes} byte(s), TLS {Tls}")]
    public static partial void NotificationSent(ILogger logger, ZabbixServerNode node, int bytes, ZabbixTlsMode tls);

    /// <summary>A notification was answered with something that is not a redirect; upstream ignores it, and so does this.</summary>
    [LoggerMessage(EventId = 1108, Level = LogLevel.Debug, Message = "Notification to {Node} answered with {Bytes} byte(s) that are not a redirect; ignored")]
    public static partial void NotificationAnswered(ILogger logger, ZabbixServerNode node, int bytes);

    /// <summary>A proxy group moved the host: the request goes to the redirect's target, once, and the target stays first.</summary>
    [LoggerMessage(EventId = 1109, Level = LogLevel.Information, Message = "{Node} redirected the exchange to {Target} (revision {Revision}); sending there")]
    public static partial void Redirected(ILogger logger, ZabbixServerNode node, ZabbixServerNode target, ulong revision);

    /// <summary>A redirect reset: the node that answered goes behind the others and the request is sent again with failover.</summary>
    [LoggerMessage(EventId = 1110, Level = LogLevel.Information, Message = "{Node} reset its redirect; sending again from {Next}")]
    public static partial void RedirectReset(ILogger logger, ZabbixServerNode node, ZabbixServerNode next);

    /// <summary>A redirect was not followed: a second one in one exchange, or older than the one followed. Nothing was taken.</summary>
    [LoggerMessage(EventId = 1111, Level = LogLevel.Warning, Message = "{Node} redirected the exchange ({Redirect}) and it was not followed: {Reason}")]
    public static partial void RedirectRefused(ILogger logger, ZabbixServerNode node, string redirect, string reason);

    /// <summary>
    /// After a notification, listening for a redirect ended without one: the peer closed, the deadline passed, or the
    /// bytes it sent are not a frame. Upstream ignores whatever a heartbeat's peer sends back once the notify itself
    /// succeeded, so this is not a failed exchange — a Debug event ties the reason to the connection without raising it.
    /// </summary>
    [LoggerMessage(EventId = 1112, Level = LogLevel.Debug, Message = "{Node}: no redirect after the notification ({ErrorType})")]
    public static partial void NotifyRedirectNotOffered(ILogger logger, ZabbixServerNode node, string errorType);

    // ----- 1200: the getter -----------------------------------------------------------------------------------

    [LoggerMessage(EventId = 1200, Level = LogLevel.Debug, Message = "Agent {Destination} answered {Key}: supported {Supported}")]
    public static partial void GetterAnswered(ILogger logger, string destination, [ZabbixItemKeyData] ZabbixItemKey key, bool supported);

    [LoggerMessage(EventId = 1201, Level = LogLevel.Warning, Message = "Agent {Destination} could not be asked for the value of item key \"{Key}\"")]
    public static partial void GetterFailed(ILogger logger, string destination, [ZabbixItemKeyData] ZabbixItemKey key, Exception exception);

    /// <summary>The agent refused the request as a whole (a root <c>error</c>), not one key.</summary>
    [LoggerMessage(EventId = 1202, Level = LogLevel.Warning, Message = "Agent {Destination} refused the request for item key \"{Key}\": {Info}")]
    public static partial void GetterRefused(ILogger logger, string destination, [ZabbixItemKeyData] ZabbixItemKey key, [ZabbixPeerText] string? info);

    // ----- 1300: the server wire ------------------------------------------------------------------------------

    /// <summary>The server answered one of its wire requests.</summary>
    [LoggerMessage(EventId = 1300, Level = LogLevel.Debug, Message = "Server wire {Request}: {Response}")]
    public static partial void ServerWireAnswered(ILogger logger, string request, string response);

    /// <summary>The server refused a wire request (<c>"response":"failed"</c> with its <c>info</c>).</summary>
    [LoggerMessage(EventId = 1301, Level = LogLevel.Warning, Message = "Server wire {Request} refused: {Info}")]
    public static partial void ServerWireRefused(ILogger logger, string request, [ZabbixPeerText] string info);

    /// <summary>
    /// The exchange failed: no reply, not the server's reply shape, or the transport itself broke. Debug: the exception is
    /// the caller's, who decides what it means and logs it at the level it chooses.
    /// </summary>
    [LoggerMessage(EventId = 1302, Level = LogLevel.Debug, Message = "Server wire {Request} to {Node} failed")]
    public static partial void ServerWireFailed(ILogger logger, string request, string node, Exception exception);
}
