<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Troubleshooting

Find what you see, and do what it says. If nothing here fits, [turn on logging](#turn-on-logging) and check
[Known limitations](KNOWN-LIMITATIONS.md).

## Turn on logging

Without a logger factory, the SDK logs nothing. Pass one to any class. `AddConsole` comes from the
`Microsoft.Extensions.Logging.Console` package.

```csharp
using ILoggerFactory loggers = LoggerFactory.Create(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Debug));

await using ZabbixWireClient wire = ZabbixWireClient.CreateBuilder().WithServer("zbx-server-01").UseLogging(loggers).Build();
```

- `ZabbixClient` and the agents and proxies take it the same way, in their longest constructor. In a host, they use the host's logging.
- Each event has a fixed id. [Telemetry](guides/telemetry.md) lists the blocks of ids, the ones worth an alert, and how secrets and
  item keys stay out of the logs.

## The API

| What you see | What it means | What to do |
| --- | --- | --- |
| `InvalidDataException` naming the endpoint | The reply wasn't JSON-RPC. The address points at another page of the frontend. | Pass the frontend's address. The client calls its `api_jsonrpc.php`. |
| `HttpRequestException` | The request couldn't be sent, the reply was an HTTP error status, the circuit is open, or the concurrency limit was reached. | Check the address and the network. Its message says which. |
| `TimeoutException` | The call passed its deadline. | Raise the timeout, or ask for less. See [Retries](guides/api-client.md). |
| `ZabbixApiException` with `Kind` `NotAuthorized`, `SessionTerminated`, `TokenExpired`, or `LoginFailed` | The credential isn't accepted. | Check the token or the user name and password. |
| `ZabbixApiException` with `Kind` `AuthorizationHeaderRefused` | The call sent a token to a method that must be called without one. | Call that method on a client built with `ZabbixCredential.Anonymous`. |
| `ZabbixApiException` with `Kind` `Permission` or `PermissionOrNotFound` | The user may not do this, or the object doesn't exist. Zabbix doesn't say which, though for a super admin it means the object doesn't exist. | Check the id, and the user's role and permissions. |
| `ZabbixApiException` with `Kind` `Validation` | The server refused the request's values. | Read `Message`, which holds the server's reason. |
| `ZabbixVersionException` | The server's version doesn't have this method or field. Nothing was sent. | Use what that version has. See [The API client](guides/api-client.md). |
| `ZabbixNotFoundException` | A read through the tree named a host, a group, or an item that doesn't exist. | Check the name, or call `ExistsAsync` first. |
| A credential isn't sent over `http` | The SDK sends a credential over plain `http` only to a loopback host. | Use `https`, or set `ZabbixClientOptions.AllowUnencryptedCredentials`. |

Filter on `Kind` rather than parsing the message. In PowerShell, a failed API call is a non-terminating error whose
`FullyQualifiedErrorId` starts with `ZabbixApi.<Kind>`.

## Sending values

A send can succeed and still report failed values. Check `Failed` on the response.

| What you see | What it means | What to do |
| --- | --- | --- |
| `Failed` above 0 | The server refused those values. | Check that the host and the key exist, the value has the item's type, and your address is in the item's allowed hosts. |
| `Failed` above 0 right after you created the item | The server hasn't loaded it. | Wait for its next `CacheUpdateFrequency` refresh, then send again. |
| `Failed` above 0 on 8.0, from another machine | An item created through the API accepts only localhost by default. | Set the item's `TrapperHosts`. |
| `ZabbixTrapperRefusedException` with `IsPaused` | The server paused data collection, and took nothing. | Keep the values and send them later. A send loop does this for you. |
| `ZabbixTrapperRefusedException` otherwise | The reply wasn't a counted success. | Read `Response` and `Info`. |
| `SocketException` | No node could be reached. | Check the address, port 10051, and any firewall. |
| `IOException` | The connection was lost after the request was written, which a standby HA node does, or a redirect couldn't be followed. | Name all the HA nodes, joined with `;`. See [HA nodes and redirects](guides/wire-client.md#ha-nodes-and-redirects). |
| `ZabbixSenderIncompleteException` or `ZabbixSenderCanceledException` | Some frames were taken and others weren't. | Send `Unconfirmed` again. Nothing the server confirmed goes twice. |
| `InvalidDataException` | The reply wasn't a frame or JSON, or was larger than `MaxPayloadBytes`. | Check that the port is the trapper's, and raise `MaxPayloadBytes` if the reply really is that large. |

## Reading an item from an agent

| What you see | What it means | What to do |
| --- | --- | --- |
| An answer with `Error` set | The agent doesn't support the key, or its rules don't allow it. | Check the key and its parameters, and the agent's `AllowKey` and `DenyKey` rules. |
| `ZabbixAgentRefusedException` | The agent refused the request as a whole. | Read `Error`, the agent's reason. |
| `IOException`, with no reply | The agent closed the connection, usually because it doesn't allow your address. | Add your address to the agent's `Server` setting. |
| `SocketException` | The agent couldn't be reached. | Check the address, port 10050, and any firewall. |

## Asking a server for its status

| What you see | What it means | What to do |
| --- | --- | --- |
| `ZabbixServerRefusedException` with `Permission denied.` | Your address isn't one `StatsAllowedIP` admits. | Add it to the server's `StatsAllowedIP`, for `GetStatsAsync` and `GetQueueSizeAsync`. |
| `ZabbixServerRefusedException` about the session | The session isn't valid, or isn't a super admin's. | Pass a super admin's API token. `GetStatusAsync` answers almost nothing for anyone else. |
| `InvalidOperationException` that says none is set | The request goes to a server, and the client has none. | Pass it to the constructor, or set it with the builder's `WithServer`. |

## Agents and proxies

| What you see | What it means | What to do |
| --- | --- | --- |
| `InvalidOperationException` from `StartAsync` | An option is missing or can't be used. Nothing has started. | Its message says what to set, and `InnerException` lists each failure. Fix it and call `StartAsync` again. |
| An active agent's checks never run | The server has no active checks for the host. | The host must exist under the name you passed, with items of type "Zabbix agent (active)". Look for event 3201, a refused request. |
| A passive agent or listener refuses the server | The server's address isn't an allowed peer. | Add it to the allowed peers. Look for event 6003, a refused connection. |
| Values pile up | The server isn't taking them. | Read `agent.Status`: its `Delivered`, `Rejected`, `Stored`, `Held`, `LastDelivered`, and `Fault`. See [Agents](guides/zabbix-agents.md). |
| A plugin won't load | It's missing, names no entry, or was built against a later major version. On Linux and macOS, other accounts can write to its files or a folder above them. | `InvalidOperationException` says which. See [Plugins](guides/plugins.md). |

## TLS and PSK

| What you see | What it means | What to do |
| --- | --- | --- |
| `AuthenticationException` | The handshake was refused. | Check that both ends have the same identity and key, or that the CA signs the peer's certificate and any pinned issuer and subject match. See [TLS and PSK](guides/tls-and-psk.md). |
| `InvalidOperationException` at the first request or at `StartAsync` | The TLS options don't validate, or a certificate file can't be read. A key must be an even number of hex digits, 32 to 512. | Its message says what's wrong, and the cause is inside. |
| A listener logs events 6002 or 6003 | A handshake failed, or a peer was refused. Each is logged once an hour per peer. | Check the peer's TLS settings, and `TlsAccept`. |

## Report a problem

If you've found a bug, [open an issue](https://github.com/schroeder247/community-zabbix/issues) with the SDK and Zabbix versions, a small
program that shows it, and the log. Take tokens and keys out of anything you post. To report a vulnerability, follow
[SECURITY.md](../SECURITY.md) and don't open a public issue.
