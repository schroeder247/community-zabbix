// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Diagnostics;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Threading.RateLimiting;
using Community.Zabbix.PowerShell.Diagnostics;
using Microsoft.Extensions.Logging;
using PS = System.Management.Automation.PowerShell;

namespace Community.Zabbix.PowerShell.Sessions;

/// <summary>
/// Runs a user's <see cref="ScriptBlock"/> safely from a session's background loop thread, on a dedicated
/// <see cref="Runspace"/> opened once for the session's lifetime.
/// </summary>
/// <remarks>
/// A scriptblock is only safe on the runspace that owns it, so every background-loop bridge (agent item handlers,
/// proxy task handlers) runs its scripts here.
/// <c>$script:</c> state persists across calls, and every invocation is serialized: two concurrent pipelines on one
/// local runspace throw <see cref="PSInvalidOperationException"/>. A <see cref="RunspacePool"/> would reset
/// <c>$script:</c> state whenever a different runspace is borrowed, and <see cref="SessionState.InvokeCommand"/> is
/// bound to the pipeline thread that opened it, which a session's loop (a <see cref="PeriodicTimer"/> tick, an agent's
/// check dispatch) is not.
/// <para>
/// The handlers are called concurrently (a passive agent answers each connection's check on its own), so the
/// serialization is a wait, and it is awaited: the gate is a <see cref="ConcurrencyLimiter"/> of one permit with an
/// oldest-first queue (<see cref="RateLimiter.AcquireAsync(int, CancellationToken)"/>, whose queued request leaves the
/// queue when its token is cancelled), and the pipeline runs through
/// <see cref="System.Management.Automation.PowerShell.InvokeAsync()"/> on the runspace's own pipeline thread. No pool
/// thread is held while a call waits for the one before it, and a call cancelled while it waits leaves at once without
/// running its script. A <see cref="Lock"/> held around a synchronous <see cref="System.Management.Automation.PowerShell.Invoke()"/>
/// would do both wrong: each waiting caller would pin a pool thread inside <c>lock</c>, and a cancellation that arrives
/// during the wait would not be seen until the script it should prevent had run.
/// </para>
/// </remarks>
internal sealed class ZabbixScriptRunspace : IAsyncDisposable
{
    private readonly Runspace _runspace;
    // One pipeline at a time, in arrival order; a waiter cancelled in the queue leaves it.
    private readonly ConcurrencyLimiter _gate = new(new ConcurrencyLimiterOptions
    {
        PermitLimit = 1,
        QueueLimit = int.MaxValue,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
    });
    private readonly ZabbixPowerShellTelemetry _telemetry;
    private readonly bool _ownsTelemetry;
    private readonly string _handlerKind;
    private readonly ActivityContext? _sessionContext;
    private readonly ILogger? _log;
    private int _disposed;

    /// <summary>Opens a dedicated runspace, with the default session state unless <paramref name="state"/> is given.</summary>
    /// <param name="state">The initial session state; the default when <see langword="null"/>.</param>
    /// <param name="telemetry">
    /// The owning session's telemetry, so every <see cref="InvokeAsync"/> gets a handler span/duration/outcome on the same
    /// meter as the session's own lifecycle spans; an owned instance (on its own meter, disposed with this runspace)
    /// when <see langword="null"/>.
    /// </param>
    /// <param name="handlerKind">What kind of handler this runspace invokes (<c>agent-item</c>, <c>discovery-tick</c>...), for the span name and tags.</param>
    /// <param name="sessionContext">The owning session's own lifecycle span context, for the handler span's link.</param>
    /// <param name="log">Where a failed invocation is logged (<see cref="ZabbixPowerShellLog.HandlerInvocationFailed"/>); none when <see langword="null"/>.</param>
    /// <param name="module">
    /// The sibling module whose session owns the handler (<see cref="Manifest(Type, string)"/> of Community.Zabbix.Agent or
    /// Community.Zabbix.Proxy), imported after Community.Zabbix so the handler can call that module's commands too; none when <see langword="null"/>.
    /// </param>
    public ZabbixScriptRunspace(InitialSessionState? state = null, ZabbixPowerShellTelemetry? telemetry = null, string handlerKind = "script",
        ActivityContext? sessionContext = null, ILogger? log = null, string? module = null)
    {
        // With the modules in it: a handler that calls Get-ZbxHost gets "not recognized" in a bare default runspace. The
        // manifests beside the module assemblies, as Import-Module loaded them: Community.Zabbix first (a sibling
        // requires it), then the sibling module that owns the session.
        if (state is null)
        {
            state = InitialSessionState.CreateDefault2();
            string[] manifests = [.. new[] { Manifest(typeof(ZabbixScriptRunspace), "Community.Zabbix"), module }.OfType<string>().Where(System.IO.File.Exists)];
            if (manifests.Length > 0) state.ImportPSModule(manifests);
        }
        _runspace = RunspaceFactory.CreateRunspace(state);
        _runspace.Open();
        _ownsTelemetry = telemetry is null;
        _telemetry = telemetry ?? new ZabbixPowerShellTelemetry(meters: null, time: null);
        _handlerKind = handlerKind;
        _sessionContext = sessionContext;
        _log = log;
    }

    /// <summary>The manifest of module <paramref name="name"/>, beside the assembly that declares <paramref name="typeInModule"/>.</summary>
    /// <param name="typeInModule">A type of the module's assembly.</param>
    /// <param name="name">The module's name (its manifest's file name).</param>
    internal static string Manifest(Type typeInModule, string name)
        => System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeInModule.Assembly.Location) ?? string.Empty, name + ".psd1");

    /// <summary>
    /// Runs <paramref name="scriptText"/> to completion, serialized against every other call on this runspace, inside
    /// its own root span (a handler runs off the SDK's own dispatch, not any command's flow) linked to the owning
    /// session's lifecycle span. Cancelling <paramref name="cancellationToken"/> while the call waits for the runspace
    /// ends the wait without running the script; cancelling it while the script runs stops the pipeline.
    /// </summary>
    /// <param name="scriptText">The script text (a <see cref="ScriptBlock"/>'s <see cref="object.ToString()"/>).</param>
    /// <param name="arguments">Positional arguments, bound as <c>$args</c>/<c>param()</c>.</param>
    /// <param name="cancellationToken">Ends the wait for the runspace, or stops the running pipeline.</param>
    /// <returns>The script's output.</returns>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled, while waiting or running.</exception>
    /// <exception cref="ZabbixScriptRunspaceException">The script wrote an error and did not throw.</exception>
    /// <exception cref="RuntimeException">The script threw (a terminating error).</exception>
    public async Task<PSDataCollection<PSObject>> InvokeAsync(string scriptText, IReadOnlyList<object?>? arguments, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptText);
        ZabbixHandlerOperation operation = _telemetry.StartHandler(_handlerKind, _sessionContext);
        try
        {
            using RateLimitLease turn = await _gate.AcquireAsync(1, cancellationToken).ConfigureAwait(false);
            // Not acquired: the limiter was disposed with this runspace while the call waited.
            ObjectDisposedException.ThrowIf(!turn.IsAcquired, this);
            // Acquired and cancelled at once: the token wins, the script does not run.
            cancellationToken.ThrowIfCancellationRequested();
            using var shell = PS.Create();
            shell.Runspace = _runspace;
            shell.AddScript(scriptText);
            if (arguments is not null)
            {
                foreach (object? argument in arguments) shell.AddArgument(argument);
            }
            PSDataCollection<PSObject> output;
            Task<PSDataCollection<PSObject>> running = shell.InvokeAsync();
            // Registered once the pipeline has started, so a cancellation in between stops it rather than finding
            // nothing to stop; StopAsync, so the cancelling thread does not wait for the pipeline to wind down.
            using (cancellationToken.Register(static state => _ = ((PS)state!).StopAsync(null, null), shell))
            {
                try
                {
                    output = await running.ConfigureAwait(false);
                }
                catch (PipelineStoppedException stopped) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(stopped.Message, stopped, cancellationToken);
                }
            }
            if (shell.HadErrors && shell.Streams.Error.Count > 0)
            {
                ErrorRecord last = shell.Streams.Error[^1];
                throw new ZabbixScriptRunspaceException(last.Exception?.Message ?? last.ToString(), last.Exception, last);
            }
            _telemetry.FinishHandler(operation, "succeeded");
            return output;
        }
        catch (OperationCanceledException failure)
        {
            _telemetry.FinishHandler(operation, "canceled", failure);
            throw;
        }
        catch (Exception failure)
        {
            _telemetry.FinishHandler(operation, "failed", failure);
            // The reason in the line itself: the host's streams show a warning's message, not its exception.
            if (_log is not null) ZabbixPowerShellLog.HandlerInvocationFailed(_log, _handlerKind, failure.Message, failure);
            throw;
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // Runspace.Dispose() closes any running pipeline synchronously; run it off this thread so a caller awaiting
        // disposal from a loop's own thread (the common case) never blocks itself on its own runspace.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        _runspace.Dispose();
        // Ends the wait of every call still queued for the runspace: each gets a lease it did not acquire.
        await _gate.DisposeAsync().ConfigureAwait(false);
        if (_ownsTelemetry) _telemetry.Dispose();
    }
}
