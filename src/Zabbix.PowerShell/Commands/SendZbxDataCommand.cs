// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Net;
using Community.Zabbix.PowerShell.Diagnostics;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Logging;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Sends values to a server or proxy's trapper (the sender protocol, <c>zabbix_sender</c>'s).</para>
/// <para type="description">
/// One value from <c>-HostName</c>/<c>-Key</c>/<c>-Value</c>, or a batch piped by property name (any object with
/// <c>Host</c>/<c>Key</c>/<c>Value</c> — and optionally <c>Clock</c>/<c>Ns</c> — properties, one send per pipeline
/// record collected into one request). The trapper is <c>-Server</c>, else the connection's own
/// (<c>Connect-ZbxServer -Trapper</c>), else the API's active HA node, exactly as the other protocol commands resolve it.
/// With <c>-Server</c> no connection is needed: the sender protocol carries no credentials, as <c>zabbix_sender</c>
/// sends without one. TLS is <c>-Tls</c> with <c>-PskIdentity</c>/<c>-Psk</c> or the certificate parameters, as
/// <c>zabbix_sender --tls-connect</c> takes it; without them, the connection's (<c>Connect-ZbxServer -TrapperTls</c>) when the
/// trapper is the connection's, and unencrypted when <c>-Server</c> names one.
/// </para>
/// </summary>
[Cmdlet(VerbsCommunications.Send, "ZbxData", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Low)]
[OutputType(typeof(ZabbixSenderResponse))]
public sealed class SendZbxDataCommand : ZabbixServerWireCmdlet
{
    /// <summary>The host the value belongs to, as configured in Zabbix.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipelineByPropertyName = true)]
    [Alias("Host")]
    [ValidateNotNullOrWhiteSpace]
    public string? HostName { get; set; }

    /// <summary>The item key.</summary>
    [Parameter(Mandatory = true, Position = 1, ValueFromPipelineByPropertyName = true)]
    [ValidateNotNullOrWhiteSpace]
    [ZabbixCompleter<ZabbixItem>]
    public string? Key { get; set; }

    /// <summary>The value; an empty string is a value, as <c>zabbix_sender -o ''</c> sends it (a text or log item takes it).</summary>
    [Parameter(Mandatory = true, Position = 2, ValueFromPipelineByPropertyName = true)]
    [ValidateNotNull]
    [AllowEmptyString]
    public string? Value { get; set; }

    /// <summary>When (a date, Unix seconds or <c>clock.ns</c>); now (to the nanosecond, never twice the same instant) when omitted.</summary>
    [Parameter(ValueFromPipelineByPropertyName = true)]
    [ZabbixClock]
    public DateTimeOffset? Clock { get; set; }

    /// <summary>Nanoseconds within the second of <c>-Clock</c>, to 100 ns; the clock's own when omitted.</summary>
    [Parameter(ValueFromPipelineByPropertyName = true)]
    public int? Ns { get; set; }

    /// <summary>
    /// The server or proxy's trapper: <c>host</c> or <c>host:port</c> (port 10051 when omitted; an IPv6 address with a
    /// port is bracketed, <c>[::1]:10051</c>). Several are the nodes of one HA cluster, tried in order. The connection's
    /// (<c>Connect-ZbxServer -Trapper</c>), else the active HA node's, when omitted; given, no connection is needed.
    /// </summary>
    [Parameter]
    [ValidateNotNullOrEmpty]
    [ZabbixEndPoint]
    public DnsEndPoint[]? Server { get; set; }

    /// <inheritdoc/>
    private protected override bool NeedsConnection => Server is null;

    /// <inheritdoc/>
    /// <remarks><c>-Server</c> may name another peer (a proxy's trapper), which the connection's TLS words are not for.</remarks>
    private protected override bool InheritsConnectionTls => Server is null;

    private readonly List<ZabbixValue> _items = [];

    private DateTimeOffset _stamped;

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (Clock is { } clock)
        {
            Add(Ns is { } ns ? ZabbixClockAttribute.WithNanoseconds(clock, ns) : clock);
            return;
        }
        // Now, to the 100 ns tick the clock gives, and never twice the same instant in one pipeline: a
        // value stamped at the whole second (ns 0) collides with the next one for the same item in that second on
        // history's (itemid, clock, ns) key, and the server skips it while its reply still says processed (7.0.31:
        // "skipped 1 duplicates").
        DateTimeOffset now = (ConnectedOrNull?.Time ?? ZabbixContext.Time).GetUtcNow();
        if (now <= _stamped) now = _stamped.AddTicks(1);
        _stamped = now;
        Add(now);
    }

    private void Add(DateTimeOffset time)
    {
        try
        {
            _items.Add(new ZabbixValue(HostName!, Key!, Value!, time));
        }
        catch (FormatException refused)
        {
            WriteError(new ErrorRecord(refused, "ZabbixItemKeyInvalid", ErrorCategory.InvalidArgument, Key));
        }
    }

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        if (_items.Count == 0)
        {
            WriteWarning("no values to send");
            return;
        }
        if (!ShouldProcess($"{_items.Count} value(s)", "Send trapper data")) return;
        // Read on the pipeline thread: the runspace's context is the thread's default runspace's.
        ZabbixConnection? connection = ConnectedOrNull;
        TimeProvider time = connection?.Time ?? ZabbixContext.Time;
        // With no connection the client logs to this command's own streams, as a connection's loggers do.
        using ZabbixCommandLoggers? own = connection is null ? new ZabbixCommandLoggers(time) : null;
        ILoggerFactory loggers = connection?.Loggers ?? own!.Factory;
        ZabbixConnectionOptions options = TrapperOptions;
        if (TryRun(async cancellationToken =>
            {
                IReadOnlyList<DnsEndPoint> nodes = await ResolveTrapperAsync(Server, cancellationToken).ConfigureAwait(false);
                string node = string.Join(", ", nodes.Select(ZabbixEndPoint.Format));
                // The connection lives for the send: disposing it releases what certificate mode read.
                using ZabbixWireLink link = ZabbixWireLinks.Open(nodes, options, time, loggers);
                using var wire = new ZabbixWireClient(link.AsExchange(followRedirects: true), new ZabbixWireOptions { Server = string.Join(';', nodes.Distinct().Select(ZabbixEndPoint.Format)) }, loggers, time);
                // In the client's own frame size (ZabbixWireOptions.DefaultBatchSize, zabbix_sender's VALUES_MAX 250), a call per
                // frame so the progress bar moves with each answer: one bad value or one timeout fails one request, not the
                // whole pipeline. The answers add up to the one record the command writes.
                const int batch = ZabbixWireOptions.DefaultBatchSize;
                ZabbixSenderResponse? total = null;
                for (int start = 0; start < _items.Count; start += batch)
                {
                    if (_items.Count > batch) Progress("Send-ZbxData", $"Sending {_items.Count} values to {node}", start, _items.Count);
                    ZabbixSenderResponse answer = await wire.SendAsync(_items.GetRange(start, Math.Min(batch, _items.Count - start)), cancellationToken).ConfigureAwait(false);
                    total = total is null ? answer : Sum(total, answer);
                }
                if (_items.Count > batch) Progress("Send-ZbxData", $"Sent {_items.Count} values to {node}", _items.Count, _items.Count);
                return total!;
            }, out ZabbixSenderResponse? response, Server is null ? Connected.Name : string.Join(", ", Server.Select(ZabbixEndPoint.Format))))
        {
            WriteObject(response);
        }
    }

    // The answers of several requests as the one the pipeline was: counts add up, the info line reads as the server's.
    private static ZabbixSenderResponse Sum(ZabbixSenderResponse total, ZabbixSenderResponse next)
    {
        int processed = total.Processed + next.Processed, failed = total.Failed + next.Failed, sent = total.Total + next.Total;
        TimeSpan spent = total.TimeSpent + next.TimeSpent;
        return new ZabbixSenderResponse(processed, failed, sent, spent, string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"processed: {processed}; failed: {failed}; total: {sent}; seconds spent: {spent.TotalSeconds:0.000000}"));
    }
}
