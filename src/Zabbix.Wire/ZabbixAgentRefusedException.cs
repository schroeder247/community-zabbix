// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0

namespace Community.Zabbix;

/// <summary>
/// The exception that is thrown when a passive agent refuses a <c>passive checks</c> request as a whole: its reply
/// carries a root <c>error</c> (<c>zbx_agent_handle_response</c>), not a value or a per-key error. A key the agent does
/// not support is not this: it is an answer, <see cref="ZabbixAgentAnswer"/> with its reason.
/// </summary>
public sealed class ZabbixAgentRefusedException : ZabbixRefusedException
{
    /// <summary>Initializes a refusal with no detail.</summary>
    public ZabbixAgentRefusedException()
    {
    }

    /// <summary>Initializes a refusal with a message.</summary>
    /// <param name="message">The message, which says what was refused and why.</param>
    public ZabbixAgentRefusedException(string message) : base(message)
    {
    }

    /// <summary>Initializes a refusal with a message and the failure that caused it.</summary>
    /// <param name="message">The message, which says what was refused and why.</param>
    /// <param name="innerException">The failure that caused this one.</param>
    public ZabbixAgentRefusedException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Initializes the agent's refusal of the request for <paramref name="key"/>.</summary>
    /// <param name="key">The key that was asked for.</param>
    /// <param name="info">The agent's <c>error</c> text, verbatim.</param>
    public ZabbixAgentRefusedException(ZabbixItemKey key, string info)
        : base($"The agent refused the request: {info}")
    {
        Key = key;
        Info = info;
    }

    /// <summary>Gets the key that was asked for.</summary>
    /// <value>The key; <see langword="default"/> when the exception was not built from a reply.</value>
    public ZabbixItemKey Key { get; }

    /// <summary>Gets the agent's reason.</summary>
    /// <value>The <c>error</c> text, verbatim; <see langword="null"/> when the exception was not built from a reply.</value>
    public string? Info { get; }
}
