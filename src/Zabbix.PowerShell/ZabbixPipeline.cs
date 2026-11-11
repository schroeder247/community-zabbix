// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Carries writes from an asynchronous operation to the pipeline thread. Every write an operation makes — output, a log
/// record, an error — is queued here and written by the pipeline thread that drains it, because PowerShell allows
/// <c>WriteObject</c> and its siblings only from that thread. The operation's asynchronous flow carries the pipeline as
/// an <see cref="AsyncLocal{T}"/>, so the SDK's log lines reach the command that caused them in whichever runspace it
/// runs, with no thread id shared between runspaces.
/// </summary>
/// <remarks>
/// <see cref="BlockingCollection{T}"/>, not <c>Channel&lt;T&gt;</c>: the consumer is PowerShell's pipeline thread,
/// which the cmdlet contract makes synchronous (<c>ProcessRecord</c> returns <see langword="void"/> and must not return
/// before its output is written). A channel's reader is asynchronous only, so draining one there needs a blocking wait
/// on <c>WaitToReadAsync</c> — the sync-over-async RS0030 bans. A thread that must block until items or completion is exactly
/// <see cref="BlockingCollection{T}.GetConsumingEnumerable(CancellationToken)"/>, with the stop token. Unbounded: a
/// producer never blocks a pool thread; read-ahead is bounded by the API, which answers a <c>.get</c> in one reply.
/// </remarks>
[System.Diagnostics.CodeAnalysis.SuppressMessage("ApiDesign", "RS0030:Do not use banned APIs", Justification = "The pipeline thread's blocking drain, one of the module's recorded bridges to the synchronous cmdlet contract (decisions.md, \"Async meets a synchronous contract at three places\").")]
internal sealed class ZabbixPipeline : IDisposable
{
    private static readonly AsyncLocal<ZabbixPipeline?> s_ambient = new();

    private readonly BlockingCollection<PipelineWrite> _writes = [];
    private readonly bool _verbose;
    private readonly bool _debug;
    private readonly bool _information;
    private readonly bool _warning;

    /// <summary>A pipeline writing the levels the command's preferences show.</summary>
    /// <param name="verbose">Whether Verbose is shown (Trace).</param>
    /// <param name="debug">Whether Debug is shown.</param>
    /// <param name="information">Whether Information is recorded.</param>
    /// <param name="warning">Whether Warning is shown.</param>
    public ZabbixPipeline(bool verbose, bool debug, bool information, bool warning)
    {
        _verbose = verbose;
        _debug = debug;
        _information = information;
        _warning = warning;
    }

    /// <summary>The pipeline of the command whose operation is running on this asynchronous flow, if any.</summary>
    public static ZabbixPipeline? Current => s_ambient.Value;

    /// <summary>Makes this the ambient pipeline of the calling asynchronous flow (and the flows it starts).</summary>
    public void Enter() => s_ambient.Value = this;

    /// <summary>
    /// Clears the ambient pipeline for the calling asynchronous flow (and the flows it starts) — for a command that
    /// spins up a session's own long-running loop and must not leave it pointed at a pipeline that will be disposed
    /// once the command returns (<see cref="Community.Zabbix.PowerShell.Diagnostics.ZabbixPowerShellLoggerProvider"/> then
    /// buffers the loop's own logging instead of routing it live). Async/await's context save/restore means this has
    /// no effect on the caller once the calling method returns.
    /// </summary>
    internal static void Reset() => s_ambient.Value = null;

    /// <summary>Whether a log record at <paramref name="level"/> would be written: the command's preferences, read once on the pipeline thread.</summary>
    /// <param name="level">The level.</param>
    public bool IsEnabled(LogLevel level) => level switch
    {
        LogLevel.Trace => _verbose,
        LogLevel.Debug => _debug,
        LogLevel.Information => _information,
        LogLevel.Warning => _warning,
        LogLevel.Error or LogLevel.Critical => true,
        _ => false,
    };

    /// <summary>Queues a write; a write after the operation completed (an abandoned operation's late log line) is dropped.</summary>
    /// <param name="write">The write.</param>
    public void Post(PipelineWrite write)
    {
        try
        {
            _writes.TryAdd(write);
        }
        catch (InvalidOperationException)
        {
            // Completed for adding, or disposed (ObjectDisposedException is one): the command has returned, so there
            // is no stream left to write to.
        }
    }

    /// <summary>No more writes: the drain ends once the queue is empty. Idempotent, and harmless after disposal.</summary>
    public void Complete()
    {
        try
        {
            _writes.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
            // The consumer gave up (a stop, a deadline) and disposed the queue; nobody is draining.
        }
    }

    /// <summary>The writes, as they come, on the calling (pipeline) thread; ends at <see cref="Complete"/>.</summary>
    /// <param name="stopping">The command's stop token: a stop ends the drain at once.</param>
    public IEnumerable<PipelineWrite> Drain(CancellationToken stopping) => _writes.GetConsumingEnumerable(stopping);

    /// <inheritdoc/>
    public void Dispose() => _writes.Dispose();
}
