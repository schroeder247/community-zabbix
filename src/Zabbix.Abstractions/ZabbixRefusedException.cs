// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix;

/// <summary>
/// The base of the exceptions thrown when a Zabbix peer received a request, read it and declined it: an API error
/// object, a trapper's or a server's <c>"response":"failed"</c>, an agent's refusal. The exchange itself worked, so a
/// refusal is the peer's answer and carries its words, not a transport fault.
/// </summary>
/// <remarks>
/// <para>The SDK's exception family has two halves. A refusal derives from this type: <see cref="ZabbixApiException"/>,
/// and in <c>Zabbix.Wire</c> and the hosting packages the sender's, server wire's, agent's and proxy's refusals. A failure
/// of the exchange (no node reachable, a reset connection, a reply that is not the protocol) is an
/// <see cref="IOException"/>, a <see cref="TimeoutException"/> or an
/// <see cref="System.Security.Authentication.AuthenticationException"/>, and the wire's own carry the endpoint and the
/// request. Catch this type to tell "the server said no" from "the server could not be asked".</para>
/// <para>Every concrete exception of the family is sealed.</para>
/// </remarks>
public abstract class ZabbixRefusedException : Exception, IZabbixErrorTyped
{
    /// <summary>Initializes a refusal with no detail.</summary>
    protected ZabbixRefusedException()
    {
    }

    /// <summary>Initializes a refusal with a message.</summary>
    /// <param name="message">The message, which says what was refused and the peer's reason.</param>
    protected ZabbixRefusedException(string message) : base(message)
    {
    }

    /// <summary>Initializes a refusal with a message and the failure that caused it.</summary>
    /// <param name="message">The message, which says what was refused and the peer's reason.</param>
    /// <param name="innerException">The failure that caused this one.</param>
    protected ZabbixRefusedException(string message, Exception? innerException) : base(message, innerException)
    {
    }

    /// <summary>The OpenTelemetry <c>error.type</c> of a refusal: <c>refused</c>, unless a derived type knows a finer one.</summary>
    string IZabbixErrorTyped.ErrorType => "refused";
}
