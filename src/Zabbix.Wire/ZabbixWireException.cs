// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix;

/// <summary>
/// The base of the wire's own exchange failures, where it knows more than the raw transport failure does (which redirect).
/// Internal: a caller sees an <see cref="IOException"/>, as for any lost connection, and a peer that answered and declined
/// is a <see cref="ZabbixRefusedException"/> instead.
/// </summary>
internal abstract class ZabbixWireException : IOException, IZabbixErrorTyped
{
    /// <summary>Initializes a wire failure with no detail.</summary>
    protected ZabbixWireException()
    {
    }

    /// <summary>Initializes a wire failure with a message.</summary>
    /// <param name="message">The message, which names the endpoint and the request where they are known.</param>
    protected ZabbixWireException(string message) : base(message)
    {
    }

    /// <summary>Initializes a wire failure with a message and the transport failure that caused it.</summary>
    /// <param name="message">The message, which names the endpoint and the request where they are known.</param>
    /// <param name="innerException">The raw transport failure.</param>
    protected ZabbixWireException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>The OpenTelemetry <c>error.type</c>: the raw transport failure's, when there is one.</summary>
    string IZabbixErrorTyped.ErrorType => InnerException is { } inner ? ZabbixErrorType.Of(inner) : "io_error";
}
