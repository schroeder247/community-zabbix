// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Text;

namespace Community.Zabbix;

/// <summary>Represents the <c>filter</c> or <c>search</c> of a <c>.get</c> whose fields the API doesn't list: each is set by its wire name.</summary>
public sealed record ZabbixGetFields : ZabbixGetConditions
{
    /// <summary>Initializes conditions with nothing set.</summary>
    public ZabbixGetFields()
    {
    }

    /// <summary>Initializes conditions with fields set by their wire names.</summary>
    /// <param name="fields">The wire names and values; none when <see langword="null"/>.</param>
    public ZabbixGetFields(IEnumerable<KeyValuePair<string, ZabbixFieldValue>>? fields)
        : base(fields)
    {
    }

    /// <inheritdoc/>
    public bool Equals(ZabbixGetFields? other) => base.Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => base.GetHashCode();

    /// <inheritdoc/>
    protected override bool PrintMembers(StringBuilder builder) => base.PrintMembers(builder);

    /// <inheritdoc/>
    protected override IEnumerable<KeyValuePair<string, ZabbixFieldValue>> MemberConditions() => [];
}
