// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix;

/// <summary>
/// The exception that is thrown when a send of several frames fails after some of them were taken: the frames before the
/// failed one were answered with a counted success (<see cref="Delivered"/>), and the failed frame and those after it were
/// not confirmed (<see cref="Unconfirmed"/>). Sending only <see cref="Unconfirmed"/> again sends nothing twice that the
/// server confirmed. The failed frame's own exception (a <see cref="ZabbixTrapperRefusedException"/>, an
/// <see cref="IOException"/>, a <see cref="TimeoutException"/> and so on) is the <see cref="Exception.InnerException"/>.
/// </summary>
/// <remarks>
/// It is an <see cref="IOException"/>, as a write that fails part of the way through is. When a frame failed after it
/// was written (a reset or a deadline while waiting for the answer), the server may have taken it without answering; the
/// trapper protocol has no way to ask, so its items are among <see cref="Unconfirmed"/>.
/// </remarks>
public sealed class ZabbixSenderIncompleteException : IOException, IZabbixErrorTyped
{
    /// <summary>An incomplete send with no detail.</summary>
    public ZabbixSenderIncompleteException()
    {
        Unconfirmed = [];
    }

    /// <summary>An incomplete send with a message.</summary>
    /// <param name="message">The message that describes the failure.</param>
    public ZabbixSenderIncompleteException(string message) : base(message)
    {
        Unconfirmed = [];
    }

    /// <summary>An incomplete send with a message and the failed frame's exception.</summary>
    /// <param name="message">The message that describes the failure.</param>
    /// <param name="innerException">The failed frame's exception.</param>
    public ZabbixSenderIncompleteException(string message, Exception innerException) : base(message, innerException)
    {
        Unconfirmed = [];
    }

    /// <summary>A send to <paramref name="destination"/> that stopped at a failed frame.</summary>
    /// <param name="destination">What the sender calls the other end.</param>
    /// <param name="delivered">The answer for the frames taken.</param>
    /// <param name="unconfirmed">The values of the failed frame and of every frame after it.</param>
    /// <param name="innerException">The failed frame's exception.</param>
    public ZabbixSenderIncompleteException(string destination, ZabbixSenderResponse delivered, IReadOnlyList<ZabbixValue> unconfirmed, Exception innerException)
        : base(Describe(destination, delivered, unconfirmed, innerException), innerException)
    {
        Delivered = delivered;
        Unconfirmed = unconfirmed;
    }

    /// <summary>Gets the answer for the frames the server took before the failure.</summary>
    public ZabbixSenderResponse? Delivered { get; }

    /// <summary>Gets the values the server did not confirm: the failed frame's and every later frame's, in order.</summary>
    public IReadOnlyList<ZabbixValue> Unconfirmed { get; }

    /// <summary>The OpenTelemetry <c>error.type</c>: the failed frame's.</summary>
    string IZabbixErrorTyped.ErrorType => InnerException is { } inner ? ZabbixErrorType.Of(inner) : "io_error";

    private static string Describe(string destination, ZabbixSenderResponse delivered, IReadOnlyList<ZabbixValue> unconfirmed, Exception innerException)
    {
        ArgumentNullException.ThrowIfNull(delivered);
        ArgumentNullException.ThrowIfNull(unconfirmed);
        ArgumentNullException.ThrowIfNull(innerException);
        return $"Sending to {destination} stopped after {delivered.Frames} frame(s) were taken ({delivered.Total} value(s)); {unconfirmed.Count} value(s) were not confirmed: {innerException.Message}";
    }
}
