// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Net;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Resolves the trapper endpoint (the server, or the host's proxy) for a host.</para>
/// <para type="description">
/// For a host monitored by a proxy, the proxy's own address and port (<c>proxy.get</c>). Otherwise, or when the proxy
/// has no address, the server's trapper: the one given to <c>Connect-ZbxServer -Trapper</c>, else the active HA node's
/// address and port from <c>hanode.get</c> — the same rule the Zabbix frontend uses, never a guessed port. Writes one
/// object per host with <c>HostName</c>, <c>HostId</c>, <c>Server</c>, <c>Address</c> and <c>Port</c> (the first node to
/// try) and <c>Nodes</c> (every node of the server's HA cluster when <c>Connect-ZbxServer -Trapper</c> named several).
/// </para>
/// </summary>
/// <remarks>
/// Server when <c>MonitoredBy</c> is the server; the proxy's own address and port when it is a proxy — the frontend's
/// own rule, not a guessed port (as <see cref="ZabbixServerCmdlet.ResolveTrapperAsync"/> applies at the connection's
/// own scope).
/// </remarks>
[Cmdlet(VerbsDiagnostic.Resolve, "ZbxTrapperEndpoint")]
[OutputType("Community.Zabbix.PowerShell.TrapperEndpoint")]
public sealed class ResolveZbxTrapperEndpointCommand : ZabbixServerCmdlet
{
    /// <summary>The host, from <c>Get-ZbxHost -Filter @{ Output = 'extend' }</c> or similar (needs <c>MonitoredBy</c> and <c>ProxyId</c>).</summary>
    [Parameter(Mandatory = true, Position = 0, ValueFromPipeline = true)]
    [ValidateNotNull]
    public ZabbixHost? InputObject { get; set; }

    private readonly Dictionary<ulong, ZabbixProxy?> _proxies = [];

    /// <inheritdoc/>
    protected override void ProcessRecord()
    {
        ZabbixHost host = InputObject!;
        if (TryRun(async cancellationToken =>
            {
                if (host.MonitoredBy == ZabbixMonitoredBy.Proxy && host.ProxyId is ulong proxyId and not 0)
                {
                    if (!_proxies.TryGetValue(proxyId, out ZabbixProxy? proxy))
                    {
                        var parameters = new ZabbixProxyGetParameters { ProxyIds = [proxyId] };
                        IReadOnlyList<ZabbixProxy> found = await ZabbixClient.Proxies.GetAsync(parameters, cancellationToken).ConfigureAwait(false);
                        _proxies[proxyId] = proxy = found.Count > 0 ? found[0] : null;
                    }
                    if (proxy is { Address.Length: > 0 })
                        return (IReadOnlyList<DnsEndPoint>)[new DnsEndPoint(proxy.Address!, proxy.Port?.Number ?? ZabbixWireClient.DefaultServerPort)];
                }
                return await ResolveTrapperAsync(null, cancellationToken).ConfigureAwait(false);
            }, out IReadOnlyList<DnsEndPoint>? nodes, Connected.Name))
        {
            DnsEndPoint node = nodes[0];
            var result = new PSObject();
            result.Properties.Add(new PSNoteProperty("HostName", host.HostName));
            result.Properties.Add(new PSNoteProperty("HostId", host.HostId));
            result.Properties.Add(new PSNoteProperty("Server", node.Host));
            result.Properties.Add(new PSNoteProperty("Address", node.Host));
            result.Properties.Add(new PSNoteProperty("Port", node.Port));
            result.Properties.Add(new PSNoteProperty("Nodes", nodes.ToArray()));
            result.TypeNames.Insert(0, "Community.Zabbix.PowerShell.TrapperEndpoint");
            WriteObject(result);
        }
    }
}
