// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;
using System.Net;
using System.Security;

namespace Community.Zabbix.PowerShell.Commands;

/// <summary>
/// <para type="synopsis">Updates the server's autoregistration TLS settings (<c>autoregistration.update</c>).</para>
/// </summary>
[Cmdlet(VerbsCommon.Set, "ZbxAutoregistration", SupportsShouldProcess = true, ConfirmImpact = ConfirmImpact.Medium)]
[OutputType(typeof(ZabbixAutoregistrationSettings))]
public sealed class SetZbxAutoregistrationCommand : ZabbixSingletonSetCmdlet<ZabbixAutoregistrationUpdateRequest>
{
    /// <summary>
    /// Which incoming connection encryption modes autoregistration accepts (<c>tls_accept</c>). Give several modes as a
    /// list, e.g. <c>-TlsAccept Unencrypted,Psk</c>; they are combined into the one setting.
    /// </summary>
    /// <remarks>
    /// Which incoming connection encryption modes autoregistration accepts (<c>tls_accept</c>, a bitmask): an array
    /// so <c>-TlsAccept Unencrypted,Psk</c> OR's every given mode onto one request field (<c>ZabbixTlsAcceptModes</c> is
    /// <c>[Flags]</c>, but a scalar <c>ZabbixTlsAcceptModes?</c> parameter rejects an array literal with "Cannot convert
    /// 'System.Object[]'").
    /// </remarks>
    [Parameter] public ZabbixTlsAcceptModes[]? TlsAccept { get; set; }

    /// <summary>The pre-shared-key identity autoregistering agents must present (<c>tls_psk_identity</c>).</summary>
    [Parameter] public string? TlsPskIdentity { get; set; }

    /// <summary>The pre-shared key itself (<c>tls_psk</c>), as a hex string.</summary>
    [Parameter] public SecureString? TlsPsk { get; set; }

    /// <inheritdoc/>
    protected override string Subject => "the autoregistration settings";

    /// <inheritdoc/>
    protected override ZabbixAutoregistrationUpdateRequest BuildRequest() => new()
    {
        TlsAccept = TlsAccept is null ? null : TlsAccept.Aggregate((ZabbixTlsAcceptModes)0, (mask, mode) => mask | mode),
        TlsPskIdentity = TlsPskIdentity,
        TlsPsk = TlsPsk is null ? null : new NetworkCredential(string.Empty, TlsPsk).Password,
    };

    /// <inheritdoc/>
    protected override async Task<object?> UpdateAsync(ZabbixClient client, ZabbixAutoregistrationUpdateRequest request, CancellationToken cancellationToken)
        => await client.Autoregistration.UpdateAsync(request, cancellationToken).ConfigureAwait(false);

    /// <inheritdoc/>
    protected override async Task<object?> GetAsync(CancellationToken cancellationToken)
        => await ZabbixClient.Autoregistration.GetAsync(cancellationToken).ConfigureAwait(false);
}
