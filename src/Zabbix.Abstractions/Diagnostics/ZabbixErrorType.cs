// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;

namespace Community.Zabbix.Diagnostics;

/// <summary>
/// Maps a failure to the OpenTelemetry <c>error.type</c> attribute: a well-known name in snake_case
/// where one exists (as the platform's HTTP, DNS and Kestrel instruments do), otherwise the exception's full type name
/// (semantic conventions). A domain exception that knows its kind supplies it through <see cref="IZabbixErrorTyped"/>.
/// </summary>
internal static class ZabbixErrorType
{
    /// <summary>The <c>error.type</c> for <paramref name="failure"/>.</summary>
    /// <param name="failure">The exception.</param>
    /// <returns>A low-cardinality name.</returns>
    public static string Of(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure switch
        {
            IZabbixErrorTyped typed => typed.ErrorType,
            TimeoutException => "timeout",
            SocketException socket => Socket(socket.SocketErrorCode),
            IOException { InnerException: SocketException socket } => Socket(socket.SocketErrorCode),
            AuthenticationException => "tls_handshake_failed",
            InvalidDataException => "invalid_frame",
            JsonException => "invalid_response",
            HttpRequestException { HttpRequestError: var error } when error != HttpRequestError.Unknown => Snake(error.ToString()),
            HttpRequestException { StatusCode: { } status } => ((int)status).ToString(System.Globalization.CultureInfo.InvariantCulture),
            IOException => "io_error",
            _ => failure.GetType().FullName ?? failure.GetType().Name,
        };
    }

    private static string Socket(SocketError error) => error switch
    {
        SocketError.ConnectionRefused => "connection_refused",
        SocketError.ConnectionReset => "connection_reset",
        SocketError.ConnectionAborted => "connection_aborted",
        SocketError.TimedOut => "timeout",
        SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "name_resolution_error",
        SocketError.NetworkUnreachable or SocketError.HostUnreachable => "unreachable",
        _ => Snake(error.ToString()),
    };

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1308:Normalize strings to uppercase", Justification = "OpenTelemetry error.type values are snake_case by convention.")]
    private static string Snake(string pascal)
    {
        var text = new System.Text.StringBuilder(pascal.Length + 8);
        for (int i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i])) text.Append('_');
            text.Append(char.ToLowerInvariant(pascal[i]));
        }
        return text.ToString();
    }
}
