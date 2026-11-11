// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;
using Community.Zabbix.Connector;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Reads a Zabbix connector real-time-export NDJSON stream — from <c>-Path</c> (a file the
/// connector writes to) or piped-in lines — and dispatches each parsed record: item values to
/// <c>-ItemHandler</c>, events to <c>-EventHandler</c>, when given (the record is <c>$_</c>); a record with no
/// matching handler is written to the pipeline.</para>
/// <para type="description">
/// Each line is one JSON record in the shape a
/// Zabbix connector sends: an item value (<c>ZabbixConnectorValue</c>) or an event (<c>ZabbixConnectorEvent</c>),
/// told apart by which id the line carries. This command is not an HTTP listener: a connector POSTs its lines to a
/// URL, so capture them with whatever receives that POST (or save them to a file) and pipe the lines in, or point
/// <c>-Path</c> at the file.
/// </para>
/// </summary>
/// <remarks>
/// Lines are parsed by <see cref="ZabbixConnectorFeed.Parse(string)"/>.
/// </remarks>
[Cmdlet(VerbsCommunications.Read, "ZbxConnectorStream", DefaultParameterSetName = "Pipeline")]
[OutputType(typeof(ZabbixConnectorRecord))]
public sealed class ReadZbxConnectorStreamCommand : ZabbixCmdlet
{
    /// <summary>NDJSON connector lines from the pipeline (one or more lines per call).</summary>
    [Parameter(ValueFromPipeline = true, Position = 0)]
    [AllowEmptyString]
    public string[]? InputObject { get; set; }

    /// <summary>
    /// A file (or named pipe) the connector writes NDJSON to, read after any pipeline input; wildcards that match one
    /// file name it.
    /// </summary>
    [Parameter(ParameterSetName = "Path", Mandatory = true)]
    [SupportsWildcards]
    [ValidateNotNullOrWhiteSpace]
    public string? Path { get; set; }

    /// <summary>A file (or named pipe) the connector writes NDJSON to, taken literally (no wildcards); read after any pipeline input.</summary>
    [Parameter(ParameterSetName = "LiteralPath", Mandatory = true)]
    [Alias("PSPath", "LP")]
    [ValidateNotNullOrWhiteSpace]
    public string? LiteralPath { get; set; }

    /// <summary>Invoked for each item-value record (<c>$_</c> is the <c>ZabbixConnectorValue</c>).</summary>
    [Parameter]
    public ScriptBlock? ItemHandler { get; set; }

    /// <summary>Invoked for each event record (<c>$_</c> is the <c>ZabbixConnectorEvent</c>).</summary>
    [Parameter]
    public ScriptBlock? EventHandler { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (InputObject is null) return;
        foreach (string chunk in InputObject)
        {
            if (string.IsNullOrEmpty(chunk)) continue;
            IReadOnlyList<ZabbixConnectorRecord> parsed;
            try
            {
                parsed = ZabbixConnectorFeed.Parse(chunk);
            }
            catch (JsonException failure)
            {
                WriteError(ZabbixErrors.From(failure, chunk));
                continue;
            }
            foreach (ZabbixConnectorRecord record in parsed) Dispatch(record);
        }
    }

    /// <inheritdoc/>
    protected override void EndProcessing()
    {
        if ((Path ?? LiteralPath) is not { } written) return;
        string[] matches;
        try
        {
            matches = Path is null ? [GetUnresolvedProviderPathFromPSPath(written)] : [.. GetResolvedProviderPathFromPSPath(written, out _)];
        }
        catch (Exception unresolved) when (unresolved is ItemNotFoundException or System.Management.Automation.DriveNotFoundException or ProviderNotFoundException)
        {
            WriteError(new ErrorRecord(unresolved, "ZabbixFileNotFound", ErrorCategory.ObjectNotFound, written));
            return;
        }
        if (matches.Length != 1)
        {
            WriteError(new ErrorRecord(new PSArgumentException($"{written} matches {matches.Length} files; the stream is read from one. Name one, or use -LiteralPath."),
                "ZabbixFileAmbiguous", ErrorCategory.InvalidArgument, written));
            return;
        }
        string resolved = matches[0];
        if (TryRun(cancellationToken => ReadAllAsync(resolved, cancellationToken), out IReadOnlyList<ZabbixConnectorRecord>? records, written))
            foreach (ZabbixConnectorRecord record in records!) Dispatch(record);
    }

    private static async Task<IReadOnlyList<ZabbixConnectorRecord>> ReadAllAsync(string path, CancellationToken cancellationToken)
    {
        var records = new List<ZabbixConnectorRecord>();
        FileStream stream = File.OpenRead(path);
        await using (stream.ConfigureAwait(false))
        {
            await foreach (ZabbixConnectorRecord record in ZabbixConnectorFeed.ReadAsync(stream, cancellationToken).ConfigureAwait(false))
                records.Add(record);
        }
        return records;
    }

    private void Dispatch(ZabbixConnectorRecord record)
    {
        ScriptBlock? handler = record switch
        {
            ZabbixConnectorValue => ItemHandler,
            ZabbixConnectorEvent => EventHandler,
            _ => null,
        };
        if (handler is null)
        {
            WriteObject(record);
            return;
        }
        var variables = new List<PSVariable> { new("_", record) };
        foreach (PSObject output in handler.InvokeWithContext(null, variables, record)) WriteObject(output.BaseObject);
    }
}
