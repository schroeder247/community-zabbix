// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// Provides the base of a command that connects over a framed Zabbix protocol with no connected server (the getter, the
/// proxy uplink, <c>Connect-ZbxServer</c>'s trapper): the TLS words of <see cref="ZabbixTlsCmdlet"/> and the mode,
/// <c>-Tls</c> (Zabbix's <c>TLSConnect</c>).
/// </summary>
public abstract class ZabbixWireCmdlet : ZabbixTlsCmdlet
{
    /// <summary>
    /// How the connection is protected (Zabbix's <c>TLSConnect</c>): <c>Unencrypted</c>, <c>PreSharedKey</c> (or
    /// <c>psk</c>) or <c>Certificate</c> (or <c>cert</c>). When omitted, <c>PreSharedKey</c> if <c>-Psk</c> or
    /// <c>-PskIdentity</c> is given, <c>Certificate</c> if a certificate parameter is, and unencrypted if neither is.
    /// </summary>
    [Parameter]
    [ZabbixTlsMode]
    public ZabbixTlsMode? Tls { get; set; }

    /// <summary>The TLS words of this command, or <see langword="null"/> when none is given (unencrypted); read once <see cref="BeginProcessing"/> has run.</summary>
    private protected ZabbixTlsOptions? TlsOptions { get; private set; }

    /// <summary>Reads the TLS words once, before any record: words that cannot be used stop the command (<c>ZabbixTlsModeAmbiguous</c>).</summary>
    protected override void BeginProcessing()
    {
        base.BeginProcessing();
        try
        {
            TlsOptions = ZabbixTlsParameters.Connecting(this, Tls);
        }
        catch (ZabbixParameterException failure)
        {
            ThrowTerminatingError(ZabbixErrors.From(failure, nameof(Tls)));
        }
    }
}
