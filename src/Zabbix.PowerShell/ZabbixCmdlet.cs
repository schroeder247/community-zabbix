// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics.CodeAnalysis;
using System.Management.Automation;
using Community.Zabbix.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Provides the base of every command of the module: the bridge from PowerShell's synchronous cmdlet contract to the
/// SDK's asynchronous API. <c>TryRun</c> starts the operation off the pipeline thread with <see cref="StopToken"/>, the
/// token the engine signals on a stop — so Ctrl+C reaches the socket and an interrupted write is abandoned by the
/// server's own request cancellation, not by the shell walking away — and drains what the operation writes (output,
/// log records, errors) on the pipeline thread, the only thread PowerShell lets write. A failure is written as one
/// <see cref="ErrorRecord"/> (<see cref="ZabbixErrors"/>) and the call answers <see langword="false"/>; a stop becomes
/// <see cref="PipelineStoppedException"/>, as the engine expects.
/// </summary>
public abstract class ZabbixCmdlet : PSCmdlet, IDisposable
{
    private CancellationTokenSource? _stop;

    /// <summary>
    /// The command's stop token: the engine's own <see cref="Cmdlet.PipelineStopToken"/> (PowerShell 7.6, signalled by
    /// Ctrl+C and <c>PowerShell.Stop()</c>), linked with the command's own cancellation for when a downstream command has
    /// taken all it wants (<c>Select-Object -First</c>). Read it on the pipeline thread.
    /// </summary>
    protected CancellationToken StopToken => (_stop ??= CancellationTokenSource.CreateLinkedTokenSource(PipelineStopToken)).Token;

    /// <summary>The context of the runspace this command runs in.</summary>
    protected static ZabbixContext ZabbixContext => ZabbixContext.Of(System.Management.Automation.Runspaces.Runspace.DefaultRunspace);

    /// <summary>
    /// The connection whose telemetry, log bridge and lease the next <c>TryRun</c> uses; <see langword="null"/>
    /// for a command that talks to no connected server.
    /// </summary>
    private protected virtual ZabbixConnection? RunConnection => null;

    /// <summary>Runs <paramref name="operation"/> with the stop token; on failure writes the error and answers <see langword="false"/>.</summary>
    /// <param name="operation">The work, given <see cref="StopToken"/>.</param>
    /// <param name="target">What the command acts on, for the error record.</param>
    /// <exception cref="PipelineStoppedException">The pipeline was stopped.</exception>
    protected bool TryRun(Func<CancellationToken, Task> operation, object? target)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Run<bool>(RunConnection, async (_, cancellationToken) =>
        {
            await operation(cancellationToken).ConfigureAwait(false);
            return true;
        }, out _, target);
    }

    /// <summary>Runs <paramref name="operation"/> with the stop token and hands its result back on the pipeline thread.</summary>
    /// <typeparam name="T">The result.</typeparam>
    /// <param name="operation">The work, given <see cref="StopToken"/>.</param>
    /// <param name="result">The result, when the operation succeeded.</param>
    /// <param name="target">What the command acts on, for the error record.</param>
    /// <exception cref="PipelineStoppedException">The pipeline was stopped.</exception>
    protected bool TryRun<T>(Func<CancellationToken, Task<T>> operation, [MaybeNullWhen(false)] out T result, object? target)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return Run(RunConnection, (_, cancellationToken) => operation(cancellationToken), out result, target);
    }

    /// <summary>
    /// Writes a stream to the pipeline one object at a time, as it arrives, and stops reading when the pipeline stops or
    /// a downstream command has enough (<c>Select-Object -First 1</c> cancels the read at the wire).
    /// </summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="source">The stream; enumerated with <see cref="StopToken"/>.</param>
    /// <param name="target">What the command acts on, for the error record.</param>
    protected bool WriteAll<T>(IAsyncEnumerable<T> source, object? target)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Run<bool>(RunConnection, async (pipeline, cancellationToken) =>
        {
            await foreach (T item in source.WithCancellation(cancellationToken).ConfigureAwait(false)) pipeline.Post(new OutputWrite(item));
            return true;
        }, out _, target);
    }

    /// <summary>
    /// Writes a list the SDK answers at once, one object at a time. The call is a function of the stop token, never a
    /// started <see cref="Task"/>: it starts inside the operation, after the connection's lease is taken and with this
    /// command's pipeline current, so the request's own log records reach this command's streams (<c>-Verbose</c>) and
    /// the API's error answer is this command's one error record, not a warning for the next command.
    /// </summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="source">The call, given <see cref="StopToken"/>.</param>
    /// <param name="target">What the command acts on, for the error record.</param>
    protected bool WriteAll<T>(Func<CancellationToken, Task<IReadOnlyList<T>>> source, object? target)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Run<bool>(RunConnection, async (pipeline, cancellationToken) =>
        {
            foreach (T item in await source(cancellationToken).ConfigureAwait(false)) pipeline.Post(new OutputWrite(item));
            return true;
        }, out _, target);
    }

    /// <summary>
    /// <see cref="WriteAll{T}(Func{CancellationToken, Task{IReadOnlyList{T}}}, object?)"/> for a client that streams:
    /// the stream is opened inside the operation with the stop token.
    /// </summary>
    /// <typeparam name="T">The element.</typeparam>
    /// <param name="source">The call, given <see cref="StopToken"/>.</param>
    /// <param name="target">What the command acts on, for the error record.</param>
    protected bool WriteAll<T>(Func<CancellationToken, IAsyncEnumerable<T>> source, object? target)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Run<bool>(RunConnection, async (pipeline, cancellationToken) =>
        {
            await foreach (T item in source(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false)) pipeline.Post(new OutputWrite(item));
            return true;
        }, out _, target);
    }

    /// <summary>
    /// <see cref="Cmdlet.ShouldProcess(string, string)"/>, except that <paramref name="force"/> skips its prompt (Learn,
    /// "Required Development Guidelines" RD04/RD05: a <c>Force</c> parameter "bypasses the calls to these two methods
    /// and proceeds with the operation … so that the cmdlet can be used in non-interactive scripts and hosts").
    /// <c>-Force</c> never overrides <c>-WhatIf</c> (or <c>$WhatIfPreference</c>) and never an explicit <c>-Confirm</c>:
    /// both still go through <see cref="Cmdlet.ShouldProcess(string, string)"/>, which previews or asks.
    /// </summary>
    /// <param name="target">The object changed.</param>
    /// <param name="action">The change.</param>
    /// <param name="force">Whether <c>-Force</c> was given.</param>
    protected bool ShouldProcessUnlessForced(string target, string action, bool force)
    {
        if (force && !WhatIfInEffect() && !ConfirmExplicitlyRequested())
        {
            WriteVerbose($"Performing the operation \"{action}\" on target \"{target}\" (-Force).");
            return true;
        }
        return ShouldProcess(target, action);
    }

    /// <summary>
    /// The confirmation contract for a change that warrants a second question (Learn, "Requesting Confirmation from
    /// Cmdlets": <c>ShouldContinue</c> is for an operation "potentially dangerous" beyond what <c>ConfirmImpact</c>
    /// says), used only by <c>Clear-ZbxHistory</c> and an import whose rules delete what the document does not name:
    /// <see cref="ShouldProcessUnlessForced"/> (so <c>-WhatIf</c> and <c>-Confirm</c> work), then
    /// <see cref="Cmdlet.ShouldContinue(string, string, ref bool, ref bool)"/> unless <paramref name="force"/> or an
    /// explicit <c>-Confirm:$false</c> — never by rewriting <c>$ConfirmPreference</c>. A host that cannot prompt (a CI
    /// runner, a non-interactive Pester run) makes <c>ShouldContinue</c> throw a bare
    /// <see cref="NullReferenceException"/>; it is turned here into a terminating, catchable <see cref="ErrorRecord"/>.
    /// </summary>
    /// <param name="target">The object changed.</param>
    /// <param name="action">The change.</param>
    /// <param name="force">Whether <c>-Force</c> was given.</param>
    /// <param name="yesToAll">The command's Yes-to-All answer, kept across calls.</param>
    /// <param name="noToAll">The command's No-to-All answer, kept across calls.</param>
    protected bool ShouldChange(string target, string action, bool force, ref bool yesToAll, ref bool noToAll)
    {
        if (!ShouldProcessUnlessForced(target, action, force)) return false;
        if (force || ConfirmExplicitlyDeclined()) return true;
        try
        {
            return ShouldContinue($"{action} {target}?", MyInvocation.MyCommand.Name, ref yesToAll, ref noToAll);
        }
        catch (Exception failure) when (failure is not PipelineStoppedException and not FlowControlException)
        {
            ThrowTerminatingError(new ErrorRecord(
                new InvalidOperationException(
                    $"{action} {target} needs confirmation but the host cannot prompt for it: pass -Force, or -Confirm:$false to skip the extra confirmation",
                    failure),
                "ZabbixConfirmationUnavailable", ErrorCategory.InvalidOperation, target));
            return false; // Unreachable: ThrowTerminatingError always throws.
        }
    }

    /// <summary>Whether <c>-WhatIf</c> is bound, or, when it is not, <c>$WhatIfPreference</c> is set.</summary>
    private bool WhatIfInEffect()
        => MyInvocation.BoundParameters.TryGetValue("WhatIf", out object? given) && given is SwitchParameter whatIf
            ? whatIf.IsPresent
            : LanguagePrimitives.IsTrue(GetVariableValue("WhatIfPreference"));

    /// <summary>Whether the caller bound <c>-Confirm</c> (or <c>-Confirm:$true</c>) explicitly.</summary>
    private bool ConfirmExplicitlyRequested()
        => MyInvocation.BoundParameters.TryGetValue("Confirm", out object? given) && given is SwitchParameter { IsPresent: true };

    /// <summary>Whether the caller bound <c>-Confirm:$false</c> explicitly, as opposed to leaving it unbound.</summary>
    private bool ConfirmExplicitlyDeclined()
        => MyInvocation.BoundParameters.TryGetValue("Confirm", out object? given) && given is SwitchParameter { IsPresent: false };

    /// <summary>
    /// Runs <paramref name="operation"/> on <paramref name="connection"/> (its lease, telemetry and log bridge) rather
    /// than <see cref="RunConnection"/>: for a command that opens the connection it talks to.
    /// </summary>
    private protected bool TryRun<T>(ZabbixConnection connection, Func<CancellationToken, Task<T>> operation, [MaybeNullWhen(false)] out T result, object? target)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(operation);
        return Run(connection, (_, cancellationToken) => operation(cancellationToken), out result, target);
    }

    private bool Run<T>(ZabbixConnection? connection, Func<ZabbixPipeline, CancellationToken, Task<T>> operation, [MaybeNullWhen(false)] out T result, object? target)
    {
        CancellationToken stopToken = StopToken;
        LastRunFailed = true;
        if (connection is not null && !connection.TryLease())
        {
            WriteError(ZabbixErrors.From(new ObjectDisposedException(connection.Name, $"the connection to {connection.Name} was closed; connect again"), target));
            result = default;
            return false;
        }

        using ZabbixPipeline pipeline = CreatePipeline();
        connection?.Logs.Flush(pipeline);
        // Every running session's own background loop logs through the same idiom (Zabbix.PowerShell.Diagnostics.
        // ZabbixPowerShellLoggerProvider): buffered while no command's pipeline is live, so its Warning+ records wait
        // here for the next command in this runspace — any command, exactly like a connection's own unrouted records.
        foreach (Diagnostics.IZabbixLoggedSession logged in ZabbixContext.Sessions.All.OfType<Diagnostics.IZabbixLoggedSession>())
            logged.Logs.Flush(pipeline);
        ZabbixOperation span = connection?.Telemetry.StartCommand(MyInvocation.MyCommand.Name, connection.Endpoint) ?? default;
        Outcome<T> outcome = Start(connection, pipeline, operation, stopToken);
        var held = new HeldRecords();
        try
        {
            foreach (PipelineWrite write in pipeline.Drain(stopToken)) Deliver(write, held);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            // Stopped: the operation has its cancellation and releases its lease when it returns; nothing waits for it.
            connection?.Telemetry.Finish(span, new OperationCanceledException(stopToken));
            throw new PipelineStoppedException();
        }
        catch (Exception failure) when (Abandon(connection, span, failure))
        {
            // Never taken: Abandon cancels the operation and answers false, so a downstream stop propagates as is.
            throw;
        }

        Exception? thrown = outcome.Failure;
        connection?.Telemetry.Finish(span, thrown);
        if (thrown is OperationCanceledException && stopToken.IsCancellationRequested) throw new PipelineStoppedException();
        Settle(thrown, held, target);
        LastRunFailed = thrown is not null;
        if (thrown is null)
        {
            result = outcome.Value!;
            return true;
        }
        result = default;
        return false;
    }

    /// <summary>
    /// The bridge's far side: runs <paramref name="operation"/> off the pipeline thread with <paramref name="pipeline"/>
    /// current (so the SDK's log records and the operation's writes reach this command), captures its result or
    /// failure, releases the connection's lease and completes the pipeline, which ends the pipeline thread's drain. The
    /// task never faults (the outcome holds the failure), so nothing observes it but the drain.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The cmdlet bridge: the operation runs in an execution context of its own, so the ambient pipeline never leaks onto the pipeline thread (decisions.md, \"Async meets a synchronous contract at three places\").")]
    private static Outcome<T> Start<T>(ZabbixConnection? connection, ZabbixPipeline pipeline, Func<ZabbixPipeline, CancellationToken, Task<T>> operation, CancellationToken stopToken)
    {
        var outcome = new Outcome<T>();
        outcome.Running = Task.Run(async () =>
        {
            pipeline.Enter();
            try
            {
                outcome.Value = await operation(pipeline, stopToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (outcome.Capture(failure))
            {
                // Captured: the pipeline thread writes it (or turns a stop into PipelineStoppedException).
            }
            finally
            {
                connection?.Release();
                pipeline.Complete();
            }
        }, CancellationToken.None);
        return outcome;
    }

    /// <summary>Writes one record the operation queued, on the pipeline thread, or holds it until the operation ends.</summary>
    private void Deliver(PipelineWrite write, HeldRecords held)
    {
        switch (write)
        {
            case OutputWrite output:
                if (Admits(output.Value)) WriteObject(output.Value);
                break;
            case LogWrite { Level: > LogLevel.Trace } attempt when Diagnostics.ZabbixPowerShellLoggerProvider.IsResilience(attempt.Category):
                // A resilience pipeline's attempt record (Polly logs a failed attempt at Warning or Error) is
                // telemetry about a retry, not this command's failure: the operation's own exception is. As an
                // error record, -ErrorAction Stop would make it the terminating error in place of that exception. The
                // module's bridge already writes it at Trace; a record posted at its own level is verbose too.
                WriteVerbose(attempt.Message);
                break;
            case LogWrite { Level: LogLevel.Warning, EventId.Id: ApiAnsweredError } answered:
                // The API's error answer, logged as it arrives: held until the operation ends, because when the
                // operation fails with that answer its error record says the same, and one failure is one
                // record, not a warning and an error.
                held.Answered.Add(answered);
                break;
            case LogWrite { Level: LogLevel.Warning } narrative when IsWireFailureNarrative(narrative.EventId.Id):
                // The server's own protocol narrating the failure the operation is about to throw (a TLS peer that
                // refused, an agent that could not be asked, a refused request): held, and written as a warning only
                // when the operation did not fail with it.
                held.Narratives.Add(narrative);
                break;
            case LogWrite { Level: >= LogLevel.Error, Exception: not null } error:
                // Held until the operation ends: the exception it carries may be the one the operation throws,
                // and one failure is one error record.
                held.Errors.Add(error);
                break;
            case LogWrite log:
                Write(log);
                break;
            case ErrorWrite error:
                WriteError(error.Record);
                break;
            case ProgressWrite progress:
                WriteProgress(progress.Record);
                break;
        }
    }

    /// <summary>
    /// Writes the operation's own failure first (under <c>-ErrorAction Stop</c> the first error record is the
    /// terminating one, and that must be the exception the operation threw, not a log record written on the way), then
    /// the held records that are not that same failure. A failure <see cref="Terminating"/> answers a record for ends the
    /// command with that record instead, after the held records.
    /// </summary>
    private void Settle(Exception? thrown, HeldRecords held, object? target)
    {
        ErrorRecord? terminating = thrown is null ? null : Terminating(thrown, target);
        if (thrown is not null && terminating is null) WriteError(ZabbixErrors.From(thrown, target));
        // A terminating record explains the refusal itself: the API's answers that led to it are not written again.
        foreach (LogWrite answered in terminating is null ? held.Answered : [])
            if (thrown is not ZabbixApiException { Method: { } method } || !answered.Message.StartsWith($"JSON-RPC {method} #", StringComparison.Ordinal)) Write(answered);
        // A failed operation's error record says what its narrative did: the narrative stays reachable with -Verbose.
        foreach (LogWrite narrative in held.Narratives)
        {
            if (thrown is null) Write(narrative);
            else WriteVerbose(narrative.Message);
        }
        foreach (LogWrite error in held.Errors)
            if (!ReferenceEquals(error.Exception, thrown)) WriteError(ZabbixErrors.FromLog(error));
        if (terminating is not null) ThrowTerminatingError(terminating);
    }

    /// <summary>
    /// The record that ends the command when the operation fails with <paramref name="failure"/>, or
    /// <see langword="null"/> to write it as a non-terminating error (the default): for a failure after which the
    /// command has nothing left to do (<c>Connect-ZbxServer</c> with a credential the server refused).
    /// </summary>
    /// <param name="failure">The operation's failure.</param>
    /// <param name="target">What the command was acting on.</param>
    private protected virtual ErrorRecord? Terminating(Exception failure, object? target) => null;

    /// <summary>Whether the last <c>TryRun</c>/<c>WriteAll</c> of this command failed (and wrote its error).</summary>
    private protected bool LastRunFailed { get; private set; }

    /// <summary>The progress activity id every command of the module reports under (one activity per command at a time).</summary>
    private protected const int ProgressActivity = 0x5A42;

    /// <summary>
    /// Reports how far an operation running off the pipeline thread is (Learn SD04, "Support the WriteProgress Method"):
    /// the record is queued and the pipeline thread writes it. A fresh record per call, since a queued record must not
    /// change under the writer.
    /// </summary>
    /// <param name="activity">The activity (the command's name).</param>
    /// <param name="status">What it is doing.</param>
    /// <param name="done">How many of <paramref name="total"/> are done; <paramref name="done"/> equal to <paramref name="total"/> completes the activity.</param>
    /// <param name="total">How many there are.</param>
    private protected static void Progress(string activity, string status, long done, long total)
    {
        var record = new ProgressRecord(ProgressActivity, activity, status)
        {
            PercentComplete = total <= 0 ? -1 : (int)Math.Min(100, 100 * done / total),
            RecordType = done >= total ? ProgressRecordType.Completed : ProgressRecordType.Processing,
        };
        ZabbixPipeline.Current?.Post(new ProgressWrite(record));
    }

    /// <summary>
    /// Whether an object the operation wrote reaches the pipeline: every object, unless a command narrows on this side
    /// what the server could not (a <c>Get-</c>'s <c>-Name</c> pattern with <c>?</c>, <see cref="ZabbixNamePattern"/>).
    /// Called on the pipeline thread.
    /// </summary>
    /// <param name="item">The object.</param>
    private protected virtual bool Admits(object? item) => true;

    // The SDK's "JSON-RPC {Method} #{RequestId} answered error …" (Zabbix ApiLog.ApiError).
    private const int ApiAnsweredError = 4001;

    // The wire's Warning events that narrate one exchange's failure (Zabbix.Wire WireLog): values refused (1007),
    // redirected and not followed (1008, 1111), a send stopped with values unconfirmed (1009), a TLS peer that refused
    // (1103), an agent that could not be asked or refused (1201, 1202), a server wire request refused (1301).
    private static bool IsWireFailureNarrative(int eventId) => eventId is 1007 or 1008 or 1009 or 1103 or 1111 or 1201 or 1202 or 1301;

    private bool Abandon(ZabbixConnection? connection, in ZabbixOperation span, Exception failure)
    {
        // A downstream command stopped us (Select-Object -First n throws out of WriteObject): cancel the read at the wire.
        _stop?.Cancel();
        connection?.Telemetry.Finish(span, failure is PipelineStoppedException ? new OperationCanceledException(failure.Message, failure) : failure);
        return false;
    }

    private ZabbixPipeline CreatePipeline() => new(
        verbose: Shows("Verbose", "VerbosePreference"),
        debug: Shows("Debug", "DebugPreference"),
        information: Preference("InformationAction", "InformationPreference") != ActionPreference.Ignore,
        warning: Preference("WarningAction", "WarningPreference") is not (ActionPreference.SilentlyContinue or ActionPreference.Ignore));

    private bool Shows(string switchName, string preferenceVariable)
        => MyInvocation.BoundParameters.TryGetValue(switchName, out object? given) && given is SwitchParameter { IsPresent: var present }
            ? present
            : Preference(null, preferenceVariable) is not (ActionPreference.SilentlyContinue or ActionPreference.Ignore);

    private ActionPreference Preference(string? commonParameter, string preferenceVariable)
    {
        if (commonParameter is not null && MyInvocation.BoundParameters.TryGetValue(commonParameter, out object? given) && given is ActionPreference action)
            return action;
        return LanguagePrimitives.TryConvertTo(GetVariableValue(preferenceVariable), System.Globalization.CultureInfo.InvariantCulture, out ActionPreference preference)
            ? preference
            : ActionPreference.SilentlyContinue;
    }

    private void Write(LogWrite log)
    {
        switch (log.Level)
        {
            case LogLevel.Trace:
                WriteVerbose(log.Message);
                break;
            case LogLevel.Debug:
                WriteDebug(log.Message);
                break;
            case LogLevel.Information:
                WriteInformation(log.Message, ["Zabbix", log.Category, log.EventId.Name ?? log.EventId.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
                break;
            case LogLevel.Warning:
                WriteWarning(log.Message);
                break;
            case LogLevel.Error or LogLevel.Critical:
                WriteError(ZabbixErrors.FromLog(log));
                break;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Releases the stop token.</summary>
    /// <param name="disposing">Whether this is <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing) _stop?.Dispose();
    }

    /// <summary>The records the drain holds until the operation ends (<see cref="Settle"/>).</summary>
    private sealed class HeldRecords
    {
        public List<LogWrite> Answered { get; } = [];

        public List<LogWrite> Errors { get; } = [];

        public List<LogWrite> Narratives { get; } = [];
    }

    private sealed class Outcome<T>
    {
        /// <summary>The operation's task: held, though it never faults, so the bridge owns what it starts.</summary>
        public Task? Running { get; set; }

        public T? Value { get; set; }

        public Exception? Failure { get; private set; }

        public bool Capture(Exception failure)
        {
            Failure = failure;
            return true;
        }
    }
}
