<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Security Policy

Community Zabbix, the .NET SDK and the PowerShell modules, is an unofficial, community-maintained project, and
Zabbix LLC does not support it. Provided "as is", without warranty of any kind: use at your own risk. This project is not affiliated with, endorsed by or sponsored by Zabbix LLC. Zabbix is a registered trademark of Zabbix LLC.
This policy covers vulnerabilities in this project's code itself, not in Zabbix Server, Zabbix Agent, or any Zabbix
LLC product.

## Supported versions

Security fixes are applied to the latest released `major.minor` line. Older lines are not
maintained; upgrade to the latest release to receive fixes.

| Version | Supported          |
| ------- | ------------------ |
| 1.0.x   | :white_check_mark: |

## Reporting a vulnerability

**Do not open a public issue for a security vulnerability.**

Report privately through **GitHub Private Vulnerability Reporting**: the repository's **Security** tab, **Report a
vulnerability**, or <https://github.com/schroeder247/community-zabbix/security/advisories/new>. Only the maintainer sees
the report.

Please include:

- The affected component (SDK, PowerShell module, or a specific wire protocol —
  sender / getter / agent / proxy / server).
- Affected version(s) and platform.
- A description of the impact and, where possible, a minimal reproduction or proof of concept.
- Whether the issue is remotely reachable (e.g. against the passive-agent, active-proxy, or
  trapper listeners) or requires local/authenticated access.

The project has one maintainer, so responses are best effort: expect an acknowledgement within about a week. A fix
ships in the next release, and a reporter who wants to be named is credited in its changelog entry.

## Scope and threat model

This project both **calls** Zabbix servers and **implements network listeners** (passive agent,
active proxy, trapper). The following are in scope:

- Memory-safety or denial-of-service issues in the wire-protocol parsers reachable by an
  untrusted network peer.
- Credential or secret disclosure (API tokens, PSK keys, plaintext media-type / IPMI credentials)
  through logs, error messages, exports, or serialized output.
- TLS/PSK authentication bypasses.
- Injection reachable from server responses or peer-supplied item keys.

Out of scope:

- Vulnerabilities in Zabbix LLC products — report those to Zabbix.
- Misconfiguration by the operator (for example, disabling TLS certificate validation in the
  application's own `HttpClient` configuration, or widening a listener's `AllowedPeers` to
  `0.0.0.0/0,::/0`, which must be written out precisely because it admits everyone).
- Issues requiring a pre-compromised host or a malicious operator with local credentials.

## What the wire's TLS checks, and what it does not

The framed-TCP protocols (sender, getter, server wire, agents, proxies, listeners) hold TLS to Zabbix's own rules, so
the SDK accepts exactly the peers a Zabbix server, proxy or agent configured the same way would accept:

- **Certificate mode trusts the configured CA file only** (`TLSCAFile`, `ZabbixTlsOptions.CaFile`); the operating
  system's store is never consulted.
- **The host name is not checked.** Upstream sets no host to verify (`tls_openssl.c`); the issuer and subject pins
  (`PeerCertificateIssuer`, `PeerCertificateSubject`, upstream's `TLSServerCertIssuer` and `TLSServerCertSubject`) are
  how a peer is told apart. Without pins, any certificate the CA signed is accepted: set the pins where the CA signs more
  than the peers you mean.
- **Revocation is checked only against a configured CRL file** (`TLSCRLFile`, `ZabbixTlsOptions.CrlFile`). Online
  revocation (OCSP, CRL distribution points) is not fetched; without a CRL file, a revoked certificate is accepted, as
  upstream accepts it.
- **TLS 1.2 is the minimum** in certificate mode; pre-shared-key mode offers upstream's TLS 1.3 pair and its TLS 1.2
  suites, on P-256 only, and a listener's pre-shared-key server its TLS 1.2 suites only.
- **Certificate files are read once**, when a wire client first needs its connection or when a listener, agent or proxy
  starts, as upstream reads them at start; a changed CA, certificate or CRL file is picked up by building a new one.
- **A pre-shared key is decoded into one array**, handed to BouncyCastle and zeroed when the handshake ends, with the
  secret derived from it, on both the client and the listener. The configured hex text (`ZabbixTlsOptions.Psk`,
  `ZabbixListenerOptions.PreSharedKeys`) is a string: it lives as long as the options that hold it, the options cache
  and the configuration providers that read it, and a string cannot be zeroed.

The JSON-RPC client never follows an HTTP redirect and sends its token only to the configured endpoint.

`ZabbixWireClient.SendAsync`, `Send-ZbxData` and the active agent follow a server's redirect, as upstream's
sender and agent do: a server of an HA cluster answers with the address of the active node, and the request is sent
there, which may be an address outside the configured node list, with the same TLS settings and credentials (the
issuer and subject pins included). One redirect is followed per exchange, and a redirect older than one already
followed is refused. The proxy uplink, the getter and the server wire (`Get-ZbxQueue`, `Get-ZbxServerStatus` and the
other server-wire commands) never follow one. Where the servers are not trusted to name their peers, use certificate
mode with issuer and subject pins, which the redirected connection is held to as well.

## Handling secrets

When reporting, **do not include real credentials, tokens, or PSK keys** in your report or in
any attached capture. Redact them; a description of the field that leaked is sufficient.
