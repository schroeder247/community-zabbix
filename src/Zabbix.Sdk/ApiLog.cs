// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Logging;

namespace Community.Zabbix;

/// <summary>
/// Provides the JSON-RPC API layer's log events, ids 4000–4099.
/// 4000–4003 one call's way out and back; 4004–4009 the session and the server's version. The values these events carry
/// are never credentials: a token or password is not a parameter of any of them, and a classified parameter would be
/// dropped or erased by the extended generator this project uses (<see cref="ZabbixDataClassifications"/>).
/// </summary>
internal static partial class ApiLog
{
    /// <summary>A JSON-RPC request is leaving.</summary>
    [LoggerMessage(EventId = 4000, Level = LogLevel.Debug, Message = "JSON-RPC {Method} #{RequestId} →")]
    public static partial void Requesting(ILogger logger, string method, long requestId);

    /// <summary>The API answered with an error object; the caller gets a <see cref="ZabbixApiException"/>.</summary>
    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning, Message = "JSON-RPC {Method} #{RequestId} answered error {Code} ({Kind}): {ErrorMessage} {Detail}")]
    public static partial void ApiError(ILogger logger, string method, long requestId, int code, ZabbixApiErrorKind kind, string errorMessage, [ZabbixPeerText] string? detail);

    /// <summary>The API answered with a result, and the result was read.</summary>
    [LoggerMessage(EventId = 4002, Level = LogLevel.Debug, Message = "JSON-RPC {Method} #{RequestId} ← result in {ElapsedMs:F1} ms")]
    public static partial void Replied(ILogger logger, string method, long requestId, double elapsedMs);

    /// <summary>The exchange failed: no reply in time, not a JSON-RPC reply, or a result the model cannot read.</summary>
    /// <remarks>Debug: the exception is the caller's, who decides what it means and logs it once at the level it chooses.</remarks>
    [LoggerMessage(EventId = 4003, Level = LogLevel.Debug, Message = "JSON-RPC {Method} #{RequestId} failed ({ErrorType})")]
    public static partial void Failed(ILogger logger, string method, long requestId, string errorType, Exception exception);

    /// <summary>The server refused the session before running the method; the credential logs in again and the call is sent once more.</summary>
    [LoggerMessage(EventId = 4004, Level = LogLevel.Information, Message = "JSON-RPC {Method} #{RequestId}: the session was refused ({Kind}); logging in again and re-sending once")]
    public static partial void SessionRenewing(ILogger logger, string method, long requestId, ZabbixApiErrorKind kind);

    /// <summary>The server's API version was read.</summary>
    [LoggerMessage(EventId = 4005, Level = LogLevel.Information, Message = "Zabbix server {Server} at {Endpoint} speaks API {Version}")]
    public static partial void ApiVersion(ILogger logger, string server, Uri endpoint, Version version);

    /// <summary>A disposed client ended its credential's login session (<c>user.logout</c>).</summary>
    [LoggerMessage(EventId = 4006, Level = LogLevel.Debug, Message = "Zabbix server {Server}: the login session was logged out")]
    public static partial void LoggedOut(ILogger logger, string server);

    /// <summary>A disposed client could not end its credential's login session; the server expires it.</summary>
    [LoggerMessage(EventId = 4007, Level = LogLevel.Debug, Message = "Zabbix server {Server}: user.logout failed ({ErrorType}); the login session is left to expire")]
    public static partial void LogoutFailed(ILogger logger, string server, string errorType, Exception exception);
}
