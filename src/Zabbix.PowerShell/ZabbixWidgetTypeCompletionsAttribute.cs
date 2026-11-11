// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>Completes the <see cref="ZabbixNamedValues.WidgetTypes"/> names.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixWidgetTypeCompletionsAttribute : ArgumentCompletionsAttribute
{
    /// <summary>The widget type names.</summary>
    public ZabbixWidgetTypeCompletionsAttribute()
        : base(ZabbixNamedValues.Names(ZabbixNamedValues.WidgetTypes))
    {
    }
}
