// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix.Proxy;

/// <summary>
/// Provides the proxy protocol's version rules, as Zabbix compares them. The version itself is
/// <see cref="ZabbixComponentVersion"/> (the <c>zbx_get_component_version</c> parse, in <c>Zabbix.Abstractions</c>);
/// what is here are the two comparisons the proxy protocol makes, each taken from the source:
/// <list type="bullet">
/// <item><see cref="ServesConfig"/> is <c>ZBX_PROXY_VERSION_CURRENT</c>: a server sends configuration only to a proxy of
/// its own major <i>and minor</i> (patch ignored); <c>zbx_send_proxyconfig</c> answers anything else with a version
/// mismatch error and its own version (7.0.31, 7.4.15, 8.0.0rc1 alike).</item>
/// <item><see cref="DeniesEmptyTrapperHosts"/>: from 8.0 an item's empty <c>trapper_hosts</c> denies every peer (the
/// check always runs); 7.0 and 7.4 skip the check when the list is empty (<c>sender_item_validator</c>).</item>
/// </list>
/// </summary>
internal static class ZabbixProxyProtocol
{
    /// <summary>The version announced until the server says otherwise.</summary>
    public const string DefaultVersion = "7.4.0";

    /// <summary><see cref="DefaultVersion"/>, parsed.</summary>
    public static ZabbixComponentVersion Default { get; } = ZabbixComponentVersion.Parse(DefaultVersion, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Whether a server of <paramref name="server"/> sends its configuration to a proxy announcing <paramref name="proxy"/>.</summary>
    /// <param name="server">The server's version.</param>
    /// <param name="proxy">The proxy's version.</param>
    /// <returns>Whether major and minor match.</returns>
    public static bool ServesConfig(ZabbixComponentVersion server, ZabbixComponentVersion proxy) => server.WithoutPatch == proxy.WithoutPatch;

    /// <summary>Whether a proxy of <paramref name="proxy"/> refuses every peer for an item whose <c>trapper_hosts</c> is empty.</summary>
    /// <param name="proxy">The version the proxy speaks.</param>
    /// <returns><see langword="true"/> from 8.0.</returns>
    public static bool DeniesEmptyTrapperHosts(ZabbixComponentVersion proxy) => proxy.Major >= 8;
}
