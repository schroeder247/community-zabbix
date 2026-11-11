// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell.Diagnostics;

/// <summary>
/// Routes <see cref="ILogger"/> records from a connection or a background session to the PowerShell host's streams.
/// While a command from this asynchronous flow is running (<see cref="ZabbixPipeline.Current"/>), each level maps to
/// one stream:
/// <list type="table">
/// <item><term>Trace</term><description><c>Write-Verbose</c> (<c>-Verbose</c>)</description></item>
/// <item><term>Debug</term><description><c>Write-Debug</c> (<c>-Debug</c>)</description></item>
/// <item><term>Information</term><description><c>Write-Information</c>, tagged <c>Zabbix</c>, the category and the event name</description></item>
/// <item><term>Warning</term><description><c>Write-Warning</c></description></item>
/// <item><term>Error, Critical</term><description>a non-terminating <c>ErrorRecord</c> whose FullyQualifiedErrorId carries the event name (<c>ZabbixLog.&lt;event name&gt;</c>)</description></item>
/// </list>
/// The resilience pipeline's records (the <c>Polly</c> category: one <c>Execution attempt</c> per try, and the retry
/// events) are written at <c>Trace</c> whatever their level, so <c>-Verbose</c> shows them: they narrate the tries of a
/// call whose own outcome is the command's result or error record, and at Warning an unreachable server would print one
/// warning per try before the error that says so.
/// </summary>
/// <remarks>
/// A logger never writes: it queues onto the running command's pipeline, and that command's pipeline thread writes. A
/// record logged with no command running — the common case for a session's background loop — goes to a bounded ring
/// (<see cref="Snapshot"/>) that a <c>Get-Zbx&lt;Session&gt;Log</c> cmdlet reads; a Warning or Error among those also
/// waits in a small escalation queue for the next command in this runspace to surface (<see cref="Flush"/>).
/// </remarks>
internal sealed class ZabbixPowerShellLoggerProvider : ILoggerProvider
{
    /// <summary>How many buffered records a <c>Get-Zbx&lt;Session&gt;Log</c> cmdlet can review; the oldest is dropped first.</summary>
    public const int BufferCapacity = 1024;

    /// <summary>How many unrouted Warning+ records wait for the next command; older ones are dropped first.</summary>
    public const int EscalationCapacity = 16;

    private readonly TimeProvider _time;
    private readonly ConcurrentQueue<ZabbixSessionLogEntry> _buffer = new();
    private int _bufferCount;

    private readonly Channel<LogWrite> _pending = Channel.CreateBounded<LogWrite>(
        new BoundedChannelOptions(EscalationCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    /// <summary>Creates the provider.</summary>
    /// <param name="time">The clock buffered entries are timestamped on; the system clock when <see langword="null"/>.</param>
    public ZabbixPowerShellLoggerProvider(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <inheritdoc/>
    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    /// <summary>The buffered records a background loop logged with no command running on its flow, oldest first.</summary>
    public IReadOnlyList<ZabbixSessionLogEntry> Snapshot() => [.. _buffer];

    /// <summary>Clears the buffer (a <c>Clear-Zbx&lt;Session&gt;Log</c>).</summary>
    public void Clear()
    {
        while (_buffer.TryDequeue(out _)) Interlocked.Decrement(ref _bufferCount);
    }

    /// <summary>Moves the Warning+ records buffered with no command running onto <paramref name="pipeline"/>.</summary>
    /// <param name="pipeline">The command's pipeline.</param>
    internal void Flush(ZabbixPipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        while (_pending.Reader.TryRead(out LogWrite? write)) pipeline.Post(write);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // false only on a second Dispose, when the channel is already complete
        _ = _pending.Writer.TryComplete();
        Clear();
    }

    private void Buffer(LogLevel level, EventId eventId, string category, string message, Exception? exception)
    {
        _buffer.Enqueue(new ZabbixSessionLogEntry(_time.GetUtcNow(), level, category, message));
        if (Interlocked.Increment(ref _bufferCount) > BufferCapacity && _buffer.TryDequeue(out _)) Interlocked.Decrement(ref _bufferCount);
        // DropOldest never refuses a write; false only once the provider is disposed, when nothing will flush it
        if (level >= LogLevel.Warning) _ = _pending.Writer.TryWrite(new LogWrite(level, eventId, category, message, exception));
    }

    /// <summary>Whether <paramref name="category"/> is the resilience pipeline's (<c>Polly</c>, ResilienceTelemetry's logger name).</summary>
    /// <param name="category">The logger's category.</param>
    internal static bool IsResilience(string category)
        => category == "Polly" || category.StartsWith("Polly.", StringComparison.Ordinal);

    private sealed class Logger(ZabbixPowerShellLoggerProvider provider, string category) : ILogger
    {
        private readonly bool _resilience = IsResilience(category);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
            => logLevel != LogLevel.None && (ZabbixPipeline.Current is { } pipeline ? pipeline.IsEnabled(Shown(logLevel)) : true);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (!IsEnabled(logLevel)) return;
            logLevel = Shown(logLevel);
            string message = formatter(state, exception);
            if (ZabbixPipeline.Current is { } pipeline)
            {
                pipeline.Post(new LogWrite(logLevel, eventId, category, message, exception));
                return;
            }
            provider.Buffer(logLevel, eventId, category, message, exception);
        }

        // The level a record is written at: a resilience record's is Trace (the type's remarks say why).
        private LogLevel Shown(LogLevel logLevel) => _resilience && logLevel != LogLevel.None ? LogLevel.Trace : logLevel;
    }
}
