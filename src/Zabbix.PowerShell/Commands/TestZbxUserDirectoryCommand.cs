// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Text.Json;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Tests LDAP/SAML connectivity and provisioning for a user directory (<c>userdirectory.test</c>).</para>
/// <para type="description"><c>-Request</c> is the typed <c>ZabbixUserDirectoryTestRequest</c>, cast from a hashtable — the connection, bind, search and test credentials the frontend's own test dialog sends.</para>
/// </summary>
[Cmdlet(VerbsDiagnostic.Test, "ZbxUserDirectory")]
[OutputType("Community.Zabbix.PowerShell.UserDirectoryTestResult")]
public sealed class TestZbxUserDirectoryCommand : ZabbixServerCmdlet
{
    /// <summary>The test request.</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [ValidateNotNull]
    public ZabbixUserDirectoryTestRequest? Request { get; set; }

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (TryRun(cancellationToken => ZabbixClient.UserDirectories.TestAsync(Request!, cancellationToken), out JsonElement result, Request))
            WriteObject(ZabbixJsonShape.ToShell(result, "Community.Zabbix.PowerShell.UserDirectoryTestResult"));
    }
}
