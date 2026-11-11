// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Hosting;

/// <summary>
/// <c>ServerActive</c> must parse, and a typed poll or a meter needs a host to report for — checked at start, so a typo
/// stops the host rather than the first send.
/// </summary>
internal sealed class ZabbixPushAgentServersValidator : IValidateOptions<ZabbixPushAgentOptions>
{
    public ValidateOptionsResult Validate(string? name, ZabbixPushAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        // An agent's sources run on a cadence: a slow sender must cost counted drops, never a late poll.
        if (options.Overflow == BoundedChannelFullMode.Wait) return ValidateOptionsResult.Fail("Overflow cannot be Wait for an agent: its sources are on a schedule; use DropOldest or DropWrite");
        if (options.ReportsForHost && string.IsNullOrWhiteSpace(options.HostName))
            return ValidateOptionsResult.Fail("HostName is required: the agent's typed polls and meters report for it");
        return ZabbixServerActiveValidation.Validate(options.ServerActive);
    }
}
