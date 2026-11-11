// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.Hosting;

/// <summary><c>ServerActive</c> must parse, and the store may not pace the schedule.</summary>
internal sealed class ZabbixActiveAgentServersValidator : IValidateOptions<ZabbixActiveAgentOptions>
{
    public ValidateOptionsResult Validate(string? name, ZabbixActiveAgentOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Overflow == BoundedChannelFullMode.Wait) return ValidateOptionsResult.Fail("Overflow cannot be Wait for an active agent: its checks are on a schedule; use DropOldest or DropWrite");
        // The server takes heartbeat_freq as 1..3600 whole seconds and drops anything else (trapper.c 8.0:1136,
        // ZBX_AGENT_HEARTBEAT_FREQUENCY_MAX); zero turns the heartbeat off here, as HeartbeatFrequency=0 does upstream.
        TimeSpan heartbeat = options.HeartbeatInterval;
        if (heartbeat != TimeSpan.Zero && (heartbeat < TimeSpan.FromSeconds(1) || heartbeat > TimeSpan.FromHours(1) || heartbeat.Ticks % TimeSpan.TicksPerSecond != 0))
            return ValidateOptionsResult.Fail($"HeartbeatInterval {heartbeat} must be zero (off) or whole seconds from 1 to 3600, as the server takes it");
        return ZabbixServerActiveValidation.Validate(options.ServerActive);
    }
}
