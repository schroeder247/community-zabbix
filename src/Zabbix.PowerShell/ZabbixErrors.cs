// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using System.Management.Automation;
using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text.Json;
using Community.Zabbix.Diagnostics;
using Microsoft.Extensions.Options;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Turns a failure into an <see cref="ErrorRecord"/> whose error id is stable and whose category is read from the
/// failure's kind, never from its prose. The kind is the SDK's one classification (<see cref="ZabbixApiErrorKind"/>,
/// from the JSON-RPC code and the English <c>data</c> prefix: API mode always runs <c>initLocales('en_us')</c>, so the
/// text is stable, but classifying it is the SDK's job, done once, not a cmdlet's).
/// </summary>
/// <remarks>
/// One id scheme, in PascalCase as PowerShell's own ids are (<c>PathNotFound</c>): a failure of a layer is
/// <c>Zabbix&lt;Layer&gt;.&lt;Kind&gt;</c> (<c>ZabbixApi.PermissionOrNotFound</c>, <c>ZabbixHttp.ConnectionError</c>,
/// <c>ZabbixWire.ConnectionRefused</c>, <c>ZabbixWire.TlsHandshakeFailed</c>, <c>ZabbixProxy.Refused</c>), a logged
/// error <c>ZabbixLog.&lt;event name&gt;</c>, a failure of the module's own rules one word (<c>ZabbixReferenceNotFound</c>,
/// <c>ZabbixNotConnected</c>), and any other exception <c>Zabbix.&lt;type name&gt;</c> (<c>Zabbix.InvalidOperation</c>).
/// The kind is the telemetry's <c>error.type</c> (<see cref="ZabbixErrorType.Of"/>) in PascalCase; the snake_case value
/// itself is kept in the exception's <see cref="Exception.Data"/> under <c>error.type</c>, so a record still correlates
/// with the span and the metric that name the same failure. The engine appends <c>,&lt;command type&gt;</c>.
/// </remarks>
internal static class ZabbixErrors
{
    /// <summary>The error record for <paramref name="failure"/>.</summary>
    /// <param name="failure">The exception.</param>
    /// <param name="target">What the command was acting on, if anything.</param>
    public static ErrorRecord From(Exception failure, object? target = null)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (!failure.Data.Contains(ErrorTypeKey)) failure.Data[ErrorTypeKey] = ZabbixErrorType.Of(failure);
        // No ErrorDetails for an API error: ZabbixApiException.Message is already "method: message detail" (the
        // server's bare text is ServerMessage), so the record's own text is the exception's.
        return new ErrorRecord(failure, IdOf(failure), CategoryOf(failure), target);
    }

    /// <summary>The <see cref="Exception.Data"/> key under which the failure's OpenTelemetry <c>error.type</c> is kept.</summary>
    public const string ErrorTypeKey = "error.type";

    /// <summary>The stable error id of <paramref name="failure"/>.</summary>
    /// <param name="failure">The exception.</param>
    public static string IdOf(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure switch
        {
            ZabbixApiException api => "ZabbixApi." + api.Kind.ToString(),
            ZabbixParameterException parameter => parameter.ErrorId,
            // The module's own typed failure names itself (ZabbixScriptFailed).
            IZabbixErrorTyped { ErrorType: { } own } when IsModuleId(own) => own,
            HttpRequestException http => "ZabbixHttp." + (http.HttpRequestError != HttpRequestError.Unknown
                ? http.HttpRequestError.ToString()
                : http.StatusCode?.ToString() ?? "RequestFailed"),
            JsonException => "ZabbixApi." + Pascal(ZabbixErrorType.Of(failure)),
            IZabbixErrorTyped or SocketException or IOException or AuthenticationException or InvalidDataException
                => LayerOf(failure) + "." + (IsSnake(ZabbixErrorType.Of(failure)) ? Pascal(ZabbixErrorType.Of(failure)) : TypeWord(failure)),
            _ => "Zabbix." + TypeWord(failure),
        };
    }

    // The layer of a failure the wire, the agent, the proxy or the SDK raised: the assembly its type lives in
    // (Community.Zabbix.Wire is ZabbixWire). A socket, TLS, frame or I/O failure from the platform is the server's own
    // protocol: the API's transport fails as an HttpRequestException.
    private static string LayerOf(Exception failure) => failure.GetType().Assembly.GetName().Name switch
    {
        "Community.Zabbix.Abstractions" => "Zabbix",
        { } assembly when assembly.StartsWith("Community.Zabbix.PowerShell", StringComparison.Ordinal) => "ZabbixPowerShell",
        { } assembly when assembly.StartsWith(Family, StringComparison.Ordinal) => "Zabbix" + assembly[Family.Length..].Replace(".", string.Empty, StringComparison.Ordinal),
        _ => "ZabbixWire",
    };

    // The prefix every assembly of the family's name starts with.
    private const string Family = "Community.Zabbix.";

    // A module id: Zabbix and one PascalCase word, no dot (ZabbixReferenceNotFound).
    private static bool IsModuleId(string id)
        => id.Length > "Zabbix".Length && id.StartsWith("Zabbix", StringComparison.Ordinal) && char.IsAsciiLetterUpper(id["Zabbix".Length]) && id.All(char.IsAsciiLetter);

    private static bool IsSnake(string errorType) => errorType.Length > 0 && errorType.All(c => c is (>= 'a' and <= 'z') or '_') && char.IsAsciiLetterLower(errorType[0]);

    // connection_refused is ConnectionRefused.
    private static string Pascal(string snake)
        => string.Concat(snake.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

    // InvalidOperationException is InvalidOperation; a generic type's arity is dropped.
    private static string TypeWord(Exception failure)
    {
        string name = failure.GetType().Name;
        int arity = name.IndexOf('`', StringComparison.Ordinal);
        if (arity >= 0) name = name[..arity];
        return name.Length > "Exception".Length && name.EndsWith("Exception", StringComparison.Ordinal) ? name[..^"Exception".Length] : name;
    }

    /// <summary>The category of <paramref name="failure"/>, from its type and code.</summary>
    /// <param name="failure">The exception.</param>
    public static ErrorCategory CategoryOf(Exception failure) => failure switch
    {
        // The SDK's classification of the error object (ZabbixApiException.Kind), never its prose.
        ZabbixApiException api => api.Kind switch
        {
            ZabbixApiErrorKind.Validation => ErrorCategory.InvalidArgument,
            ZabbixApiErrorKind.NotAuthorized or ZabbixApiErrorKind.SessionTerminated
                or ZabbixApiErrorKind.TokenExpired or ZabbixApiErrorKind.LoginFailed => ErrorCategory.AuthenticationError,
            ZabbixApiErrorKind.Permission => ErrorCategory.PermissionDenied,
            ZabbixApiErrorKind.PermissionOrNotFound => ErrorCategory.ObjectNotFound,
            ZabbixApiErrorKind.MethodNotFound => ErrorCategory.NotImplemented,
            ZabbixApiErrorKind.AuthorizationHeaderRefused => ErrorCategory.InvalidOperation,
            ZabbixApiErrorKind.Transient => ErrorCategory.ResourceUnavailable,
            // -32500 "Database error occurred.": the method's SQL failed and was rolled back; not worth a retry.
            ZabbixApiErrorKind.Database => ErrorCategory.InvalidOperation,
            _ => ErrorCategory.NotSpecified,
        },
        ZabbixParameterException { ErrorId: "ZabbixReferenceNotFound" } => ErrorCategory.ObjectNotFound,
        OperationCanceledException => ErrorCategory.OperationStopped,
        TimeoutException => ErrorCategory.OperationTimeout,
        AuthenticationException => ErrorCategory.SecurityError,
        HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => ErrorCategory.PermissionDenied,
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError } => ErrorCategory.SecurityError,
        HttpRequestException { HttpRequestError: HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded } => ErrorCategory.ProtocolError,
        HttpRequestException => ErrorCategory.ConnectionError,
        // The server's own protocol (a trapper, an agent, a proxy) fails as a socket, or as I/O over one.
        System.Net.Sockets.SocketException or IOException { InnerException: System.Net.Sockets.SocketException } => ErrorCategory.ConnectionError,
        InvalidDataException or JsonException => ErrorCategory.InvalidResult,
        OptionsValidationException or ArgumentException or FormatException => ErrorCategory.InvalidArgument,
        KeyNotFoundException or ItemNotFoundException => ErrorCategory.ObjectNotFound,
        ObjectDisposedException => ErrorCategory.ResourceUnavailable,
        // A trapper, a server wire request, an agent or a proxy request the peer declined: it answered, and said no.
        ZabbixRefusedException => ErrorCategory.InvalidOperation,
        InvalidOperationException => ErrorCategory.InvalidOperation,
        _ => ErrorCategory.NotSpecified,
    };

    /// <summary>
    /// The error record for an <c>Error</c>/<c>Critical</c> log record: <c>ZabbixLog.&lt;event name&gt;</c>, or
    /// <c>ZabbixLog.Event&lt;id&gt;</c> for an event logged without a name.
    /// </summary>
    /// <param name="write">The log record.</param>
    internal static ErrorRecord FromLog(LogWrite write)
    {
        string? name = write.EventId.Name;
        string id = "ZabbixLog." + (name is { Length: > 0 } && char.IsAsciiLetterUpper(name[0]) && name.All(char.IsAsciiLetterOrDigit)
            ? name
            : "Event" + write.EventId.Id.ToString(CultureInfo.InvariantCulture));
        Exception failure = write.Exception ?? new InvalidOperationException(write.Message);
        if (write.Exception is { } logged && !logged.Data.Contains(ErrorTypeKey)) logged.Data[ErrorTypeKey] = ZabbixErrorType.Of(logged);
        return new ErrorRecord(failure, id, write.Exception is null ? ErrorCategory.NotSpecified : CategoryOf(write.Exception), write.Category)
        {
            ErrorDetails = new ErrorDetails(write.Message),
        };
    }
}
