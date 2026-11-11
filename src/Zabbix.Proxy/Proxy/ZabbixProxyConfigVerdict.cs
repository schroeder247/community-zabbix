// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Proxy;

/// <summary>
/// Represents what a passive proxy's <c>OnConfig</c> handler decides about a configuration the server pushes: take it
/// (<see cref="Accepted"/>), or refuse it with a reason the server reads in the proxy's <c>failed</c> answer
/// (<see cref="Refuse"/>). The default value takes it.
/// </summary>
/// <example>
/// <code language="csharp">
/// builder.OnConfig((incoming, held, cancellationToken) => ValueTask.FromResult(incoming.Hosts.Count > 10_000
///     ? ZabbixProxyConfigVerdict.Refuse("This proxy takes at most 10,000 hosts.")
///     : ZabbixProxyConfigVerdict.Accepted));
/// </code>
/// </example>
public readonly record struct ZabbixProxyConfigVerdict
{
    private ZabbixProxyConfigVerdict(string reason) => Reason = reason;

    /// <summary>Gets the verdict that takes the configuration.</summary>
    public static ZabbixProxyConfigVerdict Accepted => default;

    /// <summary>Gets whether the configuration is taken.</summary>
    public bool IsAccepted => Reason is null;

    /// <summary>Gets why the configuration is refused, which the server reads; <see langword="null"/> when it's taken.</summary>
    public string? Reason { get; }

    /// <summary>Returns the verdict that refuses the configuration: the proxy keeps the one it holds, and answers the server <c>failed</c>.</summary>
    /// <param name="reason">Why, in words the server's log shows.</param>
    /// <returns>The verdict.</returns>
    /// <exception cref="ArgumentException"><paramref name="reason"/> is empty.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="reason"/> is <see langword="null"/>.</exception>
    public static ZabbixProxyConfigVerdict Refuse(string reason)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        return new ZabbixProxyConfigVerdict(reason);
    }
}
