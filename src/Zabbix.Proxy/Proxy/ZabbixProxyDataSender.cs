// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Hosting;
using Community.Zabbix.Storage;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.Proxy;

/// <summary>
/// The <c>proxy data</c> sender both proxy hosts share (upstream's <c>datasender.c</c>): the pages leased from the five
/// stores, the turn that fills, sends and settles them, the hold a server's <c>upload: disabled</c> puts on it, and the
/// isolation of a refused page. Only the exchange differs between the hosts: the active proxy sends through its uplink,
/// the passive proxy answers the server's pull on the conversation, so each step is exactly one exchange and a host
/// takes as many as its own shape allows (<see cref="TurnAsync"/> a whole turn, <see cref="PullAsync"/> one).
/// </summary>
/// <remarks>
/// <para>A page stays leased in memory until the server's <c>success</c> settles it, and is sent again unchanged rather
/// than released and re-leased: <see cref="ZabbixProxyPaging.Fill"/> maps against the configuration as it stands, and a
/// re-leased page could differ from the one already sent. The server's <c>last_id</c> de-duplicates a re-send.</para>
/// <para>A refused page that carries the caller's rows (discovery, auto-registration, availability, task results) is
/// taken apart, because the server refuses a whole <c>proxy data</c> message for one row it cannot read
/// (<c>zbx_process_proxy_data</c>) and refuses it again on every retry. First the control: the history alone, or the
/// empty probe. The server never refuses history for its content (<c>process_history_data_by_itemids</c>), so a refused
/// control is a refusal of the proxy itself and every page waits. Otherwise each caller kind goes alone, and one refused
/// on its own is dropped from its store, logged (7040) and counted (<see cref="ZabbixProxyTelemetry.Invalid"/>). The
/// refusal is not classified by its <c>info</c> (a parse error's text, not a contract), and the common case keeps
/// upstream's one message per turn.</para>
/// <para>Not thread-safe: the active host's upload loop is its one caller, and the passive host serialises its pulls.</para>
/// </remarks>
internal sealed class ZabbixProxyDataSender
{
    private readonly string _proxy;
    private readonly ZabbixProxyStores _stores;
    private readonly int _pageSize;
    private readonly Func<ZabbixProxyConfig> _config;
    private readonly Func<IReadOnlyList<ZabbixProxyDataCallback>> _providers;
    private readonly Func<ZabbixProxyTaskCallback?> _taskHandler;
    private readonly ZabbixProxyTelemetry _telemetry;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ZabbixAgentStatus? _status;
    private readonly List<ZabbixStoreEntry<ZabbixValue>> _history;
    private readonly List<ZabbixStoreEntry<ZabbixProxyDiscoveryItem>> _discovery = [];
    private readonly List<ZabbixStoreEntry<ZabbixProxyAutoregistrationItem>> _autoregistration = [];
    private readonly List<ZabbixStoreEntry<ZabbixProxyAvailabilityItem>> _availability = [];
    private readonly List<ZabbixStoreEntry<ZabbixProxyTaskResult>> _tasks = [];
    private int _held;
    private int _unmapped;                                                                  // entries of the history page no item maps
    private Isolation? _isolating;                                                          // a refused page being taken apart

    /// <summary>Sends one page and answers the server's acknowledgement: the uplink's exchange, or one pull's conversation.</summary>
    /// <param name="page">The lists to send.</param>
    /// <param name="more">Whether more is held behind the page.</param>
    /// <param name="delay">How many seconds the oldest value held behind the page has waited (<c>proxy_delay</c>).</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    public delegate Task<ZabbixProxyResponse> Exchange(ZabbixProxyDataPage page, bool more, long delay, CancellationToken cancellationToken);

    public ZabbixProxyDataSender(string proxy, ZabbixProxyStores stores, int pageSize, Func<ZabbixProxyConfig> config, Func<IReadOnlyList<ZabbixProxyDataCallback>> providers,
        Func<ZabbixProxyTaskCallback?> taskHandler, ZabbixProxyTelemetry telemetry, ILogger logger, TimeProvider time, ZabbixAgentStatus? status)
    {
        _proxy = proxy;
        _stores = stores;
        _pageSize = pageSize;
        _config = config;
        _providers = providers;
        _taskHandler = taskHandler;
        _telemetry = telemetry;
        _logger = logger;
        _time = time;
        _status = status;
        _history = new List<ZabbixStoreEntry<ZabbixValue>>(pageSize);
    }

    /// <summary>Whether the server has disabled uploads and the sender is holding its pages.</summary>
    public bool IsHeld => Volatile.Read(ref _held) != 0;

    /// <summary>Whether a page of any kind is leased and not yet settled.</summary>
    public bool AnyLeased => _history.Count > 0 || _discovery.Count > 0 || _autoregistration.Count > 0 || _availability.Count > 0 || _tasks.Count > 0;

    /// <summary>How many history entries are leased and not yet settled.</summary>
    public int HistoryLeased => _history.Count;

    /// <summary>
    /// One turn of the active data sender: the probe while held, the page, and the isolation of a refused page, as many
    /// exchanges as that takes. Answers whether to go again at once (<c>more</c> was sent and taken).
    /// </summary>
    public async Task<bool> TurnAsync(Exchange exchange, CancellationToken cancellationToken)
    {
        while (true)
        {
            (bool ended, bool again) = await StepAsync(exchange, answerAlways: false, cancellationToken).ConfigureAwait(false);
            if (ended) return again;
        }
    }

    /// <summary>
    /// One pull of the passive proxy: exactly one exchange, whatever the step is (the probe while held, the next step of an
    /// isolation, or the page, empty when nothing is held), so every pull is answered once.
    /// </summary>
    public Task PullAsync(Exchange exchange, CancellationToken cancellationToken) => StepAsync(exchange, answerAlways: true, cancellationToken);

    /// <summary>
    /// The passive proxy's <c>proxy tasks</c> pull (<c>send_task_data</c>, <c>trapper_proxy.c</c> 8.0:387): the task results
    /// leased (the lease a page uses, so a result goes out once), sent, and taken out of the store only on the server's
    /// <c>success</c>, whose own tasks are then handed to the task handler.
    /// </summary>
    /// <param name="exchange">Sends the results (<see langword="null"/> when none is held) and answers the acknowledgement.</param>
    /// <param name="cancellationToken">Cancels the exchange.</param>
    /// <returns>The server's acknowledgement.</returns>
    public async Task<ZabbixProxyResponse> TaskResultsAsync(Func<IReadOnlyList<ZabbixProxyTaskResult>?, CancellationToken, Task<ZabbixProxyResponse>> exchange, CancellationToken cancellationToken)
    {
        List<ZabbixProxyTaskResult> tasks = ZabbixProxyPaging.FillSimple(_tasks, _pageSize, _stores.Tasks, static x => x);
        ZabbixProxyResponse response = await exchange(tasks.Count > 0 ? tasks : null, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccess) return response;
        Settle(_stores.Tasks, _tasks, tasks.Count, ZabbixProxyTelemetry.Tasks);
        await HandleTasksAsync(response, cancellationToken).ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// One step: at most one exchange, at least one when <paramref name="answerAlways"/>. <c>Ended</c> says the turn is over
    /// (<c>Again</c>: whether to go again at once); otherwise the same turn has another exchange to make.
    /// </summary>
    private async Task<(bool Ended, bool Again)> StepAsync(Exchange exchange, bool answerAlways, CancellationToken cancellationToken)
    {
        if (IsHeld)
        {
            ZabbixProxyResponse probe = await exchange(ZabbixProxyDataPage.Empty, more: false, delay: 0, cancellationToken).ConfigureAwait(false);
            await HandleTasksAsync(probe, cancellationToken).ConfigureAwait(false);
            if (!probe.IsSuccess || probe.UploadDisabled) return (true, false);
            Volatile.Write(ref _held, 0);
            ProxyLog.ProxyUploadResumed(_logger, _proxy);
            return answerAlways ? (true, false) : (false, false);                          // the probe was this pull's one exchange
        }
        return _isolating is { } isolation
            ? await IsolateAsync(exchange, isolation, cancellationToken).ConfigureAwait(false)
            : await PageAsync(exchange, answerAlways, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(bool Ended, bool Again)> PageAsync(Exchange exchange, bool answerAlways, CancellationToken cancellationToken)
    {
        ZabbixProxyStores stores = _stores;
        (List<ZabbixProxyHistoryItem> history, _unmapped) = ZabbixProxyPaging.Fill(_history, _pageSize, stores.History, _config());
        List<ZabbixProxyDiscoveryItem> discovery = ZabbixProxyPaging.FillSimple(_discovery, _pageSize, stores.Discovery, static x => x);
        List<ZabbixProxyAutoregistrationItem> autoregistration = ZabbixProxyPaging.FillSimple(_autoregistration, _pageSize, stores.Autoregistration, static x => x);
        List<ZabbixProxyAvailabilityItem> availability = ZabbixProxyPaging.FillSimple(_availability, _pageSize, stores.Availability, static x => x);
        List<ZabbixProxyTaskResult> tasks = ZabbixProxyPaging.FillSimple(_tasks, _pageSize, stores.Tasks, static x => x);
        IReadOnlyList<ZabbixProxyDataCallback> providers = _providers();
        if (!answerAlways && !AnyLeased && providers.Count == 0) return (true, true);     // nothing leased and no provider to ask

        (bool moreHistory, long delayHistory) = ZabbixProxyPaging.Behind(stores.History, _time);
        (bool moreDiscovery, long delayDiscovery) = ZabbixProxyPaging.Behind(stores.Discovery, _time);
        (bool moreAutoregistration, long delayAutoregistration) = ZabbixProxyPaging.Behind(stores.Autoregistration, _time);
        (bool moreAvailability, long delayAvailability) = ZabbixProxyPaging.Behind(stores.Availability, _time);
        (bool moreTasks, long delayTasks) = ZabbixProxyPaging.Behind(stores.Tasks, _time);
        bool more = moreHistory || moreDiscovery || moreAutoregistration || moreAvailability || moreTasks;
        long delay = Math.Max(delayHistory, Math.Max(delayDiscovery, Math.Max(delayAutoregistration, Math.Max(delayAvailability, delayTasks))));

        var page = new ZabbixProxyDataPage(
            history.Count > 0 ? history : null, discovery.Count > 0 ? discovery : null, autoregistration.Count > 0 ? autoregistration : null,
            availability.Count > 0 ? availability : null, tasks.Count > 0 ? tasks : null);
        page = await ZabbixProxyPaging.AskProvidersAsync(page, providers, _config(), _logger, _proxy, cancellationToken).ConfigureAwait(false);
        if (page.IsEmpty && !answerAlways)
        {
            if (_history.Count > 0) SettleHistory(delivered: 0);                           // history left behind only unmapped entries
            return (true, more);
        }

        ZabbixProxyResponse response = await exchange(page, more, delay, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccess)
        {
            SettleHistory(history.Count);
            Settle(stores.Discovery, _discovery, discovery.Count, ZabbixProxyTelemetry.Discovery);
            Settle(stores.Autoregistration, _autoregistration, autoregistration.Count, ZabbixProxyTelemetry.Autoregistration);
            Settle(stores.Availability, _availability, availability.Count, ZabbixProxyTelemetry.Availability);
            Settle(stores.Tasks, _tasks, tasks.Count, ZabbixProxyTelemetry.Tasks);
            await HandleTasksAsync(response, cancellationToken).ConfigureAwait(false);
            if (response.UploadDisabled) Hold(response, history.Count);
            return (true, more && !IsHeld);
        }
        if (response.UploadDisabled)
        {
            Hold(response, history.Count);                                                // the pages wait, with their ids
            return (true, false);
        }
        if (!CarriesCallerRows(page))
        {
            ProxyLog.ProxyUploadRefused(_logger, _proxy, history.Count, response.Info ?? "no reason given");
            return (true, false);
        }
        _isolating = new Isolation(page, more, delay, response.Info);
        return answerAlways ? (true, false) : (false, false);
    }

    /// <summary>The next step of taking a refused page apart: the control first, then each caller kind alone.</summary>
    private Task<(bool Ended, bool Again)> IsolateAsync(Exchange exchange, Isolation isolation, CancellationToken cancellationToken)
        => isolation.ControlTaken
            ? SendKindAloneAsync(exchange, isolation, cancellationToken)
            : SendControlAsync(exchange, isolation, cancellationToken);

    // The proxy's own part of the refused page (its history, or an empty page): refused too, the proxy itself was refused
    // and every page waits; taken, the history leaves its store and the caller kinds go one at a time.
    private async Task<(bool Ended, bool Again)> SendControlAsync(Exchange exchange, Isolation isolation, CancellationToken cancellationToken)
    {
        ZabbixProxyDataPage refused = isolation.Page;
        int values = refused.History?.Count ?? 0;
        ZabbixProxyResponse control = values > 0
            ? await exchange(new ZabbixProxyDataPage(History: refused.History), isolation.More, isolation.Delay, cancellationToken).ConfigureAwait(false)
            : await exchange(ZabbixProxyDataPage.Empty, more: false, delay: 0, cancellationToken).ConfigureAwait(false);
        if (!control.IsSuccess)
        {
            if (control.UploadDisabled) Hold(control, values);
            else ProxyLog.ProxyUploadRefused(_logger, _proxy, values, isolation.Refusal ?? "no reason given");
            _isolating = null;
            return (true, false);
        }
        if (_history.Count > 0) SettleHistory(_history.Count - _unmapped);
        await HandleTasksAsync(control, cancellationToken).ConfigureAwait(false);
        if (control.UploadDisabled) Hold(control, values);
        return NextKind(isolation with { ControlTaken = true, Kind = -1 });
    }

    // One caller kind of the refused page on its own (0 discovery, 1 auto-registration, 2 availability, 3 tasks).
    private async Task<(bool Ended, bool Again)> SendKindAloneAsync(Exchange exchange, Isolation isolation, CancellationToken cancellationToken)
    {
        ZabbixProxyDataPage refused = isolation.Page;
        ZabbixProxyDataPage alone = isolation.Kind switch
        {
            0 => new ZabbixProxyDataPage(Discovery: refused.Discovery),
            1 => new ZabbixProxyDataPage(Autoregistration: refused.Autoregistration),
            2 => new ZabbixProxyDataPage(Availability: refused.Availability),
            _ => new ZabbixProxyDataPage(Tasks: refused.Tasks),
        };
        ZabbixProxyResponse response = await exchange(alone, more: false, delay: 0, cancellationToken).ConfigureAwait(false);
        Task settled = isolation.Kind switch
        {
            0 => AloneAsync(response, _stores.Discovery, _discovery, refused.Discovery?.Count ?? 0, ZabbixProxyTelemetry.Discovery, cancellationToken),
            1 => AloneAsync(response, _stores.Autoregistration, _autoregistration, refused.Autoregistration?.Count ?? 0, ZabbixProxyTelemetry.Autoregistration, cancellationToken),
            2 => AloneAsync(response, _stores.Availability, _availability, refused.Availability?.Count ?? 0, ZabbixProxyTelemetry.Availability, cancellationToken),
            _ => AloneAsync(response, _stores.Tasks, _tasks, refused.Tasks?.Count ?? 0, ZabbixProxyTelemetry.Tasks, cancellationToken),
        };
        await settled.ConfigureAwait(false);
        return NextKind(isolation);
    }

    // The next caller kind that carried rows, or the end of the isolation (and of the turn).
    private (bool Ended, bool Again) NextKind(Isolation position)
    {
        ZabbixProxyDataPage refused = position.Page;
        int[] counts = [refused.Discovery?.Count ?? 0, refused.Autoregistration?.Count ?? 0, refused.Availability?.Count ?? 0, refused.Tasks?.Count ?? 0];
        for (int kind = position.Kind + 1; kind < counts.Length; kind++)
        {
            if (counts[kind] == 0) continue;
            _isolating = position with { Kind = kind };
            return (false, false);
        }
        _isolating = null;
        return (true, false);
    }

    // One caller kind on its own, after the server took the proxy's control message: taken, it leaves its store; refused,
    // the server cannot read it and never will, so it is dropped, logged and counted rather than kept for ever.
    private async Task AloneAsync<T>(ZabbixProxyResponse response, ZabbixStore<T> store, List<ZabbixStoreEntry<T>> page, int rows, string kind, CancellationToken cancellationToken)
    {
        if (response.IsSuccess)
        {
            Settle(store, page, page.Count, kind);
            await HandleTasksAsync(response, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (page.Count > 0) store.Acknowledge(page[^1].Sequence);
        page.Clear();
        ProxyLog.ProxyPageInvalid(_logger, _proxy, rows, kind, response.Info ?? "no reason given");
        _telemetry.Values(_proxy, ZabbixProxyTelemetry.Invalid, rows, kind);
        _status?.Report(new ZabbixSendProgress(ZabbixSendOutcome.Rejected, rows, _time.GetUtcNow()));
    }

    private static bool CarriesCallerRows(ZabbixProxyDataPage page)
        => page.Discovery is { Count: > 0 } || page.Autoregistration is { Count: > 0 } || page.Availability is { Count: > 0 } || page.Tasks is { Count: > 0 };

    private Task HandleTasksAsync(ZabbixProxyResponse response, CancellationToken cancellationToken)
        => ZabbixProxyPaging.HandleTasksAsync(response, _taskHandler(), _stores.Tasks, _logger, _proxy, cancellationToken);

    private void Hold(ZabbixProxyResponse response, int values)
    {
        if (Interlocked.Exchange(ref _held, 1) == 1) return;
        ProxyLog.ProxyUploadHeld(_logger, _proxy, response.Info ?? "upload disabled");
        _telemetry.Values(_proxy, ZabbixProxyTelemetry.Held, values);
    }

    // The server has the history page (or none of it can be sent): it leaves the store.
    private void SettleHistory(int delivered)
    {
        if (_history.Count == 0) return;
        _stores.History.Acknowledge(_history[^1].Sequence);
        _history.Clear();
        if (_unmapped > 0)
        {
            ProxyLog.ProxyValuesUnmapped(_logger, _proxy, _unmapped);
            _telemetry.Values(_proxy, ZabbixProxyTelemetry.Unmapped, _unmapped);
            _status?.Report(new ZabbixSendProgress(ZabbixSendOutcome.Rejected, _unmapped, _time.GetUtcNow()));
            _unmapped = 0;
        }
        if (delivered == 0) return;
        _telemetry.Values(_proxy, ZabbixProxyTelemetry.Uploaded, delivered);
        _status?.Report(new ZabbixSendProgress(ZabbixSendOutcome.Delivered, delivered, _time.GetUtcNow()));
    }

    // The server has a discovery/auto-registration/availability/tasks page: it leaves its store. Unlike history these
    // kinds carry no configuration mapping, so every leased entry is always sent (delivered == page.Count).
    private void Settle<T>(ZabbixStore<T> store, List<ZabbixStoreEntry<T>> page, int delivered, string kind)
    {
        if (page.Count == 0) return;
        store.Acknowledge(page[^1].Sequence);
        page.Clear();
        if (delivered == 0) return;
        _telemetry.Values(_proxy, ZabbixProxyTelemetry.Uploaded, delivered, kind);
        _status?.Report(new ZabbixSendProgress(ZabbixSendOutcome.Delivered, delivered, _time.GetUtcNow()));
    }

    /// <summary>A refused page being taken apart: whether the control was taken, and which caller kind goes next (0–3).</summary>
    private sealed record Isolation(ZabbixProxyDataPage Page, bool More, long Delay, string? Refusal, bool ControlTaken = false, int Kind = -1);
}
