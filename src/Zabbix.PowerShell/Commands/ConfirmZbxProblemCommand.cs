// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Acknowledges problems, and adds a message, changes the severity, suppresses or closes them (<c>event.acknowledge</c>).</para>
/// </summary>
/// <remarks>
/// <c>event.acknowledge</c> takes one <c>action</c> bit mask (<c>ZBX_PROBLEM_UPDATE_*</c>, <see cref="ZabbixEventActions"/>);
/// each switch or value here sets its bit. The method validates the same rules in 7.0, 7.4 and 8.0 (CEvent.php
/// <c>validateAcknowledge</c> is unchanged across the three; 8.0's new bits, maintenance suppression and CEP, are written
/// by the server only and refused by the method), so the command needs no version gate. The pairs the method refuses
/// together (acknowledge and unacknowledge, suppress and unsuppress, close with either suppression, cause and symptom,
/// CEvent.php 8.0:1502-1531) are refused here before anything is sent, by parameter.
/// </remarks>
[Cmdlet(VerbsLifecycle.Confirm, "ZbxProblem", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium)]
[OutputType(typeof(ZabbixProblem))]
public sealed class ConfirmZbxProblemCommand : ZabbixServerCmdlet
{
    /// <summary>
    /// The problems: event ids, names, problems piped from <c>Get-ZbxProblem</c>, or events piped from
    /// <c>Get-ZbxEvent</c> (a problem is its event, and both carry the same <c>EventId</c>).
    /// </summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [Alias("EventId", "Id")]
    [ZabbixProblemReference]
    [ZabbixCompleter<ZabbixProblem>]
    public ZabbixReference<ZabbixProblem>[]? InputObject { get; set; }

    /// <summary>Adds this message to the problems' update history (at most 2048 characters).</summary>
    [Parameter]
    [ValidateLength(1, 2048)]
    public string? Message { get; set; }

    /// <summary>Changes the problems' severity to this one.</summary>
    [Parameter]
    public ZabbixSeverity? Severity { get; set; }

    /// <summary>Closes the problems; their triggers must allow manual close.</summary>
    [Parameter]
    public SwitchParameter Close { get; set; }

    /// <summary>Suppresses the problems: until <c>-SuppressUntil</c> when it is given, otherwise until they are unsuppressed.</summary>
    [Parameter]
    public SwitchParameter Suppress { get; set; }

    /// <summary>Suppresses the problems until this time (a date, Unix seconds, or a clock); implies <c>-Suppress</c>.</summary>
    [Parameter]
    [ZabbixClock]
    public DateTimeOffset? SuppressUntil { get; set; }

    /// <summary>Ends a manual suppression of the problems.</summary>
    [Parameter]
    public SwitchParameter Unsuppress { get; set; }

    /// <summary>Removes the problems' acknowledgement instead of acknowledging them.</summary>
    [Parameter]
    public SwitchParameter Unacknowledge { get; set; }

    /// <summary>
    /// Makes only the other changes given (a message, a severity, a suppression, a close) and leaves the problems'
    /// acknowledgement as it is.
    /// </summary>
    [Parameter]
    public SwitchParameter NoAcknowledge { get; set; }

    /// <summary>Marks the problems as symptoms of the problem with this event id.</summary>
    [Parameter]
    public ulong? CauseEventId { get; set; }

    /// <summary>Marks symptom problems as cause problems again.</summary>
    [Parameter]
    public SwitchParameter ConvertToCause { get; set; }

    /// <summary>Writes the problems, read back after the change (resolved ones included); without it the command writes nothing.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    private readonly List<ZabbixReference<ZabbixProblem>> _problems = [];
    private ZabbixEventActions _action;

    /// <inheritdoc/>
    protected override void BeginProcessing()
    {
        base.BeginProcessing();
        Refuse(Unacknowledge && NoAcknowledge, nameof(NoAcknowledge), "-Unacknowledge already leaves the acknowledgement out; give one of -Unacknowledge and -NoAcknowledge");
        Refuse((Suppress || SuppressUntil is not null) && Unsuppress, nameof(Unsuppress), "-Suppress and -Unsuppress cannot be used together");
        Refuse(Close && (Suppress || SuppressUntil is not null || Unsuppress), nameof(Close), "-Close cannot be used with -Suppress, -SuppressUntil or -Unsuppress: Zabbix refuses closing and suppressing in one update");
        Refuse(ConvertToCause && CauseEventId is not null, nameof(ConvertToCause), "-ConvertToCause and -CauseEventId cannot be used together");
        _action = ActionOf();
        Refuse(_action == ZabbixEventActions.None, nameof(NoAcknowledge), "-NoAcknowledge leaves nothing to change: give -Message, -Severity, -Close, -Suppress, -Unsuppress, -CauseEventId or -ConvertToCause as well");
    }

    /// <inheritdoc/>
    protected override void ProcessRecord() => _problems.AddRange(InputObject!);

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        if (!TryConfirm(_problems, Describe(_action), force: false, out IReadOnlyList<ulong> ids)) return;
        var request = new ZabbixEventAcknowledgeRequest
        {
            EventIds = ids,
            Action = _action,
            Message = Message,
            Severity = Severity,
            // 0 is Zabbix's "until unsuppressed" (CEvent.php 8.0:1649, checkIfValidTime, takes 0 or a time in the future).
            SuppressUntil = _action.HasFlag(ZabbixEventActions.Suppress) ? SuppressUntil ?? DateTimeOffset.UnixEpoch : null,
            CauseEventId = CauseEventId,
        };
        if (!TryRun(async cancellationToken =>
        {
            IReadOnlyList<ulong> changed = await ZabbixClient.Events.AcknowledgeAsync(request, cancellationToken).ConfigureAwait(false);
            if (!PassThru || changed.Count == 0) return (IReadOnlyList<ZabbixProblem>)[];
            // Recent: a problem the update closed is resolved, and problem.get leaves resolved problems out without it.
            return await ListAsync(ZabbixClient.Problems.GetAsync(new ZabbixProblemGetParameters { EventIds = changed, Recent = true }, cancellationToken), cancellationToken).ConfigureAwait(false);
        }, out IReadOnlyList<ZabbixProblem>? problems, request)) return;
        foreach (ZabbixProblem problem in problems) WriteObject(problem);
    }

    // The action bit mask the parameters ask for: acknowledge unless -Unacknowledge or -NoAcknowledge says otherwise.
    private ZabbixEventActions ActionOf()
    {
        ZabbixEventActions action = ZabbixEventActions.None;
        if (Unacknowledge) action |= ZabbixEventActions.Unacknowledge;
        else if (!NoAcknowledge) action |= ZabbixEventActions.Acknowledge;
        if (Message is not null) action |= ZabbixEventActions.Message;
        if (Severity is not null) action |= ZabbixEventActions.Severity;
        if (Close) action |= ZabbixEventActions.Close;
        if (Suppress || SuppressUntil is not null) action |= ZabbixEventActions.Suppress;
        if (Unsuppress) action |= ZabbixEventActions.Unsuppress;
        if (CauseEventId is not null) action |= ZabbixEventActions.RankToSymptom;
        if (ConvertToCause) action |= ZabbixEventActions.RankToCause;
        return action;
    }

    // What the confirmation says the update does, in the order the frontend's update form lists the changes.
    private string Describe(ZabbixEventActions action)
    {
        var parts = new List<string>();
        if (action.HasFlag(ZabbixEventActions.Acknowledge)) parts.Add("Acknowledge");
        if (action.HasFlag(ZabbixEventActions.Unacknowledge)) parts.Add("Unacknowledge");
        if (action.HasFlag(ZabbixEventActions.Message)) parts.Add("add a message");
        if (action.HasFlag(ZabbixEventActions.Severity)) parts.Add("change the severity to " + Severity);
        if (action.HasFlag(ZabbixEventActions.Suppress))
            parts.Add(SuppressUntil is { } until ? "suppress until " + until.LocalDateTime.ToString("g", CultureInfo.CurrentCulture) : "suppress");
        if (action.HasFlag(ZabbixEventActions.Unsuppress)) parts.Add("unsuppress");
        if (action.HasFlag(ZabbixEventActions.RankToSymptom)) parts.Add("mark as a symptom of event " + CauseEventId);
        if (action.HasFlag(ZabbixEventActions.RankToCause)) parts.Add("mark as a cause");
        if (action.HasFlag(ZabbixEventActions.Close)) parts.Add("close");
        string text = string.Join(", ", parts);
        return char.ToUpperInvariant(text[0]) + text[1..];
    }

    private void Refuse(bool refused, string parameter, string message)
    {
        if (refused) ThrowTerminatingError(ZabbixErrors.From(new ZabbixParameterException("ZabbixParameterInvalid", parameter, message), parameter));
    }
}

/// <summary>
/// Binds a problem reference as <see cref="ZabbixReferenceAttribute{TEntity}"/> does, and also an event read with
/// <c>Get-ZbxEvent</c>: <c>event.acknowledge</c> takes event ids, and a problem's id is its event's.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixProblemReferenceAttribute : ArgumentTransformationAttribute
{
    private static readonly ZabbixReferenceAttribute<ZabbixProblem> s_problems = new();

    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
        => s_problems.Transform(engineIntrinsics, Unwrap(inputData) switch
        {
            ZabbixEvent single => Of(single),
            System.Collections.IEnumerable many and not string => many.Cast<object?>().Select(item => Unwrap(item) is ZabbixEvent one ? Of(one) : item).ToArray(),
            _ => inputData,
        });

    private static object? Unwrap(object? value) => value is PSObject wrapped ? wrapped.BaseObject : value;

    // An event read without its id (an -Output that left eventid out) stays the event, and the reference binder refuses it.
    private static object Of(ZabbixEvent e) => e.EventId is { } id ? ZabbixReference<ZabbixProblem>.ById(id) : e;
}
