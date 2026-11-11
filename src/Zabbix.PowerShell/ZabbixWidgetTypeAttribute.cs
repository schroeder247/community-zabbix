// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// A dashboard widget's <c>type</c>: a <see cref="ZabbixNamedValues.WidgetTypes"/> name becomes its wire id; any other
/// text passes through as written (a wire id, or a module widget's own id such as <c>custom.myplugin</c>, which no
/// table can list).
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixWidgetTypeAttribute : ArgumentTransformationAttribute
{
    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        object? input = inputData is PSObject wrapped ? wrapped.BaseObject : inputData;
        return input is string text && ZabbixNamedValues.WidgetTypes.TryGetValue(text.Trim(), out string? id) ? id : input;
    }
}
