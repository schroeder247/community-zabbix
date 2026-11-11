// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Compliance.Redaction;

namespace Community.Zabbix;

/// <summary>
/// Redacts a peer's free text (<see cref="ZabbixDataClassifications.PeerText"/>) to <c>[redacted]</c>, and an empty text
/// to an empty one: a line says the server explained itself without repeating what it echoed.
/// </summary>
/// <remarks>
/// Erased, the text leaves its event ending in a bare separator (<c>refused the request: </c>); the mark keeps the
/// sentence whole and tells an operator where to look (the response, or a trusted sink). <c>AddZabbixRedaction</c>
/// registers this redactor for the class. An application that takes the wire or the hosts without <c>Zabbix.Sdk</c>
/// registers it itself, as it does <see cref="ZabbixItemKeyRedactor"/>.
/// </remarks>
public sealed class ZabbixPeerTextRedactor : Redactor
{
    private const string Mark = "[redacted]";

    /// <inheritdoc/>
    public override int GetRedactedLength(ReadOnlySpan<char> input) => input.IsEmpty ? 0 : Mark.Length;

    /// <inheritdoc/>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="GetRedactedLength"/>.</exception>
    public override int Redact(ReadOnlySpan<char> source, Span<char> destination)
    {
        int length = GetRedactedLength(source);
        if (destination.Length < length)
            throw new ArgumentException($"The destination holds {destination.Length} character(s) and the mark needs {length}.", nameof(destination));
        Mark.AsSpan(0, length).CopyTo(destination);
        return length;
    }
}
