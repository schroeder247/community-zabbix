// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix.Proxy;

/// <summary>
/// The exception that is thrown when the server refuses a proxy request (<c>"response":"failed"</c>): configuration
/// to a proxy of another major.minor, a page from a proxy it does not know, a bad session. Carries the server's own
/// words and, when it sent it, its version.
/// </summary>
public sealed class ZabbixProxyRefusedException : ZabbixRefusedException, IZabbixErrorTyped
{
    /// <summary>A refusal with no detail.</summary>
    public ZabbixProxyRefusedException()
    {
    }

    /// <summary>A refusal with a message.</summary>
    /// <param name="message">The message.</param>
    public ZabbixProxyRefusedException(string message) : base(message)
    {
    }

    /// <summary>A refusal caused by another failure.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ZabbixProxyRefusedException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>The server's refusal of <paramref name="request"/>.</summary>
    /// <param name="request">The request refused: <c>proxy config</c>, <c>proxy data</c>.</param>
    /// <param name="info">The server's <c>info</c>.</param>
    /// <param name="serverVersion">The server's <c>version</c>, when it sent one.</param>
    public ZabbixProxyRefusedException(string request, string? info, string? serverVersion)
        : base($"The server refused {request}: {(info is { Length: > 0 } words ? words.TrimEnd('.') : "no reason given")}.")
    {
        Request = request;
        Info = info;
        ServerVersion = serverVersion;
    }

    /// <summary>The request refused.</summary>
    public string? Request { get; }

    /// <summary>The server's words.</summary>
    public string? Info { get; }

    /// <summary>The server's version, when the refusal carried it.</summary>
    public string? ServerVersion { get; }

    /// <inheritdoc/>
    string IZabbixErrorTyped.ErrorType => "refused";
}
