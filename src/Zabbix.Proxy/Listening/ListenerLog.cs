// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Listening;

/// <summary>
/// Defines the receiving edges' log events. Id ranges:
/// <c>60xx</c> the listener (bind, refuse, serve, fault), <c>61xx</c> the trapper, <c>62xx</c> the passive agent,
/// <c>63xx</c> the connector endpoint. Every Warning and Error here is asserted by a test.
/// </summary>
internal static partial class ListenerLog
{
    // ----- 6000: the listener ---------------------------------------------------------------------------------

    /// <summary>A listener is bound and accepting.</summary>
    [LoggerMessage(EventId = 6000, Level = LogLevel.Information, Message = "Listening on {EndPoint}, accepting {TlsAccept}, peers {AllowedPeers}")]
    public static partial void ListenerBound(ILogger logger, string? endPoint, ZabbixTlsAcceptModes tlsAccept, ZabbixAllowedPeers allowedPeers);

    /// <summary>A listener has stopped and released its port.</summary>
    [LoggerMessage(EventId = 6001, Level = LogLevel.Information, Message = "Listener stopped")]
    public static partial void ListenerStopped(ILogger logger);

    /// <summary>A connection failed while being served (the transport, the TLS handshake, the frame): once per peer and failure kind an hour.</summary>
    [LoggerMessage(EventId = 6002, Level = LogLevel.Warning, Message = "Connection from {Peer} failed")]
    public static partial void ConnectionFailed(ILogger logger, IPAddress peer, Exception exception);

    /// <summary>
    /// A connection was refused or cut before it was answered, with its <see cref="ZabbixListenerRejection"/> code: once
    /// per peer and code an hour, every one counted.
    /// </summary>
    [LoggerMessage(EventId = 6003, Level = LogLevel.Warning, Message = "Connection from {Peer} refused: {Rejection}")]
    public static partial void PeerRefused(ILogger logger, IPAddress peer, string rejection);

    /// <summary>One request was answered.</summary>
    [LoggerMessage(EventId = 6004, Level = LogLevel.Debug, Message = "Answered {Peer} ({Security}): {RequestBytes} bytes in, {ReplyBytes} bytes out, {ElapsedMs:F1} ms")]
    public static partial void RequestHandled(ILogger logger, IPAddress peer, string security, int requestBytes, int replyBytes, double elapsedMs);

    /// <summary>The handler threw; the peer was answered <c>{"response":"failed"}</c>.</summary>
    [LoggerMessage(EventId = 6005, Level = LogLevel.Error, Message = "The handler failed on a request from {Peer}; answered failed")]
    public static partial void HandlerFaulted(ILogger logger, IPAddress peer, Exception exception);

    /// <summary>A handler that ignored its deadline failed after its connection was released; nobody was answered.</summary>
    [LoggerMessage(EventId = 6007, Level = LogLevel.Warning, Message = "The handler for a request from {Peer} failed after the connection's deadline released it")]
    public static partial void HandlerFaultedLate(ILogger logger, IPAddress peer, Exception exception);

    /// <summary>Accepting a connection failed; the listener backs off and goes on accepting.</summary>
    [LoggerMessage(EventId = 6008, Level = LogLevel.Error, Message = "Accepting a connection failed ({Failures} in a row); the listener goes on")]
    public static partial void AcceptFailed(ILogger logger, int failures, Exception exception);

    /// <summary>A synchronous disposal started the transport's unbind, which then failed: the port may stay bound until the process ends.</summary>
    [LoggerMessage(EventId = 6009, Level = LogLevel.Warning, Message = "Releasing the listener's port failed after a synchronous disposal")]
    public static partial void UnbindFailed(ILogger logger, Exception exception);

    /// <summary>An allowed-peer name did not resolve; it matches nothing for this connection.</summary>
    [LoggerMessage(EventId = 6006, Level = LogLevel.Warning, Message = "Allowed peer {Name} did not resolve; it matches nothing")]
    public static partial void AllowedPeerUnresolved(ILogger logger, string name, Exception exception);

    // ----- 6100: the trapper ----------------------------------------------------------------------------------

    /// <summary>A trapper took a frame's values.</summary>
    [LoggerMessage(EventId = 6100, Level = LogLevel.Debug, Message = "Trapper took {Processed} value(s) from {Peer}, refused {Failed}")]
    public static partial void TrapperFrameReceived(ILogger logger, string? peer, int processed, int failed);

    /// <summary>A frame the trapper could not read as sender data.</summary>
    [LoggerMessage(EventId = 6101, Level = LogLevel.Warning, Message = "Trapper rejected a frame from {Peer}: {Reason}")]
    public static partial void TrapperFrameRejected(ILogger logger, string? peer, [ZabbixPeerText] string reason);

    /// <summary>The writer threw on values of a frame (a store's backend fault); they were answered failed, the rest processed.</summary>
    [LoggerMessage(EventId = 6102, Level = LogLevel.Error, Message = "Trapper's writer failed on {Failed} value(s) from {Peer}; answered them failed")]
    public static partial void TrapperWriteFaulted(ILogger logger, string? peer, int failed, Exception exception);

    /// <summary>An agent's session sent a last value id below the one it already delivered: a network anomaly, as upstream logs it.</summary>
    [LoggerMessage(EventId = 6103, Level = LogLevel.Warning, Message = "Trapper received value identifier {Id} from agent {Host} lower than the last processed value identifier {LastId}")]
    public static partial void TrapperAgentIdBehind(ILogger logger, string host, long id, long lastId);

    // ----- 6200: the passive agent ----------------------------------------------------------------------------

    /// <summary>A passive check was answered.</summary>
    [LoggerMessage(EventId = 6200, Level = LogLevel.Debug, Message = "Passive check {Key}: {Supported}")]
    public static partial void PassiveCheckAnswered(ILogger logger, [ZabbixItemKeyData] ZabbixItemKey key, bool supported);

    // ----- 6300: the connector endpoint -----------------------------------------------------------------------

    /// <summary>A connector POST was taken.</summary>
    [LoggerMessage(EventId = 6300, Level = LogLevel.Debug, Message = "Connector took {Count} record(s)")]
    public static partial void ConnectorReceived(ILogger logger, int count);

    /// <summary>A connector body was not NDJSON of records; nothing of it was taken and the server was answered 400.</summary>
    [LoggerMessage(EventId = 6301, Level = LogLevel.Warning, Message = "Connector refused a body: {Reason}")]
    public static partial void ConnectorLineRejected(ILogger logger, string reason);

    /// <summary>The sink was completed while a body was being taken; the server was answered 503 and will re-send.</summary>
    [LoggerMessage(EventId = 6302, Level = LogLevel.Warning, Message = "Connector sink is closed after {Taken} of {Count} record(s); answered 503, the server re-sends the whole body")]
    public static partial void ConnectorSinkClosed(ILogger logger, int taken, int count);

    /// <summary>A connector POST was refused by the endpoint's admission (a missing or wrong token, a peer outside the list); answered 401 or 403.</summary>
    [LoggerMessage(EventId = 6303, Level = LogLevel.Warning, Message = "Connector refused a request from {Peer}: {Reason}")]
    public static partial void ConnectorRefused(ILogger logger, IPAddress peer, string reason);
}
