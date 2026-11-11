// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

public partial class ZabbixHostInterfaceClient
{
    /// <summary>
    /// <c>hostinterface.create</c> for one interface on a host that exists (writes):
    /// <c>CreateAsync(hostId, ZabbixInterface.Snmp(address))</c>. An interface that leaves <c>Main</c> unset is the main one
    /// of its type.
    /// </summary>
    /// <param name="hostId">The host to add the interface to.</param>
    /// <param name="hostInterface">The interface, as <see cref="ZabbixInterface"/> makes one.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>The id the server answered in <c>interfaceids</c>: the interface the call created.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="hostInterface"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="hostInterface"/> has no type, port or way to connect.</exception>
    /// <exception cref="ZabbixApiException">The server answered <c>hostinterface.create</c> with an error.</exception>
    public virtual Task<ulong> CreateAsync(ulong hostId, ZabbixHostInterfaceRequest hostInterface, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostInterface);
        // hostinterface.create takes the interface's own fields and the host's id, all of them required; an interface
        // alone is the main one of its type, as the first of a host's list is (ZabbixInterface.MarkMain)
        var request = new ZabbixHostInterfaceCreateRequest(
            hostId,
            hostInterface.Type ?? throw new ArgumentException("The interface has no type: make it with ZabbixInterface, or set its Type.", nameof(hostInterface)),
            hostInterface.IP ?? string.Empty,
            hostInterface.Dns ?? string.Empty,
            hostInterface.Port ?? throw new ArgumentException("The interface has no port: make it with ZabbixInterface, or set its Port.", nameof(hostInterface)),
            hostInterface.UseIP ?? throw new ArgumentException("The interface doesn't say whether it connects by IP address or DNS name: make it with ZabbixInterface, or set its UseIP.", nameof(hostInterface)),
            hostInterface.Main ?? ZabbixInterfacePriority.Primary)
        {
            Details = hostInterface.Details,
        };
        return CreateAsync(request, cancellationToken);
    }
}
