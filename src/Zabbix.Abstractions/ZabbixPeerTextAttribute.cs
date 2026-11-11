// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Compliance.Classification;

namespace Community.Zabbix;

/// <summary>Marks a member or parameter as <see cref="ZabbixDataClassifications.PeerText"/>: free text a peer wrote, which can echo a request's values.</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter, AllowMultiple = false)]
public sealed class ZabbixPeerTextAttribute : DataClassificationAttribute
{
    /// <summary>Classifies as a peer's free text.</summary>
    public ZabbixPeerTextAttribute() : base(ZabbixDataClassifications.PeerText)
    {
    }
}
