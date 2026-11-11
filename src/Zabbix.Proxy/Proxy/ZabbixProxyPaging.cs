// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Community.Zabbix.Hosting;
using Community.Zabbix.Listening;
using Community.Zabbix.Storage;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Proxy;

/// <summary>
/// What the active and the passive proxy share: the trapper's intake by Zabbix's rules, a page cut from the store's
/// acknowledged face, and one fault contract for their loops and conversations.
/// </summary>
internal static class ZabbixProxyPaging
{
    /// <summary>
    /// The fault contract. A failure of the exchange (the wire's own <c>ZabbixFrames.IsExchangeFailure</c>: socket, I/O,
    /// TLS, frame, deadline), a reply that is not the protocol's JSON, the server's refusal, or the
    /// store's database is <b>transient</b>: it is logged, what was in flight stays held, and the next cycle tries again —
    /// the upstream proxy's datasender and configuration syncer do the same, forever. Anything else is a defect: the loop
    /// logs it (7030) and stops, and stopping the proxy surfaces it. Cancellation by the caller is neither.
    /// </summary>
    public static bool IsTransient(Exception failure, CancellationToken stopping)
        => Protocol.ZabbixFrames.IsExchangeFailure(failure) || failure is JsonException or ZabbixProxyRefusedException or DbException
           || (failure is OperationCanceledException && !stopping.IsCancellationRequested);

    /// <summary>Runs a loop under the contract: a defect is logged before the task faults with it.</summary>
    public static async Task RunAsync(string loop, Func<CancellationToken, Task> body, ILogger logger, string proxy, CancellationToken stopping)
    {
        try
        {
            await body(stopping).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            // stopped
        }
        catch (Exception failure)
        {
            ProxyLog.ProxyLoopFaulted(logger, proxy, loop, failure);
            throw;
        }
    }

    /// <summary>The <c>request</c> of a frame, read without materialising the rest; empty when it is not a JSON object with one.</summary>
    public static string KindOf(ReadOnlyMemory<byte> payload)
    {
        var reader = new Utf8JsonReader(payload.Span);
        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return string.Empty;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isRequest = reader.ValueTextEquals("request"u8);
                if (!reader.Read()) return string.Empty;
                if (isRequest) return reader.TokenType == JsonTokenType.String ? reader.GetString() ?? string.Empty : string.Empty;
                reader.Skip();
            }
        }
        catch (JsonException)
        {
            // not JSON: the trapper answers it as such
        }
        return string.Empty;
    }

    /// <summary>
    /// Hands a sender's or an agent's frame to a trapper whose writer admits by <paramref name="admission"/> for this
    /// request's peer, and which resolves an agent's item ids through the same configuration and de-duplicates its values
    /// by <paramref name="sessions"/> (without them every row of a 7.x agent would fail while answered <c>success</c>).
    /// </summary>
    public static ValueTask<ReadOnlyMemory<byte>> TrapAsync(ZabbixRequest request, string kind, ZabbixProxyAdmission admission, ZabbixAgentSessions sessions, ZabbixStore<ZabbixValue> store,
        string proxy, TimeProvider time, ILoggerFactory loggers, IMeterFactory? meters, ILogger logger, ZabbixProxyTelemetry telemetry, CancellationToken cancellationToken)
    {
        IPAddress? peer = (request.Peer as IPEndPoint)?.Address;
        // A trapper per request: its writer carries this request's peer and kind (ZabbixProxyAdmission.For); it is a few
        // fields, and its counter is the meter factory's cached instrument.
        var trapper = new ZabbixTrapper(admission.For(store, kind, peer, ZabbixProxyAdmissionLogging.Refused(logger, proxy, peer, telemetry)),
            new AgentItems(admission.Config, sessions), loggers, time, meters);
        return trapper.Handler(request, cancellationToken);
    }

    /// <summary>An agent's items and sessions as the proxy holds them: its configuration, its session table.</summary>
    private sealed class AgentItems(ZabbixProxyConfig config, ZabbixAgentSessions sessions) : IZabbixAgentItems
    {
        public bool TryGetHost(string host, out ulong hostId)
        {
            bool found = config.HostsByName.TryGetValue(host, out ZabbixProxyConfigHost? known);
            hostId = known?.HostId ?? 0UL;
            return found;
        }

        public bool TryGetItem(ulong itemId, [NotNullWhen(true)] out string? host, out ZabbixItemKey key)
        {
            host = null;
            key = default;
            return config.Items.TryGetValue(itemId, out ZabbixProxyConfigItem? item) && config.Hosts.TryGetValue(item.HostId, out ZabbixProxyConfigHost? owner)
                   && ZabbixItemKey.TryParse(item.Key, out key) && (host = owner.Host) is not null;
        }

        public ZabbixAgentSession? Session(ulong hostId, string session) => sessions.Get(hostId, session);
    }

    /// <summary>
    /// Answers <c>active checks</c> from the configuration the proxy already holds, so an agent can poll a passive
    /// proxy directly. The shape and the refusals are
    /// <c>zbxtrapper/active.c</c>'s <c>send_list_of_active_checks</c>: <c>{"response":"success","config_revision":…,
    /// "data":[{"itemid":…,"key":…,"delay":…,"lastlogsize":0,"mtime":0,"timeout":…}]}</c>, or <c>{"response":"failed",
    /// "info":"host [%s] not found"}</c> / <c>"host \"%s\" not monitored"</c>. Upstream also tracks a per-agent session so a
    /// repeat with the same revision gets no <c>data</c> at all; this proxy answers every call in full: a larger reply than
    /// upstream's, not a wrong one, and no per-agent session store to own for it.
    /// </summary>
    public static ReadOnlyMemory<byte> ActiveChecks(ZabbixRequest request, ZabbixProxyConfig config)
    {
        ZabbixActiveChecksRequest? ask;
        try
        {
            ask = JsonSerializer.Deserialize(request.Payload.Span, ZabbixActiveAgentJson.Default.ZabbixActiveChecksRequest);
        }
        catch (JsonException failure)
        {
            return ActiveChecksFailed(failure.Message);
        }
        if (ask?.Host is not { Length: > 0 } host) return ActiveChecksFailed("no host name was sent");
        if (!config.HostsByName.TryGetValue(host, out ZabbixProxyConfigHost? found)) return ActiveChecksFailed($"host [{host}] not found");
        if (found.Status != ZabbixHostStatus.Enabled) return ActiveChecksFailed($"host \"{host}\" not monitored");

        var assignments = new List<ZabbixItemAssignment>();
        foreach (ZabbixProxyConfigItem item in config.ItemsByHost.GetValueOrDefault(found.HostId, []))
        {
            if (item.Type == ZabbixItemType.ZabbixActive && item.Status == ZabbixItemStatus.Active)
            {
                // Delay and timeout with the host's user macros resolved, as zbx_dc_expand_user_and_func_macros does; an
                // item without its own timeout carries the proxy's agent timeout (dbconfig.c timeout_orig), and none is
                // sent only while the server has sent none (the agent's own Timeout then applies).
                ZabbixInterval delay = ZabbixInterval.FromReply(ZabbixProxyAdmission.ResolveMacros(item.Delay.ToString(), found, config));
                string held = item.Timeout?.ToString() ?? config.AgentTimeout;
                ZabbixDuration? timeout = held.Length > 0 ? ZabbixDuration.FromReply(ZabbixProxyAdmission.ResolveMacros(held, found, config)) : null;
                assignments.Add(new ZabbixItemAssignment(item.ItemId, item.Key, delay, timeout));
            }
        }
        var response = new ZabbixActiveChecksResponse("success", assignments, config.ConfigRevision, null);
        return JsonSerializer.SerializeToUtf8Bytes(response, ZabbixActiveAgentJson.Default.ZabbixActiveChecksResponse);
    }

    private static ReadOnlyMemory<byte> ActiveChecksFailed(string info)
        => JsonSerializer.SerializeToUtf8Bytes(new ZabbixActiveChecksResponse("failed", null, null, info), ZabbixActiveAgentJson.Default.ZabbixActiveChecksResponse);

    /// <summary>
    /// Leases up to <paramref name="size"/> more entries into <paramref name="page"/> without waiting, and maps the page to
    /// <c>history data</c> by item id; an entry whose item left the configuration cannot be sent and is counted.
    /// </summary>
    public static (List<ZabbixProxyHistoryItem> History, int Unmapped) Fill(List<ZabbixStoreEntry<ZabbixValue>> page, int size, ZabbixStore<ZabbixValue> store, ZabbixProxyConfig config)
    {
        while (page.Count < size && store.TryLease(out ZabbixStoreEntry<ZabbixValue> entry)) page.Add(entry);
        var history = new List<ZabbixProxyHistoryItem>(page.Count);
        int unmapped = 0;
        foreach (ZabbixStoreEntry<ZabbixValue> entry in page)
        {
            ZabbixValue value = entry.Record;
            if (config.ItemsByKey.TryGetValue((value.Host, value.Key.ToString()), out ZabbixProxyConfigItem? item))
                history.Add(new ZabbixProxyHistoryItem(entry.Sequence, item.ItemId, value.Clock, value.Value, value.IsSupported ? null : 1));
            else
                unmapped++;
        }
        return (history, unmapped);
    }

    /// <summary>
    /// Whether more is held behind a page, and <c>proxy_delay</c>: how many seconds the oldest value left behind has waited
    /// since it was stored (upstream <c>zbx_proxy_get_delay</c>: now − <c>write_clock</c> of the first record after the page).
    /// </summary>
    public static (bool More, long Delay) Behind(ZabbixStore<ZabbixValue> store, TimeProvider time)
        => store.TryPeek(out ZabbixStoreEntry<ZabbixValue> next)
            ? (true, Math.Max(0, (long)(time.GetUtcNow() - next.Stored).TotalSeconds))
            : (false, 0);

    /// <summary>
    /// Leases up to <paramref name="size"/> more entries into <paramref name="page"/> without waiting, and maps every entry
    /// to its wire shape with <paramref name="map"/>: the discovery, auto-registration, interface-availability and task-
    /// result kinds, which (unlike <see cref="Fill"/>'s history) carry no configuration mapping — every leased entry goes
    /// out as-is.
    /// </summary>
    public static List<TWire> FillSimple<T, TWire>(List<ZabbixStoreEntry<T>> page, int size, ZabbixStore<T> store, Func<T, TWire> map)
    {
        while (page.Count < size && store.TryLease(out ZabbixStoreEntry<T> entry)) page.Add(entry);
        var wire = new List<TWire>(page.Count);
        foreach (ZabbixStoreEntry<T> entry in page) wire.Add(map(entry.Record));
        return wire;
    }

    /// <summary>Whether more is held behind a page of <typeparamref name="T"/>, and how old the oldest of it is.</summary>
    public static (bool More, long Delay) Behind<T>(ZabbixStore<T> store, TimeProvider time)
        => store.TryPeek(out ZabbixStoreEntry<T> next)
            ? (true, Math.Max(0, (long)(time.GetUtcNow() - next.Stored).TotalSeconds))
            : (false, 0);

    // ----- providers and task handlers: the extension point every proxy host shares (Register-Zbx*ProxyDataProvider) ------

    /// <summary>
    /// Asks every provider in <paramref name="providers"/> for a page and merges the answers (<see cref="ZabbixProxyDataProviders"/>,
    /// the public type both proxy hosts and a caller composing its own turn over <see cref="ZabbixProxyUplink"/> share). A
    /// provider that throws is logged (7017) and skipped — one caller's scriptblock must not stop the turn any more than
    /// a bad store entry does (the same transient discipline as <see cref="IsTransient"/>'s callers).
    /// </summary>
    public static async ValueTask<ZabbixProxyDataPage> AskProvidersAsync(ZabbixProxyDataPage page, IReadOnlyList<ZabbixProxyDataCallback> providers, ZabbixProxyConfig config,
        ILogger logger, string proxy, CancellationToken cancellationToken)
    {
        if (providers.Count == 0) return page;
        IReadOnlyList<ZabbixProxyDataPage> provided = await ZabbixProxyDataProviders.AskAsync(providers, config,
            failure => ProxyLog.ProxyProviderFailed(logger, proxy, failure), cancellationToken).ConfigureAwait(false);
        return ZabbixProxyDataProviders.Merge(page, provided);
    }

    /// <summary>
    /// Runs <paramref name="handler"/> over every task in <paramref name="response"/> (<see cref="ZabbixProxyResponseTasks.DeserializeTasks"/>),
    /// writing each result to <paramref name="tasksStore"/> for the next turn to upload; a handler failure is logged
    /// (7018) and reported back as a failed <see cref="ZabbixProxyTaskResult"/> rather than dropped, so the server is
    /// told the task did not run. With no handler the tasks are only logged (7015).
    /// </summary>
    public static async Task HandleTasksAsync(ZabbixProxyResponse response, ZabbixProxyTaskCallback? handler, ZabbixStore<ZabbixProxyTaskResult> tasksStore,
        ILogger logger, string proxy, CancellationToken cancellationToken)
    {
        if (response.TaskCount == 0) return;
        if (handler is null)
        {
            ProxyLog.ProxyTasksIgnored(logger, proxy, response.TaskCount);
            return;
        }
        foreach (ZabbixProxyTask task in response.DeserializeTasks())
        {
            ZabbixProxyTaskResult result;
            try
            {
                result = await handler(task, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure) when (failure is not OutOfMemoryException)
            {
                ProxyLog.ProxyTaskHandlerFailed(logger, proxy, task.Type, failure);
                result = new ZabbixProxyTaskResult(ZabbixProxyTaskType.RemoteCommandResult, task.Clock, task.Ttl, Status: 1, failure.Message, task.ParentTaskId ?? 0UL);
            }
            // TryWrite, not the awaited WriteAsync: this runs on the upload loop's own thread, the store's single
            // reader — the channel's own WriteAsync path can invoke a blocked reader's continuation before returning,
            // which deadlocks that same thread against itself on the next turn's TryLease of this same
            // store. TryWrite applies the same refusal/drop policy synchronously with no such continuation.
            if (!tasksStore.TryWrite(result)) ProxyLog.ProxyTaskResultDropped(logger, proxy, task.Type);
        }
    }
}
