// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Net;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Creates or updates the Zabbix host, agent interface and items a running agent session (or an
/// explicit key spec) answers, idempotent by host name.</para>
/// <para type="description">
/// Ensures the host (in <c>-HostGroup</c>), one agent
/// interface (<c>-InterfaceAddress</c>:<c>-InterfacePort</c>), and one item per handler key (a Zabbix agent item polled
/// on that interface for a passive session, an active check otherwise) of the session named by <c>-Session</c>, or per
/// key in <c>-Key</c>. A passive session's interface port is its listener's unless <c>-InterfacePort</c> says otherwise. A second run reuses whatever already exists (by
/// host name, then by item key) rather than reshaping it. With <c>-Session</c>, what was created is remembered on that
/// session, so <c>Get-</c>, <c>Publish-</c> and <c>Clear-ZbxAgentHost</c> can act on it later; the <c>-Key</c> form
/// creates the host and items once and remembers nothing. Writes the host's status only with <c>-PassThru</c>.
/// Supports <c>-WhatIf</c>/<c>-Confirm</c> (ConfirmImpact Medium).
/// </para>
/// </summary>
/// <remarks>
/// Goes through the generated <c>Hosts</c>/<c>HostInterfaces</c>/<c>Items</c> clients; the bookkeeping lives on the
/// session in <see cref="ZabbixContext.Sessions"/>, never in static state.
/// </remarks>
[Cmdlet(VerbsData.Initialize, "ZbxAgentHost", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium, DefaultParameterSetName = ParameterSets.Session)]
[OutputType(typeof(ZabbixAgentHostStatus))]
public sealed class InitializeZbxAgentHostCommand : ZabbixServerCmdlet
{
    /// <summary>
    /// The running agent session whose handler keys become items, and which remembers what was created on the server.
    /// </summary>
    [Parameter(ParameterSetName = ParameterSets.Session, Mandatory = true, Position = 0)]
    [ValidateNotNullOrWhiteSpace]
    public string? Session { get; set; }

    /// <summary>
    /// The item keys to create, when there is no running session to read them from. Nothing is remembered about what
    /// was created, so <c>Get-</c>, <c>Publish-</c> and <c>Clear-ZbxAgentHost</c> cannot act on it later.
    /// </summary>
    [Parameter(ParameterSetName = ParameterSets.Spec, Mandatory = true)]
    [ValidateNotNullOrEmpty]
    public string[]? Key { get; set; }

    /// <summary>The monitored host's technical name. Default: the session's own <c>Hostname</c>.</summary>
    [Parameter(Position = 1)]
    [ValidateNotNullOrWhiteSpace]
    public string? HostName { get; set; }

    /// <summary>Host group for the monitored host.</summary>
    [Parameter]
    [ValidateNotNullOrWhiteSpace]
    public string HostGroup { get; set; } = ZabbixAgentHostProvisioning.DefaultGroup;

    /// <summary>
    /// The address the server reaches the agent interface on: an IP address, or a DNS name the server resolves. A
    /// passive agent is polled there, so it must be the address of this machine as the server sees it. Default:
    /// the address a passive session's listener is bound to when it is bound to one, otherwise <c>127.0.0.1</c>.
    /// </summary>
    [Parameter]
    [ValidateNotNullOrWhiteSpace]
    public string? InterfaceAddress { get; set; }

    /// <summary>The agent interface's port. Default: a passive session's listener port, otherwise 10050.</summary>
    [Parameter]
    [ValidateRange(1, 65535)]
    public int? InterfacePort { get; set; }

    /// <summary>
    /// Value type for a newly created item whose key is not a built-in one. Default: <c>Text</c>, which stores any
    /// answer; give <c>Unsigned</c> or <c>Float</c> for keys that answer numbers, to graph them. The built-in keys
    /// always get their own (<c>agent.ping</c> <c>Unsigned</c>; <c>agent.version</c>, <c>agent.hostname</c> and
    /// <c>system.uname</c> <c>Character</c>). Ignored for an item that already exists.
    /// </summary>
    [Parameter]
    public ZabbixItemValueType ValueType { get; set; } = ZabbixAgentHostProvisioning.DefaultValueType;

    /// <summary>Emits the resulting host status.</summary>
    [Parameter]
    public SwitchParameter PassThru { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        ZabbixAgentSession? session = null;
        if (ParameterSetName == ParameterSets.Session)
        {
            if (!ZabbixContext.Sessions.TryGet(Session!, out session))
            {
                WriteError(new ErrorRecord(new ItemNotFoundException($"no agent session is named '{Session}'"), "ZabbixAgentNotFound", ErrorCategory.ObjectNotFound, Session));
                return;
            }
        }

        string? hostName = HostName ?? session?.Hostname;
        if (string.IsNullOrWhiteSpace(hostName))
        {
            WriteError(new ErrorRecord(new PSArgumentException("No -HostName and the session carries none."), "ZabbixAgentHostNoHostName", ErrorCategory.InvalidArgument, null));
            return;
        }
        IReadOnlyList<string> keys = session is not null ? session.ProvisionedKeys : Key!;
        if (!ShouldProcess(hostName, "Initialize agent host")) return;

        // A passive agent is polled where its listener is; an active one is never polled, so its interface only names it.
        IPEndPoint? listening = session?.Listener?.LocalEndPoint as IPEndPoint;
        string address = InterfaceAddress
            ?? (listening is { } bound && !bound.Address.Equals(IPAddress.Any) && !bound.Address.Equals(IPAddress.IPv6Any) ? bound.Address.ToString() : "127.0.0.1");
        int port = InterfacePort ?? listening?.Port ?? ZabbixAgentHostProvisioning.DefaultInterfacePort;
        if (!TryRun(async cancellationToken =>
        {
            ZabbixAgentHostRecord record = await ZabbixAgentHostProvisioning.EnsureHostAsync(ZabbixClient, hostName, HostGroup, address, port, cancellationToken).ConfigureAwait(false);
            Dictionary<string, ulong> itemIds = await ZabbixAgentHostProvisioning.EnsureItemsAsync(
                ZabbixClient, record.HostId, keys, ValueType, ZabbixAgentHostProvisioning.ItemTypeOf(session), record.InterfaceId, cancellationToken).ConfigureAwait(false);
            foreach ((string key, ulong itemId) in itemIds) record.ItemIds[key] = itemId;
            return record;
        }, out ZabbixAgentHostRecord? result, hostName))
        {
            return;
        }

        if (session is not null) session.HostRecord = result;
        if (!PassThru) return;
        TryRun(cancellationToken => ZabbixAgentHostProvisioning.ReadStatusAsync(
            ZabbixClient, result!.HostId, session is not null ? new HashSet<string>(session.ProvisionedKeys, StringComparer.Ordinal) : null, cancellationToken),
            out ZabbixAgentHostStatus? status, hostName);
        if (status is not null) WriteObject(status);
    }

    private static class ParameterSets
    {
        public const string Session = "Session";
        public const string Spec = "Spec";
    }
}
