// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>A service an agent can check over TCP by speaking its protocol's greeting.</summary>
public enum ZabbixTcpService
{
    /// <summary>SSH, on port 22 unless told otherwise.</summary>
    Ssh,

    /// <summary>LDAP, on port 389 unless told otherwise. The SDK's own agents do not answer this check.</summary>
    Ldap,

    /// <summary>SMTP, on port 25 unless told otherwise.</summary>
    Smtp,

    /// <summary>FTP, on port 21 unless told otherwise.</summary>
    Ftp,

    /// <summary>HTTP, on port 80 unless told otherwise.</summary>
    Http,

    /// <summary>HTTPS, on port 443 unless told otherwise.</summary>
    Https,

    /// <summary>POP, on port 110 unless told otherwise.</summary>
    Pop,

    /// <summary>NNTP, on port 119 unless told otherwise.</summary>
    Nntp,

    /// <summary>IMAP, on port 143 unless told otherwise.</summary>
    Imap,

    /// <summary>Telnet, on port 23 unless told otherwise.</summary>
    Telnet,

    /// <summary>Whether a TCP connection can be made at all, on a port that must be given.</summary>
    Tcp,
}
