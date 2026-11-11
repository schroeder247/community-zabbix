// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Manages the module's lifetime in a runspace: <c>Import-Module</c> creates the runspace's <see cref="ZabbixContext"/>
/// (and with it the telemetry decision, read from the environment at import), <c>Remove-Module</c>
/// closes it. Both run on the importing runspace's pipeline thread, where <see cref="Runspace.DefaultRunspace"/> is
/// that runspace. Importing also lends this module's folder to the sibling modules that require it
/// (<see cref="ZabbixModuleDependencies"/>), and refuses a session that already holds an older release of an assembly
/// this module ships under the same assembly version.
/// </summary>
public sealed class ZabbixModule : IModuleAssemblyInitializer, IModuleAssemblyCleanup
{
    /// <inheritdoc/>
    public void OnImport()
    {
        ZabbixModuleDependencies.Register();
        ZabbixModuleDependencies.Verify();
        if (Runspace.DefaultRunspace is { } runspace) _ = ZabbixContext.Of(runspace);
    }

    /// <inheritdoc/>
    public void OnRemove(PSModuleInfo psModuleInfo)
    {
        if (Runspace.DefaultRunspace is { } runspace) ZabbixContext.Close(runspace);
    }
}
