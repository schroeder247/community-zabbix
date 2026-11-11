// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Globalization;
using Community.Zabbix.Diagnostics;

namespace Community.Zabbix;

/// <summary>
/// Represents a Zabbix API error object returned by a JSON-RPC call (the Azure <c>RequestFailedException</c> shape): the
/// code, the server's message and <c>data</c>, the method, and the <see cref="Kind"/> classified from the
/// code and the start of <c>data</c>. Classifying by text is safe here because the API's text is always English: API
/// mode calls <c>initLocales('en_us')</c> (<c>ZBase.php</c> 7.0:236, 8.0:237), so no frontend locale reaches it. A caller
/// filters with <c>catch (ZabbixApiException e) when (e.Kind == ZabbixApiErrorKind.PermissionOrNotFound)</c>.
/// </summary>
public sealed class ZabbixApiException : ZabbixRefusedException, IZabbixErrorTyped
{
    /// <summary>A failure with no detail.</summary>
    public ZabbixApiException()
    {
    }

    /// <summary>A failure with a message.</summary>
    /// <param name="message">The message.</param>
    public ZabbixApiException(string message) : base(message)
    {
    }

    /// <summary>A failure caused by another.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public ZabbixApiException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>
    /// The API's own error object. <see cref="Exception.Message"/> is the whole of what the server said, the method first:
    /// <c>host.create: Invalid params. Invalid parameter "/1/host": cannot be empty.</c> The generic <c>message</c> alone
    /// (<c>Invalid params.</c>) never says what was wrong; the <c>data</c> does.
    /// </summary>
    /// <param name="code">The JSON-RPC error code (<c>-32602</c> is invalid params; <c>-32500</c> an application error).</param>
    /// <param name="message">The server's message.</param>
    /// <param name="detail">The server's <c>data</c>, when it sent one.</param>
    /// <param name="method">The method that was called.</param>
    public ZabbixApiException(int code, string message, string? detail, string? method) : this(code, message, detail, method, null)
    {
    }

    /// <summary>
    /// The API's own error object, answered again for a failure that caused it: a refused login answered from memory keeps
    /// the server's refusal as its <see cref="Exception.InnerException"/>.
    /// </summary>
    /// <param name="code">The JSON-RPC error code.</param>
    /// <param name="message">The server's message.</param>
    /// <param name="detail">The server's <c>data</c>, when it sent one.</param>
    /// <param name="method">The method that was called.</param>
    /// <param name="innerException">The failure this one repeats, or <see langword="null"/>.</param>
    public ZabbixApiException(int code, string message, string? detail, string? method, Exception? innerException) : base(Compose(message, detail, method), innerException)
    {
        Code = code;
        ServerMessage = message;
        Detail = detail;
        Method = method;
        Kind = Classify(code, detail);
    }

    /// <summary>The JSON-RPC error code; 0 when the failure did not come from an error object.</summary>
    public int Code { get; }

    /// <summary>The server's own <c>message</c> (<c>Invalid params.</c>, <c>Application error.</c>), without the detail; null when the failure did not come from an error object.</summary>
    public string? ServerMessage { get; }

    /// <summary>The server's <c>data</c> detail, if any: the text that says what was wrong.</summary>
    public string? Detail { get; }

    /// <summary>The method that failed, if known.</summary>
    public string? Method { get; }

    /// <summary>What the error means, from its code and the start of its <c>data</c>.</summary>
    public ZabbixApiErrorKind Kind { get; }

    /// <summary>
    /// Whether the same call may succeed if sent again: the frontend could not serve it at all (<see cref="ZabbixApiErrorKind.Transient"/>,
    /// -32603). The pipeline retries such a reply only for a read; a write is never repeated. A -32500 is never transient,
    /// <see cref="ZabbixApiErrorKind.Database"/> included: the method ran, and what failed in it fails again.
    /// </summary>
    public bool IsTransient => Kind == ZabbixApiErrorKind.Transient;

    /// <summary>The OpenTelemetry <c>error.type</c>: the response status code, as the RPC conventions ask when one was returned.</summary>
    string IZabbixErrorTyped.ErrorType => Code == 0 ? "Community.Zabbix.ZabbixApiException" : Code.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// Classifies an error object by its code and the <b>start</b> of
    /// <c>data</c> (8.0 in debug mode appends text). The login texts are matched under either code, because the source
    /// raises <c>No permissions for system access.</c> as -32602 (<c>CUser.php</c> 7.0:2734, 2742) and the rest as -32500.
    /// </summary>
    /// <param name="code">The <c>error.code</c>.</param>
    /// <param name="data">The <c>error.data</c>.</param>
    /// <returns>The kind.</returns>
    internal static ZabbixApiErrorKind Classify(int code, string? data)
    {
        string text = data ?? string.Empty;
        return code switch
        {
            -32603 => ZabbixApiErrorKind.Transient,
            -32601 => ZabbixApiErrorKind.MethodNotFound,
            -32600 or -32700 => ZabbixApiErrorKind.Validation,
            -32602 or -32500 when Starts(text, "Not authorized.") => ZabbixApiErrorKind.NotAuthorized,
            -32602 or -32500 when Starts(text, "Session terminated, re-login, please.") => ZabbixApiErrorKind.SessionTerminated,
            -32602 or -32500 when Starts(text, "API token expired.") => ZabbixApiErrorKind.TokenExpired,
            -32602 or -32500 when Starts(text, "Incorrect user name or password or account is temporarily blocked.")
                                 || Starts(text, "Authentication failed:")
                                 || Starts(text, "No permissions for system access.") => ZabbixApiErrorKind.LoginFailed,
            -32602 or -32500 when Starts(text, "No permissions to referred object or it does not exist!") => ZabbixApiErrorKind.PermissionOrNotFound,
            -32602 or -32500 when Starts(text, "No permissions to call \"")
                                 || Starts(text, "You do not have permission to perform this operation.") => ZabbixApiErrorKind.Permission,
            -32602 when Starts(text, "The \"") && text.Contains("\" method must be called without authorization header.", StringComparison.Ordinal)
                => ZabbixApiErrorKind.AuthorizationHeaderRefused,
            -32500 when Starts(text, "Database error occurred.") || Starts(text, "System error occurred.") => ZabbixApiErrorKind.Database,
            -32602 or -32500 => ZabbixApiErrorKind.Validation,
            _ => ZabbixApiErrorKind.Unknown,
        };

        static bool Starts(string text, string prefix) => text.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static string Compose(string message, string? detail, string? method)
    {
        string said = string.IsNullOrEmpty(detail) ? message : message + " " + detail;
        return string.IsNullOrEmpty(method) ? said : method + ": " + said;
    }
}
