// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Proxy;

/// <summary>
/// Represents one service network discovery found on an address: a check of a discovery rule and what it saw.
/// </summary>
/// <param name="DcheckId">The check within the rule (<c>dcheckid</c>); not 0, because <c>dcheck</c> 0 is the row that closes a host.</param>
/// <param name="Port">The port checked.</param>
/// <param name="IsUp">Whether the service answered (<c>DOBJECT_STATUS_UP</c>) or not (<c>DOBJECT_STATUS_DOWN</c>).</param>
/// <param name="Value">What the check returned (an agent key's value, an SNMP OID's); <see langword="null"/> when nothing.</param>
public sealed record ZabbixProxyDiscoveredService(ulong DcheckId, int Port, bool IsUp, string? Value = null)
{
    /// <summary>The check within the rule; positive.</summary>
    public ulong DcheckId { get; init; } = DcheckId != 0 ? DcheckId : throw new ArgumentOutOfRangeException(nameof(DcheckId), DcheckId, "A check id is not 0: dcheck 0 is the row that closes a host.");
}
