// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Completes an object's names against the server, keyed on the entity through PowerShell's
/// <see cref="IArgumentCompleterFactory"/> seam. <c>[ZabbixCompleter&lt;ZabbixHost&gt;]</c> on a parameter completes host
/// names.
/// </summary>
/// <typeparam name="TEntity">The object whose names complete.</typeparam>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixCompleterAttribute<TEntity> : ArgumentCompleterFactoryAttribute
{
    /// <inheritdoc/>
    public override IArgumentCompleter Create() => new ZabbixNameCompleter<TEntity>(ZabbixEntities.Of<TEntity>());
}
