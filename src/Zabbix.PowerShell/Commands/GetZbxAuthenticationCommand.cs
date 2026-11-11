// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Gets the server's authentication settings (<c>authentication.get</c>).</para>
/// </summary>
[Cmdlet(VerbsCommon.Get, "ZbxAuthentication")]
[OutputType(typeof(ZabbixAuthenticationSettings))]
public sealed class GetZbxAuthenticationCommand : ZabbixSingletonGetCmdlet<ZabbixAuthenticationSettings>
{
    /// <inheritdoc/>
    protected override Task<ZabbixAuthenticationSettings> GetAsync(CancellationToken cancellationToken) => ZabbixClient.Authentication.GetAsync(cancellationToken: cancellationToken);
}
