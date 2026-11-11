// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections;
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// An <c>-Id</c> parameter: refuses empty text at binding, which PowerShell would otherwise convert to 0, Zabbix's id for
/// nothing, and so read every object. A 0 given as a number or piped by property name still binds, and selects nothing by id.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixIdAttribute : ArgumentTransformationAttribute
{
    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        object? input = inputData is PSObject wrapped ? wrapped.BaseObject : inputData;
        if (input is string text)
        {
            Refuse(text);
        }
        else if (input is IEnumerable values)
        {
            foreach (object? value in values)
            {
                if ((value is PSObject item ? item.BaseObject : value) is string one) Refuse(one);
            }
        }
        return inputData;
    }

    private static void Refuse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentTransformationMetadataException("An id can't be empty.");
    }
}
