// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
namespace Community.Zabbix;

/// <summary>The DNS record types an agent can ask for.</summary>
public enum ZabbixDnsRecordType
{
    /// <summary>The start of a zone's authority (<c>SOA</c>), which the key asks for when not told otherwise.</summary>
    StartOfAuthority,

    /// <summary>Every record the server will give (<c>ANY</c>).</summary>
    Any,

    /// <summary>An IPv4 address (<c>A</c>).</summary>
    Address,

    /// <summary>An IPv6 address (<c>AAAA</c>).</summary>
    Ipv6Address,

    /// <summary>An authoritative name server (<c>NS</c>).</summary>
    NameServer,

    /// <summary>A name this one is an alias of (<c>CNAME</c>).</summary>
    CanonicalName,

    /// <summary>A mailbox's domain (<c>MB</c>).</summary>
    Mailbox,

    /// <summary>A mail group member (<c>MG</c>).</summary>
    MailGroup,

    /// <summary>A mail rename (<c>MR</c>).</summary>
    MailRename,

    /// <summary>A mail destination (<c>MD</c>).</summary>
    MailDestination,

    /// <summary>A mail forwarder (<c>MF</c>).</summary>
    MailForwarder,

    /// <summary>An empty record (<c>NULL</c>).</summary>
    Null,

    /// <summary>A well-known service (<c>WKS</c>).</summary>
    WellKnownService,

    /// <summary>The name of an address (<c>PTR</c>); the key's name is then the address.</summary>
    ReversePointer,

    /// <summary>Host information (<c>HINFO</c>).</summary>
    HostInformation,

    /// <summary>Mailbox or mail list information (<c>MINFO</c>).</summary>
    MailInformation,

    /// <summary>A mail exchange (<c>MX</c>).</summary>
    MailExchange,

    /// <summary>Free text (<c>TXT</c>).</summary>
    Text,

    /// <summary>A service's host and port (<c>SRV</c>).</summary>
    Service,
}
