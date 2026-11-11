// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Proxy;

/// <summary>
/// Defines the proxy layer's log events. Ids 7000–7099: 700x the
/// configuration, 701x history, 702x the trapper's admission, 703x the loops and the passive conversations, 704x pages the
/// server refuses for their content.
/// </summary>
internal static partial class ProxyLog
{
    // ----- 700x: the configuration ---------------------------------------------------------------------------------

    /// <summary>The server handed the proxy a configuration (a full sync or a delta), now merged and held.</summary>
    [LoggerMessage(EventId = 7000, Level = LogLevel.Information, Message = "Proxy {Proxy} holds configuration revision {Revision} (full sync {FullSync}): {Hosts} host(s), {Items} item(s)")]
    public static partial void ProxyConfigReceived(ILogger logger, string proxy, long revision, bool fullSync, int hosts, int items);

    /// <summary>The configuration exchange failed (unreachable, a bad frame); the held configuration stays.</summary>
    [LoggerMessage(EventId = 7001, Level = LogLevel.Warning, Message = "Proxy {Proxy} could not pull its configuration; keeping revision {Revision}")]
    public static partial void ProxyConfigPullFailed(ILogger logger, string proxy, long revision, Exception exception);

    /// <summary>The server answered the configuration request with a refusal: nothing was received.</summary>
    [LoggerMessage(EventId = 7002, Level = LogLevel.Warning, Message = "Proxy {Proxy}: the server (version {ServerVersion}) refused configuration to proxy version {Version}: {Info}")]
    public static partial void ProxyConfigRefused(ILogger logger, string proxy, string version, string? serverVersion, [ZabbixPeerText] string info);

    /// <summary>The server's refusal named its version; the proxy now announces it, because config is served only to the server's own major.minor.</summary>
    [LoggerMessage(EventId = 7003, Level = LogLevel.Information, Message = "Proxy {Proxy} now announces protocol version {Version} (was {Previous}): the server runs {ServerVersion}")]
    public static partial void ProxyVersionAdopted(ILogger logger, string proxy, string version, string previous, string serverVersion);

    // ----- 701x: history -------------------------------------------------------------------------------------------

    /// <summary>A page of history went to the server (active) or to the server's pull (passive), and the server answered.</summary>
    [LoggerMessage(EventId = 7010, Level = LogLevel.Debug, Message = "Proxy {Proxy} sent {Count} value(s) (ids to {LastId}, more {More}): {Response}, upload {Upload}")]
    public static partial void ProxyDataSent(ILogger logger, string proxy, int count, long lastId, bool more, string response, string upload);

    /// <summary>The server refused a page; it stays held and is sent again (the upstream proxy keeps it until its offline buffer ages it).</summary>
    [LoggerMessage(EventId = 7011, Level = LogLevel.Warning, Message = "Proxy {Proxy}: the server refused a page of {Count} value(s): {Info}; it is kept and sent again")]
    public static partial void ProxyUploadRefused(ILogger logger, string proxy, int count, [ZabbixPeerText] string info);

    /// <summary>The server has disabled history uploads: the proxy holds its history and asks, without history, until they are enabled.</summary>
    [LoggerMessage(EventId = 7012, Level = LogLevel.Warning, Message = "Proxy {Proxy} holds its history: the server has disabled uploads ({Info})")]
    public static partial void ProxyUploadHeld(ILogger logger, string proxy, string info);

    /// <summary>The server has enabled uploads again.</summary>
    [LoggerMessage(EventId = 7013, Level = LogLevel.Information, Message = "Proxy {Proxy} resumes uploading: the server has enabled uploads")]
    public static partial void ProxyUploadResumed(ILogger logger, string proxy);

    /// <summary>Stored values whose item left the configuration before they were sent: they cannot be sent by item id.</summary>
    [LoggerMessage(EventId = 7014, Level = LogLevel.Warning, Message = "Proxy {Proxy} dropped {Count} stored value(s) whose item is no longer in its configuration")]
    public static partial void ProxyValuesUnmapped(ILogger logger, string proxy, int count);

    /// <summary>The server's acknowledgement carried tasks (remote commands, checks now); this proxy does not execute them.</summary>
    [LoggerMessage(EventId = 7015, Level = LogLevel.Warning, Message = "Proxy {Proxy}: the server sent {Count} task(s); this proxy does not execute tasks")]
    public static partial void ProxyTasksIgnored(ILogger logger, string proxy, int count);

    /// <summary>A page could not be exchanged (unreachable, a bad frame); it stays leased, with its ids, for the next turn.</summary>
    [LoggerMessage(EventId = 7016, Level = LogLevel.Warning, Message = "Proxy {Proxy} could not send a page of {Count} value(s); it is kept and sent again")]
    public static partial void ProxyUploadFailed(ILogger logger, string proxy, int count, Exception exception);

    /// <summary>A registered <see cref="ZabbixProxyDataCallback"/> threw; its page is skipped for this turn, the others still run.</summary>
    [LoggerMessage(EventId = 7017, Level = LogLevel.Warning, Message = "Proxy {Proxy}: a data provider failed and was skipped for this turn")]
    public static partial void ProxyProviderFailed(ILogger logger, string proxy, Exception exception);

    /// <summary>A registered <see cref="ZabbixProxyTaskCallback"/> threw running a task the server sent; the failure is reported back as the task's result.</summary>
    [LoggerMessage(EventId = 7018, Level = LogLevel.Warning, Message = "Proxy {Proxy}: the task handler failed on a type {Type} task")]
    public static partial void ProxyTaskHandlerFailed(ILogger logger, string proxy, ZabbixProxyTaskType type, Exception exception);

    /// <summary>A registered <see cref="ZabbixProxyStatsCallback"/> threw answering <c>zabbix.stats</c>; the reply carries no data.</summary>
    [LoggerMessage(EventId = 7019, Level = LogLevel.Warning, Message = "Proxy {Proxy}: the stats provider failed")]
    public static partial void ProxyStatsProviderFailed(ILogger logger, string proxy, Exception exception);

    // ----- 702x: admission -----------------------------------------------------------------------------------------

    /// <summary>A trapped value was refused by Zabbix's own rules (item type, allowed hosts, a disabled item): upstream logs the same per value; this once per host, key and reason an hour, every one counted (<c>zabbix.proxy.values</c>, refused).</summary>
    [LoggerMessage(EventId = 7020, Level = LogLevel.Warning, Message = "Proxy {Proxy} cannot process item \"{Key}\" on host \"{Host}\" from {Peer}: {Reason}")]
    public static partial void ProxyValueRefused(ILogger logger, string proxy, string host, [ZabbixItemKeyData] string key, IPAddress? peer, string reason);

    // ----- 703x: loops and conversations ---------------------------------------------------------------------------

    /// <summary>A proxy loop met a failure that is not a transient exchange or server failure (a defect) and has stopped; stopping the proxy surfaces it.</summary>
    [LoggerMessage(EventId = 7030, Level = LogLevel.Error, Message = "Proxy {Proxy}: the {Loop} loop faulted and has stopped")]
    public static partial void ProxyLoopFaulted(ILogger logger, string proxy, string loop, Exception exception);

    /// <summary>The tasks store did not keep a task's result (full under its policy, or closed): the server is never told the task ran.</summary>
    [LoggerMessage(EventId = 7021, Level = LogLevel.Warning, Message = "Proxy {Proxy}: the result of a type {Type} task was not kept: the tasks store is full or closed")]
    public static partial void ProxyTaskResultDropped(ILogger logger, string proxy, ZabbixProxyTaskType type);

    /// <summary>A passive conversation with the server ended without its acknowledgement; what it carried is kept.</summary>
    [LoggerMessage(EventId = 7031, Level = LogLevel.Warning, Message = "Proxy {Proxy}: the {Request} conversation with {Peer} failed: {Reason}")]
    public static partial void ProxyConversationFailed(ILogger logger, string proxy, string request, string? peer, string reason, Exception? exception = null);

    /// <summary>A registered <see cref="ZabbixPassiveProxyConfigCallback"/> vetoed an incoming configuration; the held configuration is kept.</summary>
    [LoggerMessage(EventId = 7032, Level = LogLevel.Warning, Message = "Proxy {Proxy}: the configuration handler vetoed revision {Revision}: {Reason}")]
    public static partial void ProxyConfigVetoed(ILogger logger, string proxy, long revision, string reason);

    // ----- 704x: pages the server refuses for their content -------------------------------------------------------

    /// <summary>
    /// The server refused a page of one caller kind on its own while it took the proxy's history (or its probe): it cannot
    /// read the rows and would refuse them for ever, so they are dropped rather than holding the data behind them.
    /// </summary>
    [LoggerMessage(EventId = 7040, Level = LogLevel.Error, Message = "Proxy {Proxy} dropped {Count} {Kind} row(s) the server refused on their own: {Info}")]
    public static partial void ProxyPageInvalid(ILogger logger, string proxy, int count, string kind, [ZabbixPeerText] string info);
}
