// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
using System.Management.Automation;

namespace Community.Zabbix.PowerShell;

/// <summary>
/// A connection's TLS mode (<c>-Tls</c>), a <see cref="ZabbixTlsMode"/>: its member names bind as any enum's do, and so
/// do the words of Zabbix's own <c>TLSConnect</c> (<c>unencrypted</c>, <c>psk</c>, <c>cert</c>) and the member names
/// <c>-TlsAccept</c> takes (<c>Psk</c>), which the enum's own conversion would refuse.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
internal sealed class ZabbixTlsModeAttribute : ArgumentTransformationAttribute
{
    /// <inheritdoc/>
    public override object? Transform(EngineIntrinsics engineIntrinsics, object? inputData)
    {
        object? input = inputData is PSObject wrapped ? wrapped.BaseObject : inputData;
        return input is string text ? text.Trim().ToUpperInvariant() switch
        {
            "PSK" => ZabbixTlsMode.PreSharedKey,
            "CERT" => ZabbixTlsMode.Certificate,
            _ => input,
        } : input;
    }
}
