// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix;

/// <summary>
/// The exception that is thrown when the caller cancels a send of several frames after some of them were taken: the
/// frames before the cancelled one were answered with a counted success (<see cref="Delivered"/>), and the cancelled frame
/// and those after it were not confirmed (<see cref="Unconfirmed"/>). Sending only <see cref="Unconfirmed"/> again sends
/// nothing twice that the server confirmed.
/// </summary>
/// <remarks>
/// It is an <see cref="OperationCanceledException"/> whose <see cref="OperationCanceledException.CancellationToken"/> is
/// the caller's, so a <c>catch (OperationCanceledException)</c> still sees the cancellation; it adds what was taken, as
/// <see cref="TaskCanceledException"/> adds its task. A frame cancelled after it was written may have been taken without
/// an answer; its items are among <see cref="Unconfirmed"/>.
/// </remarks>
public sealed class ZabbixSenderCanceledException : OperationCanceledException, IZabbixErrorTyped
{
    /// <summary>A cancelled send with no detail.</summary>
    public ZabbixSenderCanceledException()
    {
        Unconfirmed = [];
    }

    /// <summary>A cancelled send with a message.</summary>
    /// <param name="message">The message that describes the cancellation.</param>
    public ZabbixSenderCanceledException(string message) : base(message)
    {
        Unconfirmed = [];
    }

    /// <summary>A cancelled send with a message and the cancellation the frame threw.</summary>
    /// <param name="message">The message that describes the cancellation.</param>
    /// <param name="innerException">The cancellation the frame threw.</param>
    public ZabbixSenderCanceledException(string message, Exception innerException) : base(message, innerException)
    {
        Unconfirmed = [];
    }

    /// <summary>A send to <paramref name="destination"/> that the caller cancelled after earlier frames were taken.</summary>
    /// <param name="destination">What the sender calls the other end.</param>
    /// <param name="delivered">The answer for the frames taken.</param>
    /// <param name="unconfirmed">The values of the cancelled frame and of every frame after it.</param>
    /// <param name="innerException">The cancellation the frame threw.</param>
    /// <param name="cancellationToken">The caller's token, which was cancelled.</param>
    public ZabbixSenderCanceledException(string destination, ZabbixSenderResponse delivered, IReadOnlyList<ZabbixValue> unconfirmed, Exception innerException, CancellationToken cancellationToken)
        : base(Describe(destination, delivered, unconfirmed), innerException, cancellationToken)
    {
        Delivered = delivered;
        Unconfirmed = unconfirmed;
    }

    /// <summary>Gets the answer for the frames the server took before the cancellation.</summary>
    public ZabbixSenderResponse? Delivered { get; }

    /// <summary>Gets the values the server did not confirm: the cancelled frame's and every later frame's, in order.</summary>
    public IReadOnlyList<ZabbixValue> Unconfirmed { get; }

    /// <summary>The OpenTelemetry <c>error.type</c>: the cancellation's own, as an unadorned cancellation reports it.</summary>
    string IZabbixErrorTyped.ErrorType => InnerException is { } inner ? ZabbixErrorType.Of(inner) : "System.OperationCanceledException";

    private static string Describe(string destination, ZabbixSenderResponse delivered, IReadOnlyList<ZabbixValue> unconfirmed)
    {
        ArgumentNullException.ThrowIfNull(delivered);
        ArgumentNullException.ThrowIfNull(unconfirmed);
        return $"Sending to {destination} was cancelled after {delivered.Frames} frame(s) were taken ({delivered.Total} value(s)); {unconfirmed.Count} value(s) were not confirmed.";
    }
}
