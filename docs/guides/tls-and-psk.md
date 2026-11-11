<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# TLS and PSK

`ZabbixTlsOptions`, in `Community.Zabbix.Wire`, encrypts the Zabbix protocol with a pre-shared key or with certificates. Use it on
what connects (`ZabbixWireClient`, the agents, and a proxy's uplink) and, with `Community.Zabbix.Proxy`, on what listens (a trapper,
a passive agent, and a passive proxy). The API uses HTTPS, and these settings don't apply to it.

```bash
dotnet add package Community.Zabbix.Wire     # what connects
dotnet add package Community.Zabbix.Proxy    # what listens
```

## Connect with a pre-shared key

```csharp
byte[] key = Convert.FromHexString(File.ReadAllText("/etc/zabbix/web-01.psk").Trim());   // a TLSPSKFile holds the key as hex

await using ZabbixWireClient wire = ZabbixWireClient.CreateBuilder()
    .WithServer("zabbix.example")
    .WithPsk("web-01", key)
    .Build();
```

`WithPsk` takes the key as bytes, 16 to 256 of them, and sets `ZabbixTlsOptions` on the connection, with the key as the hex text a
`TLSPSKFile` holds. To set the same options yourself, assign `Connection.Tls`, as the next example does.

The server must accept the same identity and key for the host it receives values for. In the host's encryption settings,
"Connections from host" must include PSK. In the API, those settings are `tls_accept`, `tls_psk_identity`, and `tls_psk`, which a
host's node sets with the [API client](api-client.md), and `Set-ZbxHost` too:

```csharp
await zabbix.Hosts["web-01"].UpdateAsync(h =>
{
    h.TlsAccept = ZabbixTlsAcceptModes.Psk;
    h.TlsPskIdentity = "web-01";
    h.TlsPsk = Convert.ToHexString(key);
});
```

## Connect with certificates

```csharp
var connection = new ZabbixConnectionOptions
{
    Tls = new ZabbixTlsOptions
    {
        Mode = ZabbixTlsMode.Certificate,
        CaFile = "/etc/zabbix/ca.crt",
        CertificateFile = "/etc/zabbix/web-01.crt",
        KeyFile = "/etc/zabbix/web-01.key",
        CrlFile = "/etc/zabbix/ca.crl",                                 // optional: the only revocation check
        PeerCertificateIssuer = "CN=Zabbix CA,O=Example",
        PeerCertificateSubject = "CN=zabbix-server,O=Example",            // the server's certificate
    },
};
using var wire = new ZabbixWireClient(new ZabbixWireClientOptions { Server = "zabbix.example", Connection = connection });
```

- You must set `CaFile`, `CertificateFile`, and `KeyFile` together, because this end always presents its certificate.
- The issuer and subject are pins on the peer you connect to: the server or proxy for what a `ZabbixWireClient` sends or asks, and the
  agent for what it reads from an agent. Without pins, any certificate the CA signed is accepted.
- The host name isn't checked. Set the pins wherever the CA signs more than the peers you mean.

The SDK accepts exactly the peers a Zabbix server, proxy, or agent configured the same way would accept.
[What the wire's TLS checks](../../SECURITY.md#what-the-wires-tls-checks-and-what-it-does-not) lists every rule (the trust anchor,
revocation, protocol versions, and how the key is held) and how redirects are held to the same settings.

## Secure an agent or proxy

```csharp
await using ZabbixPushAgent agent = ZabbixPushAgent.CreateBuilder("edge-01")
    .AddServer("zabbix.example")
    .Configure(o => o.Connection.Tls = new ZabbixTlsOptions { Mode = ZabbixTlsMode.PreSharedKey, PskIdentity = "edge-01", Psk = edgeKey })
    .Build();
await agent.StartAsync();
```

`ZabbixConnectionOptions` is the `Connection` option of `ZabbixWireClientOptions`, `ZabbixPushAgentOptions`, `ZabbixActiveAgentOptions`, and
`ZabbixProxyOptions`.

A proxy's builder takes the proxy's own key as bytes, as Zabbix's `TLSPSKIdentity` and `TLSPSKFile` hold it:

```csharp
using System.Net;

await using ZabbixActiveProxy proxy = ZabbixActiveProxy.CreateBuilder("proxy-01")
    .WithServer("zabbix.example")
    .AllowPeer(IPNetwork.Parse("10.0.0.0/8"))
    .WithPsk("proxy-01", proxyKey)
    .Build();
await proxy.StartAsync();
```

An active proxy connects to its server with the key `WithPsk` sets, and a passive proxy takes the server's connections with it. In
the server's settings for the proxy, the same identity and key must be allowed.

## Set options

| `ZabbixTlsOptions` | Zabbix setting | PowerShell | Meaning |
| --- | --- | --- | --- |
| `Mode` | `TLSConnect` | `-Tls` | `Unencrypted` (the default, also when `Tls` isn't set), `PreSharedKey`, or `Certificate`. |
| `PskIdentity` | `TLSPSKIdentity` | `-PskIdentity` | Pre-shared-key mode, required. At most 512 bytes of UTF-8. |
| `Psk` | `TLSPSKFile`'s contents | `-Psk` | Pre-shared-key mode, required. An even number of hex digits, 32 to 512. |
| `CaFile` | `TLSCAFile` | `-CertificateAuthority` | Certificate mode, required. PEM. The only trust anchor. |
| `CertificateFile` | `TLSCertFile` | `-Certificate` | Certificate mode, required. PEM. It may carry its chain after the certificate. |
| `KeyFile` | `TLSKeyFile` | `-CertificateKey` | Certificate mode, required. PEM. |
| `CrlFile` | `TLSCRLFile` | `-CertificateRevocationList` | Certificate mode, optional. PEM. |
| `PeerCertificateIssuer` | `TLSServerCertIssuer` | `-ServerIssuer` | Certificate mode, optional. RFC 2253 form, as OpenSSL prints it. |
| `PeerCertificateSubject` | `TLSServerCertSubject` | `-ServerSubject` | Certificate mode, optional. RFC 2253 form, as OpenSSL prints it. |

`CrlFile` and the pins are refused in pre-shared-key mode.

## Renew a certificate or key

The CA, certificate, key, and CRL files are read once. A `ZabbixWireClient` reads them at its first request to the server and at its
first read from each agent. An agent, a proxy, or a listener reads them when it starts, and a `ZabbixWireLink` when you construct it.

To pick up a renewed certificate or CRL, build another client or agent, or restart the host that runs the agent or the listener.

The private key is released when you dispose the object that read it. On Windows, SChannel needs the key in a key store, so until then
it's held in the key store of the account the application runs under.

## Handle failures

Settings that don't validate, including a malformed key, fail before any connection. The message never repeats the key.

| Where | What it throws |
| --- | --- |
| `ZabbixWireClient` | `InvalidOperationException` at the first request, with the cause inside |
| An agent's, a proxy's, or a listener's builder | `InvalidOperationException` from `Build()`, saying what to set |
| An agent or a proxy you construct | `InvalidOperationException` from `StartAsync`, saying what to set |
| `ZabbixWireLink` | `ArgumentException` from the constructor |
| `ZabbixListener` you construct | `OptionsValidationException` from `StartingAsync` |
| Any of them in a host | The host doesn't start |

A file that can't be used fails when it's read:

| Exception | What's wrong |
| --- | --- |
| `IOException`, `UnauthorizedAccessException` | A file can't be read. |
| `CryptographicException` | A file isn't PEM, or a key doesn't match its certificate. |
| `InvalidDataException` | The CRL file holds no list, or a list isn't valid. |
| `ArgumentException` | A listener accepts certificates and has no `CaFile`. |

`ZabbixWireClient` wraps these in `InvalidOperationException`, with the cause inside, except `InvalidDataException`. Elsewhere they
come from `StartAsync` or the `ZabbixWireLink` constructor as they are, and a host doesn't start.

A failed handshake throws `AuthenticationException`. A refused certificate's reason (the pin, the chain, or the revocation it failed)
is logged as event 1103. A send loop retries it as a failed exchange, so a wrong key shows as retries (event 3104) and an agent health
check that goes degraded, then unhealthy.

## Accept TLS on a listener

`TlsAccept` says which protections a listener accepts, as a set of `ZabbixTlsAcceptModes`: `Unencrypted` (the default), `Psk`, and
`Certificate`. It's Zabbix's `TLSAccept`.

`PreSharedKeys` is a table of identity to hex key, for a trapper that receives from many hosts:

```csharp
await using ZabbixPushAgent relay = ZabbixPushAgent.CreateBuilder("relay")
    .AddServer("zbx-server-01")
    .AddTrapper(10051, options =>
    {
        options.AllowedPeers.Add("10.0.0.0/8");
        options.TlsAccept = ZabbixTlsAcceptModes.Psk;
        options.PreSharedKeys["web-01"] = web01Key;                        // one identity per sending host, its hex key
        options.PreSharedKeys["web-02"] = web02Key;                        // read from your secret store
    })
    .Build();
await relay.StartAsync();
```

The builders of a passive agent, a proxy, and a listener take a peer's key as bytes: `AllowPsk("web-01", key)` adds it to
`PreSharedKeys` as hex. A listener that took only unencrypted connections, its default, then takes pre-shared-key ones only.

`Tls` holds a listener's credentials. Its `Mode` must stay `Unencrypted`:

```csharp
using System.Net;

await using ZabbixPassiveAgent probe = ZabbixPassiveAgent.CreateBuilder("probe-01")
    .AllowPeer(IPAddress.Parse("192.0.2.10"))
    .Configure(o =>
    {
        o.TlsAccept = ZabbixTlsAcceptModes.Certificate;
        o.Tls = new ZabbixTlsOptions
        {
            CaFile = "/etc/zabbix/ca.crt",                                // signs the server's certificate
            CertificateFile = "/etc/zabbix/probe-01.crt",
            KeyFile = "/etc/zabbix/probe-01.key",
            PeerCertificateSubject = "CN=zabbix-server,O=Example",         // the connecting peer's certificate
        };
    })
    .Build();
await probe.StartAsync();
```

For a passive agent, the server's side is the host's "Connections to host" (`tls_connect`). A listener you build takes its
certificate with `WithCertificate(certificateFile, keyFile, caFile)` on `ZabbixListener.CreateBuilder()`, which then takes certificate
connections only, unless it took more kinds already.

- Each accepted kind needs its credentials. `Certificate` needs the CA, certificate, and key files. `Psk` needs an identity and key in
  `Tls` or in `PreSharedKeys`, and every key in the table must be valid.
- A credential for a kind `TlsAccept` doesn't include fails validation, though Zabbix accepts it. See
  [Known limitations](../KNOWN-LIMITATIONS.md).
- In certificate mode, the listener requires the peer's certificate and holds it to the same rules a client does.
- A connection with a kind outside the set is refused and logged as event 6003. A failed handshake is logged as event 6002. Each is
  logged once an hour per peer and reason.

## In an application with a host

The builders `AddZabbixPushAgent("edge-01")` and `AddZabbixActiveAgent("web-01")` return set the same options with
`Configure(o => o.Connection.Tls = …)`. For a listener, use `AddTrapper` on `AddZabbixPushAgent("relay")`, `AllowPsk` or `Configure`
on `AddZabbixPassiveAgent("probe-01")`, and `WithPsk` and `AllowPsk` on a proxy's builder.

Configuration binds the same keys, with `AddZabbixAgents(builder.Configuration)`:

```json
{ "Zabbix": { "PushAgents": { "edge-01": {
    "ServerActive": "zabbix.example",
    "Connection": { "Tls": { "Mode": "PreSharedKey", "PskIdentity": "edge-01", "Psk": "…" } } } } } }
```

Keep the key out of the file. An environment variable or the user-secrets store binds to the same key,
`Zabbix:PushAgents:edge-01:Connection:Tls:Psk`.

`Psk` is classified `[ZabbixSecret]`, so `AddZabbixRedaction`, in `Community.Zabbix.Sdk`, erases it from logs. Without that package, register
the policy yourself. See [Redact logs](telemetry.md#redact-logs).

## Use it from PowerShell

These commands take the parameters in the [options table](#set-options): `Send-ZbxData`, `Get-ZbxAgentItem`, the server's commands
(`Get-ZbxQueue`, `Get-ZbxServerStatus`, and others), `Start-ZbxActiveAgent`, and the proxy commands.

```powershell
Send-ZbxData -Server 'zabbix.example:10051' -HostName 'web-01' -Key 'app.status' -Value 'ok' `
    -PskIdentity 'web-01' -Psk ./web-01.psk
Get-ZbxAgentItem -Agent 'web-01' -Key 'agent.ping' -Certificate ./client.crt -CertificateKey ./client.key `
    -CertificateAuthority ./ca.crt -ServerSubject 'CN=web-01,O=Example'
```

- `-Psk` takes a `SecureString` or the path of a key file. It's never written to a stream, a log, or an error.
- `-Tls` is needed only when you give both a key and a certificate. Otherwise a PSK parameter means pre-shared key, a certificate
  parameter means certificates, and none means unencrypted.
- `Connect-ZbxServer` takes the trapper's settings once (`-TrapperTls`, `-TrapperPskIdentity`, `-TrapperPsk`, `-TrapperCertificate`, and so
  on). Every command that sends to the connection's trapper uses them, unless you give it TLS parameters of its own.
- The listeners, `Start-ZbxPassiveAgent` and `Start-ZbxPassiveProxy`, take the same credential parameters, and `-TlsAccept` in place of
  `-Tls` (`-TlsAccept Unencrypted,Psk`). Without it, they accept the kinds the credentials given allow, and unencrypted connections only
  when you give none.
- A wire command releases the key it read when its call ends. An agent or proxy session releases it when it stops.

## See also

- [The wire client](wire-client.md), [Agents](zabbix-agents.md), and [Listeners](listeners.md): what these settings apply to.
- [Items](items.md): the keys a passive agent answers over its listener.
- [Known limitations](../KNOWN-LIMITATIONS.md): files read once, and TLS credentials as files in PowerShell.
- [Get started with the PowerShell module](../../src/Zabbix.PowerShell/GETTING-STARTED.md): send over TLS from PowerShell.
- The [SendValues, PushAgent, and PassiveAgent samples](../../samples/README.md): programs these settings go into.
