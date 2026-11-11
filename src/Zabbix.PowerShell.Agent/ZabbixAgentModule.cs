// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Management.Automation.Runspaces;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Manages the Community.Zabbix.Agent module's lifetime: <c>Import-Module Community.Zabbix.Agent</c> answers the listener's
/// references from the copy its folder carries for the running platform (<see cref="ZabbixModulePlatformAssemblies"/>);
/// <c>Remove-Module Community.Zabbix.Agent</c> stops the agent sessions it started in that runspace; the connections and the other
/// modules' sessions belong to the module Community.Zabbix and live on (its own removal closes the whole context).
/// </summary>
public sealed class ZabbixAgentModule : IModuleAssemblyInitializer, IModuleAssemblyCleanup
{
    // Registered once per process: the type initializer runs when the first import creates this initializer, and an
    // import again (Import-Module -Force) reuses the loaded type.
    static ZabbixAgentModule() => ZabbixModulePlatformAssemblies.Register(typeof(ZabbixAgentModule).Assembly);

    /// <inheritdoc/>
    /// <remarks>The type initializer has registered the platform's assemblies; nothing else to do.</remarks>
    public void OnImport()
    {
    }

    /// <inheritdoc/>
    public void OnRemove(PSModuleInfo psModuleInfo)
    {
        if (Runspace.DefaultRunspace is { } runspace && ZabbixContext.TryGet(runspace, out ZabbixContext? context))
            context.Sessions.RetireFrom(typeof(ZabbixAgentModule).Assembly);
    }
}
