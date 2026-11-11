<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# The PowerShell modules

Three PowerShell 7.6 modules put the SDK in a shell. This guide is the overview. To learn them step by step, follow
[Get started with the PowerShell module](../../src/Zabbix.PowerShell/GETTING-STARTED.md) first.

```powershell
Install-PSResource Community.Zabbix
Install-PSResource Community.Zabbix.Agent      # agent sessions (optional)
Install-PSResource Community.Zabbix.Proxy      # proxy sessions, proxy protocol builders, the connector stream (optional)
```

```powershell
Import-Module Community.Zabbix
$token = Read-Host -AsSecureString 'API token'
Connect-ZbxServer -Uri https://zabbix.example/ -ApiToken $token
Get-ZbxHost -Name 'web-*' | Format-Table TechnicalName, VisibleName, Status
Get-ZbxHost -Name web-01 | Show-ZbxPage                  # the host's page in the web frontend
```

## Choose a module

| Module | Commands | What it holds |
| --- | --- | --- |
| `Community.Zabbix` | 267 | The per-runspace context, the generated object commands, the admin, `Send-ZbxData`, `Get-ZbxAgentItem`, history, watch, and configuration commands, and `Show-ZbxPage` |
| `Community.Zabbix.Agent` | 20 | Active and passive agent sessions, scriptblock item handlers, and agent-host provisioning |
| `Community.Zabbix.Proxy` | 23 | Active and passive proxy sessions, the proxy protocol's builders, and the connector stream |

- Importing a sibling imports `Community.Zabbix` first, and both share its connections and sessions.
- A session that only manages a server never loads the hosting and listener libraries.
- The modules load `Microsoft.Extensions.*` 10.0 into the default load context, so the import fails if a module loaded an older copy first.
  [Known limitations](../KNOWN-LIMITATIONS.md#powershell) describes the cases.

## Connect to a server

```powershell
Connect-ZbxServer -Uri https://zabbix-dr.example/ -ApiToken $drToken -Name dr -NoDefault
Get-ZbxHost -Name web-01 -Connection dr        # one call against another server
Get-ZbxContext                                 # the connections, the current one, the running sessions
```

`Connect-ZbxServer` takes the frontend URL or the API endpoint, asks the server for its API version, proves the credential with one
authenticated read, and adds the connection. A refused token or password ends the command with an error and adds nothing.

| Parameter | What it does |
| --- | --- |
| `-ApiToken`, `-Credential` | The credential: a `SecureString` or a `PSCredential`, never a plain or positional string. |
| `-Name` | The connection's name. The default is the endpoint's host and port. |
| `-NoDefault` | Adds the server without making it the current one. A first server always becomes current. |
| `-AllowUnencryptedCredential` | Sends the credential over plain `http` to any host. Without it, plain `http` reaches a loopback host only. |
| `-Trapper` | The trapper for the commands that speak the server's own protocol. Without it, they ask the API for the active HA node. |
| `-FrontendUri` | The frontend, when it isn't the endpoint's folder. |

- Each runspace has its own context. A job, a `ForEach-Object -Parallel` branch, or another runspace fails with `ZabbixNotConnected` until it
  connects itself.
- `Disconnect-ZbxServer` closes one connection. Closing the runspace or `Remove-Module Community.Zabbix` closes them all and stops the runspace's
  sessions.
- `Get-ZbxApiInfo` and `Get-ZbxServerInfo` say what a connection talks to: its name, endpoint, and authentication, and the server's API version.

## Get objects

Every API object has generated `Get-`, `New-`, `Set-`, and `Remove-` commands, such as `Get-ZbxHost` and `New-ZbxItem`.

```powershell
Get-ZbxHost -Name 'web-0?' -SelectInterfaces                       # * and ? are wildcards; [ and ] are literal
Get-ZbxHostGroup -Filter @{ with_hosts = $true }                    # -Filter takes the typed *GetParameters
Get-ZbxItem -Host web-01 -Search @{ key_ = 'system.cpu' } -CountOutput
```

- Results are typed records with the SDK's property names. A host's technical name (the API's `host`) is `TechnicalName`, and its visible name
  (`name`) is `VisibleName`. A field the server sends that a record doesn't model still reads as a property.
- A `Get-` selects by `-Id`, by `-Name` (ignoring case; a backtick escapes `*` and `?`), by a reference such as `-Host` or `-Group` (a name, an id, or
  an object), or by `-Filter`.
- `-Filter` takes the object's whole `.get` parameters, as the typed record or a hashtable keyed by wire or C# names. A key that names a field of the
  object goes into the API's `filter`. A key the record doesn't know is refused, never dropped.
- `-Search`, `-SortField`, `-CountOutput`, and the `-Select*` switches work with any selection.

## Create and change objects

```powershell
New-ZbxHost -Name web-02 -Group 'Linux servers' -InterfaceType Agent -InterfaceIp '10.0.0.12' -Tag @{ env = 'prod' }
Get-ZbxHost -Name 'web-*' | Set-ZbxHost -Description 'web tier' -PassThru
```

- `New-` writes the object as the server stored it. `Set-` writes nothing unless you pass `-PassThru`.
- `Set-` and `Remove-` take objects, ids, or names from the pipeline, resolve them all, and change them in one call when the pipeline ends.
- A name that matches no object, or several, is an error and is left out. A name never picks one of many.
- A parameter the connected server's version doesn't have fails before the call is sent.
- `-AdditionalProperty`, on every generated `New-` and `Set-`, adds any field the command has no parameter for. It's merged last and wins over a named
  parameter. A `$null` value takes the field out of the request.
- Hashtables bind as a cast (`[Community.Zabbix.ZabbixHostGroupCreateRequest]@{ name = 'Databases' }`), as an argument, and nested at any depth. A piped
  hashtable binds nothing: pipe a `[pscustomobject]`, or splat the hashtable.

## Preview a change

| Commands | ConfirmImpact | What happens |
| --- | --- | --- |
| `New-`, `Set-` | Medium | No prompt by default. `-WhatIf` previews, and `-Confirm` asks. |
| `Remove-` | High | PowerShell asks unless you pass `-Confirm:$false` or `-Force`. `-Force` never overrides `-WhatIf`. |

## Handle errors

A failed API call is a non-terminating error whose `FullyQualifiedErrorId` starts with `ZabbixApi.<Kind>`, such as `ZabbixApi.Validation` or
`ZabbixApi.PermissionOrNotFound`. Branch on the kind, not the message. `Get-ZbxError` lists the recent ones.

## Run other API methods and admin tasks

`Invoke-ZbxApi` calls any method. A `.get` passes without the confirmation gate, and every other method is gated as a change. The API's other methods,
and the tasks a server administrator scripts, have commands of their own:

| Area | Commands |
| --- | --- |
| Hosts and templates | `Add-` and `Remove-ZbxHostGroupMember`, `Add-` and `Remove-ZbxHostTemplate` (link and unlink), `Remove-ZbxTag` (one tag, the others kept), `Enable-ZbxHost`, `Copy-ZbxHost` and `Copy-ZbxTemplate` (to another name), `Copy-ZbxItem` (onto other hosts), `Wait-ZbxHost` (until a host exists) |
| Problems and checks | `Confirm-ZbxProblem` (acknowledge, with a message, a severity, suppression, or closing), `Invoke-ZbxCheckNow`, `Invoke-ZbxScript` (on a host or for an event), `Test-ZbxExpression`, `Clear-ZbxHistory` |
| Server settings | `Get-` and `Set-ZbxSetting`, `Get-` and `Set-ZbxAuthentication`, `Get-` and `Set-ZbxAutoregistration`, `Get-` and `Set-ZbxHousekeeping` |
| Users and permissions | `Unblock-ZbxUser`, `Reset-ZbxUserTotp`, `Invoke-ZbxUserProvision`, `Test-ZbxUserDirectory`, `Invoke-ZbxHostGroupPropagation`, `Invoke-ZbxTemplateGroupPropagation` |
| The server and its proxies | `Get-ZbxHaNode`, `Get-ZbxTask` and `New-ZbxTask`, `Invoke-ZbxDiagnostic`, `Sync-ZbxProxyConfig`, `Resolve-ZbxTrapperEndpoint` (the trapper a host's values go to), `Test-ZbxMediaType` |
| Parts of other objects | `New-ZbxNetworkDiscoveryCheck` (a check for a discovery rule), `Publish-ZbxDashboard` (a dashboard from a document), `Get-ZbxGraphItem`, `Get-ZbxSli` |

## Send, read, and watch values

```powershell
Watch-ZbxItem -Name cpu -HostName 'web-01' -Key 'system.cpu.load'
Receive-ZbxWatch -Name cpu | Select-Object -First 5      # waits for each value
Stop-ZbxWatch -Name cpu
```

| Commands | What they do |
| --- | --- |
| `Send-ZbxData` | Send values to trapper items over the sender protocol. With `-Server`, it needs no connection. |
| `Send-ZbxHistory` | Write values with their own clocks through the API's `history.push`, to backfill. |
| `Get-ZbxAgentItem` | Ask a passive agent for keys, as `zabbix_get` does. It needs no server. |
| `Get-ZbxHistory`, `Get-ZbxTrend` | Read history and trends. A piped item gives its id and value type. |
| `Watch-ZbxItem`, `Receive-ZbxWatch`, `Show-ZbxWatch`, `Get-ZbxWatch`, `Stop-ZbxWatch` | Run a watch session: new values of an item, a host, a group, or every host, each `-Interval`. |
| `Get-ZbxQueue`, `Get-ZbxServerStatus`, `Test-ZbxItem` | Send the server's own requests over its trapper: `queue.get` and `status.get` with a Super admin's token, and `item.test`. |
| `Export-`, `Import-`, `Compare-`, `Test-ZbxConfiguration` | Move a configuration document in JSON, XML, or YAML, and back. |

The commands that speak a Zabbix protocol take TLS parameters named after Zabbix's own configuration, as PEM files. Given once on `Connect-ZbxServer`, they
apply to every command that uses the trapper. See [TLS and PSK](tls-and-psk.md).

## Open an object in the frontend

`Show-ZbxPage` opens the frontend page of any object piped from a `Get-` command, such as a host, a trigger, a problem, or a dashboard.

- `-View` picks another of its pages, such as `Latest` for a host's latest data. `-PassThru` writes the address instead.
- Where no browser can open, as in an SSH session, it warns and writes the address.
- The frontend is the endpoint's folder, unless `Connect-ZbxServer -FrontendUri` names another.

## Run agent and proxy sessions

A `Start-` command of a sibling module starts a session that keeps running after it returns, in the background of the runspace, under its `-Name`. The other
commands find it by that name.

```powershell
Import-Module Community.Zabbix.Agent
Start-ZbxPassiveAgent -Name web-agent -HostName web-02 -AllowedPeer '127.0.0.1', '10.0.0.0/24' -Port 30150
Register-ZbxPassiveAgentHandler -Name web-agent -Key 'app.queue' -ScriptBlock { param($key) "depth of $($key.Parameters[0])" }
Get-ZbxAgentItem -Agent '127.0.0.1:30150' -Key 'app.queue[orders]', 'agent.version'
Initialize-ZbxAgentHost -Session web-agent -HostGroup 'Web servers' -InterfaceAddress '10.0.0.7'
```

- `Get-Zbx*Status` lists the sessions. `Get-Zbx*Log` and `Clear-Zbx*Log` read and empty each one's log buffer.
- `Stop-` ends a session, as `Remove-Module` and a closing runspace do.
- The next command writes any warnings a session logged while no command ran.

### Start an agent

- `Start-ZbxActiveAgent` asks the server for its host's active checks and pushes their values. It needs no open port.
- `Start-ZbxPassiveAgent` listens on `-Port` (10050) and answers only `-AllowedPeer`, which defaults to `127.0.0.1` and `::1`, as Zabbix's own agent does.
- Both answer `agent.ping`, `agent.version`, `agent.hostname`, and `system.uname`, and the [bundled keys](bundled-items.md) unless you pass `-NoBundledItems`.
- Both load [plugins](plugins.md): `-Plugin` names them, `-PluginPath` is their folder, and `-PluginSetting` is each one's settings. A plugin that can't be
  loaded fails the start.

### Answer a key with a scriptblock

`Register-ZbxActiveAgentHandler`, `Register-ZbxPassiveAgentHandler`, and `Register-ZbxAgentHandler` add a scriptblock for a key. `Test-ZbxAgentItem` answers a key
without a server.

- The first argument is the parsed key, and the last output, as text, is the value.
- A handler that throws makes the key not supported until it answers again.
- A handler runs in the session's own runspace, with `Community.Zabbix` and the agent module imported, but not your variables, functions, or connections.
  `$script:` variables persist between calls, and a session's handlers run one at a time.
- The scriptblocks go into the agent's `Items`, the same table a C# agent answers from (see [Items](items.md)). Each replaces a plugin's key or a built-in key of
  the same name.

### Provision the host

`Initialize-ZbxAgentHost` creates or reuses the host, its interface, and one item per answered key. `Publish-ZbxAgentHost` adds items for later handlers.
`Clear-ZbxAgentHost` deletes only what the session created.

### Start a proxy

- `Start-ZbxActiveProxy` runs a proxy's uplink. It pulls the configuration every `-RefreshInterval` (60 s), and uploads what `Register-ZbxActiveProxyDataProvider`
  scriptblocks return. `Set-ZbxActiveProxy` changes the interval while the session runs.
- `Start-ZbxPassiveProxy` listens for the server, and requires `-AllowedPeer` and `-Version` (the server's major.minor). `Send-ZbxPassiveProxyHistory` queues values
  for it to pull.
- `Receive-ZbxProxyConfig` and `Send-ZbxProxyData` speak the protocol once, with no session. `New-ZbxHistoryValue`, `New-ZbxHistoryDataEntry`, and `New-ZbxProxyData`
  build what they send.

### Read a connector stream

`Read-ZbxConnectorStream` parses a connector's newline-delimited JSON, piped or from a file, into item values and events. It isn't an HTTP listener.

## Get help

```powershell
Get-Help Get-ZbxHost -Examples          # worked examples for every command
Get-Help Get-ZbxHost -Full              # every parameter set and type
Get-Help Get-ZbxHost -Online            # the command's reference page on the documentation site
Get-Help about_Community.Zabbix         # the module in one page
```

The concept topics are `about_Community.Zabbix`, `about_Community.Zabbix_Sessions`, `about_Community.Zabbix_Filters`, `about_Community.Zabbix_Pipeline`,
`about_Community.Zabbix_Properties`, `about_Community.Zabbix_ErrorHandling`, and `about_Community.Zabbix.Agent`. `Get-Command -Module Community.Zabbix.Proxy` lists a
module's commands.

The command reference has one page per command, the text `Get-Help -Full` shows: [Community.Zabbix](../reference/Community.Zabbix/README.md),
[Community.Zabbix.Agent](../reference/Community.Zabbix.Agent/README.md), and [Community.Zabbix.Proxy](../reference/Community.Zabbix.Proxy/README.md).

## See also

- [TLS and PSK](tls-and-psk.md): the TLS parameters in full.
- [Telemetry](telemetry.md): the modules' streams and OTLP export.
- [Agents](zabbix-agents.md) and [Proxies](proxies.md): the classes the sessions run on.
- [Known limitations](../KNOWN-LIMITATIONS.md#powershell): the modules' known gaps.
