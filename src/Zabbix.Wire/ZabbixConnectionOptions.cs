// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.ComponentModel.DataAnnotations;
using Community.Zabbix.Protocol;
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>How a framed connection is made: the deadline for one exchange, TLS, compression, the reply cap.</summary>
public sealed class ZabbixConnectionOptions : IValidatableObject
{
    /// <summary>
    /// How long one attempt on one node may take — connect, handshake, request, reply. The deadline is per node attempt,
    /// not per exchange: a fresh one starts for each node the exchange tries, so a full failover across
    /// every node in the list can take up to <c>nodes × Timeout</c> before the caller sees the failure. Upstream's own
    /// node walk (<c>zbx_tcp_connect_failover</c>, <c>commshigh.c</c>) has the same bound: one connect timeout spent per
    /// address in the list, nothing caps the walk as a whole. More than zero, and at most what a deadline can be (about
    /// 24.8 days, <see cref="int.MaxValue"/> milliseconds).
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>The TLS words; unencrypted when unset.</summary>
    [ValidateObjectMembers(typeof(ZabbixTlsOptionsValidator))]
    public ZabbixTlsOptions? Tls { get; set; }

    /// <summary>Whether to compress a request when that makes it smaller; the reply's compression is the peer's.</summary>
    public bool Compress { get; set; }

    /// <summary>The reply cap when <see cref="MaxPayloadBytes"/> is unset, for every exchange but a proxy's configuration: 16 MiB, the listeners' own default.</summary>
    public const long DefaultMaxPayloadBytes = 16L * 1024 * 1024;

    /// <summary>
    /// The most a reply may carry, declared or inflated. Unset, it is <see cref="DefaultMaxPayloadBytes"/> (16 MiB), the cap
    /// the listeners apply to what they receive, and upstream's 16 GiB (<c>ZBX_MAX_RECV_LARGE_DATA_SIZE</c>) where a large
    /// frame is accepted (a proxy's configuration). Upstream's own receive cap is 1 GiB (<c>ZBX_MAX_RECV_DATA_SIZE</c>), but
    /// it runs in a dedicated daemon; this connection runs inside an application, where a peer that declares (or inflates
    /// to) a gigabyte costs the application that much memory. Set it to 1 GiB (1073741824) for upstream's
    /// bound. Set, it caps every receive, as a socket's <c>max_len_limit</c> does upstream.
    /// </summary>
    [Range(1, ZabbixHeader.MaxLargePayload)]
    public long? MaxPayloadBytes { get; set; }

    /// <summary>The cap a reply is read with: <see cref="MaxPayloadBytes"/> when set; otherwise <see cref="DefaultMaxPayloadBytes"/>, or the large cap where a large frame is accepted.</summary>
    internal long? ReplyCap(bool allowLarge) => MaxPayloadBytes ?? (allowLarge ? null : DefaultMaxPayloadBytes);

    /// <inheritdoc/>
    /// <remarks>
    /// <see cref="Timeout"/> is checked here and not by a <see cref="RangeAttribute"/>: a range over a
    /// <see cref="TimeSpan"/> converts its bounds through a type converter, which is not trim-safe.
    /// </remarks>
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Timeout <= TimeSpan.Zero || Timeout > s_longestTimeout)
            yield return new ValidationResult($"Timeout must be more than zero and at most {s_longestTimeout}, the longest deadline a timer takes.", [nameof(Timeout)]);
    }

    // The longest delay a CancellationTokenSource takes: a deadline past it throws where the exchange starts.
    private static readonly TimeSpan s_longestTimeout = TimeSpan.FromMilliseconds(int.MaxValue);
}
