// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using Microsoft.Extensions.Options;

namespace Community.Zabbix;

/// <summary>
/// Provides the request options the SDK's HTTP pipeline reads, so a handler added through
/// <see cref="ZabbixBuilderExtensions.ConfigureHttpClient"/> can tell a read from a write and see a request's deadline.
/// </summary>
public static class ZabbixHttp
{
    /// <summary>The name of the <see cref="HttpClient"/> a client named <paramref name="name"/> uses: <c>Zabbix</c> for the default, <c>Zabbix:prod</c> for <c>prod</c>.</summary>
    /// <param name="name">The server's name.</param>
    /// <returns>The name of the client's <see cref="HttpClient"/>.</returns>
    internal static string GetClientName(string name = "") => string.IsNullOrEmpty(name) || name == Options.DefaultName ? "Zabbix" : "Zabbix:" + name;

    /// <summary>
    /// <paramref name="endpoint"/> without its user info (<c>http://user:password@host/…</c>): <see cref="HttpClient"/> never
    /// sends it (a Zabbix credential is a token, or a login), so the SDK keeps none, and nothing it holds, logs or traces can
    /// render a password written into a URL. Everything else is kept as written.
    /// </summary>
    /// <param name="endpoint">The endpoint as configured.</param>
    internal static Uri WithoutUserInfo(Uri endpoint)
        => !endpoint.IsAbsoluteUri || endpoint.UserInfo.Length == 0 ? endpoint : new UriBuilder(endpoint) { UserName = string.Empty, Password = string.Empty }.Uri;

    /// <summary>The name of the <c>HttpStandardResilienceOptions</c> the client's resilience handler reads (<see cref="ZabbixBuilderExtensions.ConfigureResilience"/> writes them).</summary>
    /// <param name="name">The server's name.</param>
    /// <returns>The name of the client's resilience options.</returns>
    internal static string GetResilienceName(string name = "") => GetClientName(name) + "-standard";

    /// <summary>The JSON-RPC method a request carries, so the retry gate can tell a read from a write.</summary>
    public static HttpRequestOptionsKey<string> Method { get; } = new("Zabbix.Method");

    /// <summary>The deadline a request carries when <see cref="ZabbixOptions.Timeouts"/> names its method: it replaces the attempt and total timeouts.</summary>
    public static HttpRequestOptionsKey<TimeSpan> Deadline { get; } = new("Zabbix.Deadline");

    // Every method is a POST, so the HTTP verb says nothing about safety; the JSON-RPC method does, and the generated
    // capability table (ZabbixApiMethods, read from each version's source: whether anything reachable from the method
    // writes, and requiresAuthentication()) says which. A write is never retried (a host.create sent twice is two hosts).
    // Zabbix lower-cases the method before routing (strtolower in CLocalApiClient::callMethod), so the table ignores case.

    /// <summary>Whether <paramref name="method"/> may be safely sent again.</summary>
    /// <param name="method">The JSON-RPC method.</param>
    /// <returns><see langword="true"/> when the method may be sent again after a transient failure.</returns>
    public static bool IsIdempotent(string? method)
        => method is not null && ZabbixApiMethods.IsIdempotent(method);

    /// <summary>
    /// Whether <paramref name="method"/> must be sent without an <c>Authorization</c> header: its
    /// <c>requiresAuthentication()</c> is false. 7.0 refuses the header on all of them; 8.0 accepts it on
    /// <c>apiinfo.version</c> only (<c>supportsAuthentication()</c>), so none carries one.
    /// </summary>
    /// <param name="method">The JSON-RPC method.</param>
    internal static bool IsAnonymous(string method) => !ZabbixApiMethods.RequiresAuthentication(method);
}
