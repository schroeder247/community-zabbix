// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Compliance.Classification;

namespace Community.Zabbix;

/// <summary>Marks a member or parameter as <see cref="ZabbixDataClassifications.Personal"/>.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class ZabbixPersonalAttribute : DataClassificationAttribute
{
    /// <summary>Classifies as personal data.</summary>
    public ZabbixPersonalAttribute() : base(ZabbixDataClassifications.Personal)
    {
    }
}
