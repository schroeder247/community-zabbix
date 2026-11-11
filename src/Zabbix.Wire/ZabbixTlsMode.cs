// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>How a framed-TCP connection is protected — Zabbix's <c>TLSConnect</c>/<c>TLSAccept</c> vocabulary as an enum.</summary>
public enum ZabbixTlsMode
{
    /// <summary>Plain TCP (<c>unencrypted</c>).</summary>
    Unencrypted,

    /// <summary>TLS with certificates (<c>cert</c>).</summary>
    Certificate,

    /// <summary>TLS with a pre-shared key (<c>psk</c>).</summary>
    PreSharedKey,
}
