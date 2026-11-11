// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets what a connection talks to: the server's API version, the connection's name, its
/// endpoint and how it authenticates.</para>
/// <para type="description">
/// Calls <c>apiinfo.version</c> and writes one <c>ZabbixApiInfo</c> object with <c>Version</c>, <c>Connection</c>
/// (the connection's name), <c>Endpoint</c> (the JSON-RPC URL) and <c>Credential</c> (how it authenticates, e.g. an
/// API token). Use <c>Get-ZbxApiVersion</c> when only the version is needed.
/// </para>
/// </summary>
/// <remarks>
/// <c>apiinfo.version</c> answers only the version. This command adds the endpoint and how the session authenticates —
/// everything one call about "what am I talking to" needs — composed from what
/// <see cref="ZabbixConnection"/>/<see cref="ZabbixClient"/> already carry rather than a new wire method (the API has
/// none: measured against <c>ui/include/classes/api/services/CAPIInfo.php</c>, 7.0–8.0).
/// </remarks>
[Cmdlet(VerbsCommon.Get, "ZbxApiInfo")]
[OutputType(typeof(ZabbixApiInfo))]
public sealed class GetZbxApiInfoCommand : ZabbixServerCmdlet
{
    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (TryRun(ZabbixClient.GetApiVersionAsync, out Version? version, Connected.Name))
            WriteObject(new ZabbixApiInfo(version, Connected.Name, Connected.Endpoint, Connected.Credential.ToString()!));
    }
}
