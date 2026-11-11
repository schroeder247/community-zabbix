// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.PowerShell.Commands;

/// <summary>The API endpoint of a server given by its frontend URL or its endpoint.</summary>
internal static class ZabbixEndpoint
{
    /// <summary>
    /// <paramref name="uri"/> when it names a script, else <see cref="ZabbixClientOptions.ApiScript"/> under it: the SDK's rule
    /// (<see cref="ZabbixClientOptions.GetApiEndpoint"/>), after the command line's own checks.
    /// </summary>
    /// <param name="uri">The frontend URL or the endpoint.</param>
    /// <exception cref="ArgumentException"><paramref name="uri"/> is not an absolute http or https URL, or it carries a
    /// user name or password (a secret enters as a <c>PSCredential</c> or a <c>SecureString</c>; a URL's user info
    /// would print wherever the endpoint does).</exception>
    public static Uri Of(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri || (uri.Scheme != System.Uri.UriSchemeHttp && uri.Scheme != System.Uri.UriSchemeHttps))
            throw new ArgumentException($"'{Shown(uri)}' is not an absolute http or https URL", nameof(uri));
        if (uri.UserInfo.Length > 0)
            throw new ArgumentException($"'{Shown(uri)}' carries a user name or password in the URL: give them as -Credential (or a token as -ApiToken)", nameof(uri));
        return ZabbixClientOptions.GetApiEndpoint(uri);
    }

    /// <summary><paramref name="uri"/> as it may be shown: without its user info.</summary>
    /// <param name="uri">The URL.</param>
    public static string Shown(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsAbsoluteUri && uri.UserInfo.Length > 0 ? new UriBuilder(uri) { UserName = string.Empty, Password = string.Empty }.Uri.ToString() : uri.OriginalString;
    }
}
