// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0

namespace Community.Zabbix;

/// <summary>
/// The exception that is thrown when the server refuses a wire request: it answered <c>"response":"failed"</c>, and <see cref="Info"/> is its
/// reason, verbatim (<c>Permission denied.</c>, <c>Unsupported request type.</c>, …; the server's text is fixed English).
/// The request was well formed and the server declined it in its current state (the session, the user type,
/// <c>StatsAllowedIP</c>).
/// </summary>
public sealed class ZabbixServerRefusedException : ZabbixRefusedException
{
    /// <summary>A refusal with no detail.</summary>
    public ZabbixServerRefusedException()
    {
    }

    /// <summary>A refusal with a message.</summary>
    /// <param name="message">The message.</param>
    public ZabbixServerRefusedException(string message) : base(message)
    {
    }

    /// <summary>A refusal caused by another failure.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ZabbixServerRefusedException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>The server's refusal of <paramref name="request"/>.</summary>
    /// <param name="request">The request.</param>
    /// <param name="info">The server's <c>info</c>.</param>
    public ZabbixServerRefusedException(string request, string? info)
        : base($"The Zabbix server refused {request}: {info}")
    {
        Request = request;
        Info = info;
    }

    /// <summary>The request that was refused.</summary>
    public string? Request { get; }

    /// <summary>The server's reason, verbatim.</summary>
    public string? Info { get; }
}
