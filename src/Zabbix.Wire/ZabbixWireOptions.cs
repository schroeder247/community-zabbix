// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>Represents the options of a <see cref="ZabbixWireClient"/>, which the client copies when it's built.</summary>
public sealed class ZabbixWireOptions : IValidatableObject
{
    /// <summary>The most values one frame carries unless said otherwise: <c>zabbix_sender</c>'s <c>VALUES_MAX</c>, because a frame of many more takes long enough to hit the timeout.</summary>
    public const int DefaultBatchSize = ZabbixSender.DefaultBatchSize;

    /// <summary>The most bytes one frame's payload carries unless said otherwise: 1 GiB, the most a Zabbix server receives in one frame.</summary>
    public const long DefaultMaxBatchBytes = ZabbixSender.DefaultMaxBatchBytes;

    /// <summary>
    /// The server or proxy: <c>host</c> or <c>host:port</c> (port 10051 when omitted, and an IPv6 address with a port is
    /// bracketed), or the nodes of one HA cluster, <c>zbx1;zbx2</c>, tried in order. A client that only reads from agents
    /// needs none.
    /// </summary>
    public string? Server { get; set; }

    /// <summary>A <c>user.login</c> session or an API token, for the server's requests that need one. It is never logged.</summary>
    [ZabbixSecret]
    public string? SessionId { get; set; }

    /// <summary>How the client connects: its deadline, TLS and compression, for the server and for every agent it reads from.</summary>
    [ValidateObjectMembers(typeof(ZabbixConnectionOptionsValidator))]
    public ZabbixConnectionOptions Connection { get; set; } = new();

    /// <summary>The most values one frame carries; <see cref="DefaultBatchSize"/> by default.</summary>
    [Range(1, int.MaxValue)]
    public int BatchSize { get; set; } = DefaultBatchSize;

    /// <summary>The most bytes one frame's JSON payload carries, before compression: more than a frame's own envelope (35 bytes), and at most <see cref="DefaultMaxBatchBytes"/>, the default.</summary>
    [Range(ZabbixSender.EnvelopeBytes + 1, DefaultMaxBatchBytes)]
    public long MaxBatchBytes { get; set; } = DefaultMaxBatchBytes;

    /// <summary>How long a read from an agent may take, which the request carries: whole seconds, 1 to 600; 3 seconds, Zabbix's default.</summary>
    public TimeSpan CheckTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>A copy that changes to these options don't reach: what a client keeps.</summary>
    /// <returns>The copy, with a copy of <see cref="Connection"/>.</returns>
    internal ZabbixWireOptions Clone() => new()
    {
        Server = Server,
        SessionId = SessionId,
        Connection = Connection?.Clone() ?? new(),
        BatchSize = BatchSize,
        MaxBatchBytes = MaxBatchBytes,
        CheckTimeout = CheckTimeout,
    };

    /// <inheritdoc/>
    IEnumerable<ValidationResult> IValidatableObject.Validate(ValidationContext validationContext)
    {
        if (Server is not null && !TryParseServer(Server, out _, out string? reason))
            yield return new ValidationResult(reason, [nameof(Server)]);
        if (CheckTimeout < TimeSpan.FromSeconds(1) || CheckTimeout > TimeSpan.FromSeconds(600) || CheckTimeout.Ticks % TimeSpan.TicksPerSecond != 0)
            yield return new ValidationResult($"CheckTimeout must be whole seconds from 1 to 600, as an agent takes it; it is {CheckTimeout}.", [nameof(CheckTimeout)]);
    }

    /// <summary>One destination of <c>ServerActive</c>'s grammar: a client talks to one server, whose nodes are the HA nodes of one cluster.</summary>
    /// <param name="server">The text that names the server.</param>
    /// <param name="nodes">The nodes, in order.</param>
    /// <param name="reason">Why not, when it does not parse.</param>
    /// <returns>Whether the text names one server or proxy.</returns>
    internal static bool TryParseServer(string server, [NotNullWhen(true)] out IReadOnlyList<ZabbixServerNode>? nodes, [NotNullWhen(false)] out string? reason)
    {
        nodes = null;
        if (!ZabbixServerActive.TryParse(server.AsSpan(), CultureInfo.InvariantCulture, out ZabbixServerActive? parsed, out string? invalid))
        {
            reason = $"'{server}' does not name a server or proxy: {invalid}";
            return false;
        }
        if (parsed.Count != 1)
        {
            reason = $"'{server}' names {parsed.Count} destinations. A client talks to one server or proxy; you must join the nodes of an HA cluster with ';'.";
            return false;
        }
        (nodes, reason) = (parsed[0], null);
        return true;
    }
}

/// <summary>The generated validator for <see cref="ZabbixWireOptions"/>.</summary>
[OptionsValidator]
internal sealed partial class ZabbixWireOptionsValidator : IValidateOptions<ZabbixWireOptions>
{
}
