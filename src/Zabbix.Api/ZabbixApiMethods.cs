// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Community.Zabbix;

/// <summary>
/// Provides the capability table: every method of every described version, generated from the Zabbix source. A
/// method the table does not know is the server's to judge: the gate lets it through and the server answers.
/// </summary>
public static partial class ZabbixApiMethods
{
    private static readonly FrozenDictionary<ZabbixApiMethod, ZabbixApiMethodInfo> s_all = Describe();

    /// <summary>Gets every described method, ignoring case as Zabbix does.</summary>
    /// <value>A read-only table; frozen, so a lookup costs no more than a hash.</value>
    public static IReadOnlyDictionary<ZabbixApiMethod, ZabbixApiMethodInfo> All => s_all;

    /// <summary>What the table says about <paramref name="method"/>.</summary>
    /// <param name="method">The API method.</param>
    /// <param name="info">The entry, when there is one.</param>
    /// <returns><see langword="true"/> when the model knows the method; otherwise <see langword="false"/>.</returns>
    public static bool TryGet(ZabbixApiMethod method, [NotNullWhen(true)] out ZabbixApiMethodInfo? info) => s_all.TryGetValue(method, out info);

    /// <summary>Whether <paramref name="method"/> only reads.</summary>
    /// <param name="method">The API method.</param>
    /// <returns><see langword="true"/> when the method only reads.</returns>
    public static bool IsRead(ZabbixApiMethod method) => s_all.TryGetValue(method, out ZabbixApiMethodInfo? m) && m.IsRead;

    /// <summary>
    /// Whether <paramref name="method"/> may be sent again after a failure the caller cannot see the end of. Every method is
    /// a POST, so the HTTP verb says nothing about safety; the JSON-RPC method does, and a method the table doesn't know is
    /// never sent twice.
    /// </summary>
    /// <param name="method">The API method.</param>
    /// <returns><see langword="true"/> when sending the method twice has the effect of sending it once.</returns>
    public static bool IsIdempotent(ZabbixApiMethod method) => s_all.TryGetValue(method, out ZabbixApiMethodInfo? m) && m.IsIdempotent;

    /// <summary>Whether <paramref name="method"/> needs a session; a method the table does not know does.</summary>
    /// <param name="method">The API method.</param>
    /// <returns><see langword="true"/> when the method needs a session or a token.</returns>
    public static bool RequiresAuthentication(ZabbixApiMethod method) => !s_all.TryGetValue(method, out ZabbixApiMethodInfo? m) || m.RequiresAuthentication;

    /// <summary>Whether a server of <paramref name="serverVersion"/> has <paramref name="method"/> (the Tester-Doer of <see cref="EnsureSupported"/>).</summary>
    /// <param name="method">The API method.</param>
    /// <param name="serverVersion">The server's version (<c>apiinfo.version</c>).</param>
    /// <returns><see langword="true"/> when the version has the method.</returns>
    /// <exception cref="ArgumentException"><paramref name="method"/> is the default value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="serverVersion"/> is <see langword="null"/>.</exception>
    public static bool Supports(ZabbixApiMethod method, Version serverVersion)
    {
        ZabbixApiMethod.ThrowIfDefault(method);
        ArgumentNullException.ThrowIfNull(serverVersion);
        if (!s_all.TryGetValue(method, out ZabbixApiMethodInfo? m)) return true;
        return AtLeast(serverVersion, m.Since) && (m.RemovedIn is null || !AtLeast(serverVersion, m.RemovedIn));
    }

    /// <summary>Throws <see cref="ZabbixVersionException"/> when a server of <paramref name="serverVersion"/> does not have <paramref name="method"/>.</summary>
    /// <param name="method">The API method.</param>
    /// <param name="serverVersion">The server's version.</param>
    /// <exception cref="ArgumentException"><paramref name="method"/> is the default value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="serverVersion"/> is <see langword="null"/>.</exception>
    /// <exception cref="ZabbixVersionException">It does not.</exception>
    public static void EnsureSupported(ZabbixApiMethod method, Version serverVersion)
    {
        if (!Supports(method, serverVersion)) throw new ZabbixVersionException(method, serverVersion, s_all[method]);
    }

    // method (ignoring case, as Zabbix routes it) → wire field (exact, as the validator reads it) → what refuses it
    private static readonly FrozenDictionary<ZabbixApiMethod, FrozenDictionary<string, ZabbixApiField>> s_fields = DescribeFields();

    /// <summary>What the table says about <paramref name="field"/> of <paramref name="method"/>'s request, when some version refuses it.</summary>
    /// <param name="method">The API method.</param>
    /// <param name="field">The wire field.</param>
    /// <param name="info">The entry, when there is one.</param>
    /// <returns><see langword="true"/> when the model knows the field; otherwise <see langword="false"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> is <see langword="null"/>.</exception>
    public static bool TryGetField(ZabbixApiMethod method, string field, [NotNullWhen(true)] out ZabbixApiField? info)
    {
        ArgumentNullException.ThrowIfNull(field);
        info = null;
        return s_fields.TryGetValue(method, out FrozenDictionary<string, ZabbixApiField>? fields) && fields.TryGetValue(field, out info);
    }

    /// <summary>Whether a server of <paramref name="serverVersion"/> accepts <paramref name="field"/> in <paramref name="method"/>'s request (the Tester-Doer of <see cref="EnsureFieldSupported"/>); a field the table does not know is the server's to judge.</summary>
    /// <param name="method">The API method.</param>
    /// <param name="field">The wire field.</param>
    /// <param name="serverVersion">The server's version (<c>apiinfo.version</c>).</param>
    /// <returns><see langword="true"/> when the version takes the field in the method's request.</returns>
    /// <exception cref="ArgumentException"><paramref name="method"/> is the default value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> or <paramref name="serverVersion"/> is <see langword="null"/>.</exception>
    public static bool SupportsField(ZabbixApiMethod method, string field, Version serverVersion)
    {
        ZabbixApiMethod.ThrowIfDefault(method);
        ArgumentNullException.ThrowIfNull(serverVersion);
        return !TryGetField(method, field, out ZabbixApiField? f) || !AtLeast(serverVersion, f.RemovedIn);
    }

    /// <summary>Throws <see cref="ZabbixVersionException"/> when a server of <paramref name="serverVersion"/> refuses <paramref name="field"/> in <paramref name="method"/>'s request.</summary>
    /// <param name="method">The API method.</param>
    /// <param name="field">The wire field.</param>
    /// <param name="serverVersion">The server's version.</param>
    /// <exception cref="ArgumentException"><paramref name="method"/> is the default value.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="field"/> or <paramref name="serverVersion"/> is <see langword="null"/>.</exception>
    /// <exception cref="ZabbixVersionException">It does; the message names the field to send instead.</exception>
    public static void EnsureFieldSupported(ZabbixApiMethod method, string field, Version serverVersion)
    {
        if (!SupportsField(method, field, serverVersion)) throw new ZabbixVersionException(serverVersion, s_fields[method][field]);
    }

    // Versions compare by major.minor: 7.4.15 has what 7.4 has; 8.0.0 (8.0.0rc1 reports "8.0.0") lacks what 8.0 removed.
    internal static bool AtLeast(Version server, Version threshold)
        => server.Major > threshold.Major || (server.Major == threshold.Major && server.Minor >= threshold.Minor);
}
