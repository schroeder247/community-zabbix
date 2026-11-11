// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell.Diagnostics;

/// <summary>
/// The shell layer's log events. 140xx connection lifetime, 141xx completion, 142xx session lifecycle, 143xx
/// scriptblock handler invocation.
/// </summary>
internal static partial class ZabbixPowerShellLog
{
    /// <summary>A connection is open and answered <c>apiinfo.version</c>.</summary>
    [LoggerMessage(EventId = 14000, Level = LogLevel.Information, Message = "Connected {Name} to {Endpoint}, Zabbix API {Version}")]
    public static partial void Connected(ILogger logger, string name, Uri endpoint, Version version);

    /// <summary>A connection was closed; its container is disposed once the last in-flight call returns.</summary>
    [LoggerMessage(EventId = 14001, Level = LogLevel.Debug, Message = "Disconnected {Name}; {InFlight} call(s) still in flight")]
    public static partial void Disconnected(ILogger logger, string name, int inFlight);

    /// <summary>Name completion could not ask the server; no names are offered and nothing is cached.</summary>
    [LoggerMessage(EventId = 14100, Level = LogLevel.Warning, Message = "Completing {Entity} names on {Name} failed; no names offered")]
    public static partial void CompletionFailed(ILogger logger, string entity, string name, Exception exception);

    /// <summary>A session's own background loop failed one tick (a proxy's configuration pull, a discovery source's run); the loop keeps running.</summary>
    [LoggerMessage(EventId = 14200, Level = LogLevel.Warning, Message = "{Kind} session {Name}'s loop failed one tick; it keeps running")]
    public static partial void SessionTickFailed(ILogger logger, string kind, string name, Exception exception);

    /// <summary>A session failed to start; nothing was left running.</summary>
    [LoggerMessage(EventId = 14201, Level = LogLevel.Error, Message = "{Kind} session {Name} failed to start")]
    public static partial void SessionStartFailed(ILogger logger, string kind, string name, Exception exception);

    /// <summary>A session failed to stop cleanly; its resources are released regardless.</summary>
    [LoggerMessage(EventId = 14202, Level = LogLevel.Warning, Message = "{Kind} session {Name} failed to stop cleanly")]
    public static partial void SessionStopFailed(ILogger logger, string kind, string name, Exception exception);

    /// <summary>A scriptblock handler invocation (an agent item, a discovery source's tick) failed; the caller gets the failure, not a crash.</summary>
    [LoggerMessage(EventId = 14300, Level = LogLevel.Warning, Message = "{Kind} handler invocation failed: {Reason}")]
    public static partial void HandlerInvocationFailed(ILogger logger, string kind, string reason, Exception exception);
}
