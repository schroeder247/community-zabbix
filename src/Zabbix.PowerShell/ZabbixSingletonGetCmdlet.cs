// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell;

/// <summary>
/// Provides the base of every <c>Get-Zbx&lt;Singleton&gt;</c> cmdlet: <c>housekeeping</c>, <c>authentication</c>,
/// <c>autoregistration</c> and the frontend's <c>settings</c> are not collections — the server holds exactly one of
/// each, so there is no <c>-Id</c>, no <c>-Name</c>, no <c>-Filter</c>, no <see cref="ZabbixGetCmdlet{TEntity,TParameters}"/>
/// parameter-set machinery to select from. The derived class supplies the one call.
/// </summary>
/// <typeparam name="TEntity">The settings object <c>.get</c> returns.</typeparam>
public abstract class ZabbixSingletonGetCmdlet<TEntity> : ZabbixServerCmdlet
{
    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        if (TryRun(GetAsync, out TEntity? entity, Connected.Name)) WriteObject(entity);
    }

    /// <summary>The one call: <c>client.&lt;Singleton&gt;.GetAsync(cancellationToken)</c>.</summary>
    /// <param name="cancellationToken">The stop token.</param>
    protected abstract Task<TEntity> GetAsync(CancellationToken cancellationToken);
}
