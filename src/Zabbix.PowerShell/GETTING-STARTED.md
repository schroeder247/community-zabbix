<!--
This Source Code Form is subject to the terms of the Mozilla Public
License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
-->
# Get started with the PowerShell module

In this walk-through, you use the `Community.Zabbix` module to connect to a Zabbix server, create a host with a trapper item, change it,
send it a value, read the value back, handle an error, and clean up.

Every command runs against Zabbix 7.0, 7.4, and 8.0. The output is from a 7.0.31 server. 7.4 and 8.0 print the same, apart from the version,
the ids, and the clock.

> [!CAUTION]
> Provided "as is", without warranty of any kind: use at your own risk. The walk-through creates and deletes objects on the server it
> connects to. Run it against a lab server, not production.

## Prerequisites

- PowerShell 7.6 or later, the first PowerShell on .NET 10.
- A Zabbix 7.0, 7.4, or 8.0 server you may create objects on, its frontend URL, and its trapper (`zabbix.example:10051`).
- A user that may create hosts. Its user name and password are enough to start. An API token works too: in the frontend, go to
  *Users > API tokens > Create API token*, and copy the secret, which it shows once.

Install the module from the PowerShell Gallery:

```powershell
Install-PSResource Community.Zabbix
```

Or build it from the repository with the .NET 10 SDK, and import the built manifest. [Install](../../docs/INSTALL.md) has the other ways, including
how to import the modules by name.

```powershell
git clone https://github.com/schroeder247/community-zabbix
cd community-zabbix
dotnet build Zabbix.slnx
Import-Module ./src/Zabbix.PowerShell/bin/Debug/net10.0/Community.Zabbix.psd1
```

## 1. Connect

```powershell
$token = Read-Host -AsSecureString 'API token'
Connect-ZbxServer -Uri https://zabbix.example/ -ApiToken $token -Trapper zabbix.example:10051 -PassThru
Get-ZbxApiVersion
```

```text
Name           ApiVersion Endpoint                                Trapper              IsClosed
----           ---------- --------                                -------              --------
zabbix.example 7.0.31     https://zabbix.example/api_jsonrpc.php zabbix.example:10051 False

Major  Minor  Build  Revision
-----  -----  -----  --------
7      0      31     -1
```

- `-Uri` takes the frontend URL or the API endpoint.
- `-ApiToken` takes a `SecureString`, never a plain or positional string.
- `-Trapper` is `host` or `host:port`, with port 10051 if you name none. Several are the nodes of an HA pair, tried in order. Without it, the
  commands that use it ask the API for the active HA node.
- A credential the server refuses ends the command with an error and adds no connection: `ZabbixApi.SessionTerminated` for an unknown token,
  and `ZabbixApi.LoginFailed` for a wrong password.
- The connection belongs to this runspace. A job or a `ForEach-Object -Parallel` branch connects on its own
  (`Get-Help about_Community.Zabbix_Sessions`).

No token yet? Log in with `-Credential`, which works for the whole walk-through. `New-ZbxToken -Generate` then makes a token. Its `Secret` property
holds the secret as a `SecureString`, and the server shows it only this once.

```powershell
Connect-ZbxServer -Uri https://zabbix.example/ -Credential (Get-Credential) -Trapper zabbix.example:10051
$token = (New-ZbxToken -Name 'Getting started' -Generate).Secret
Connect-ZbxServer -Uri https://zabbix.example/ -ApiToken $token -Trapper zabbix.example:10051
```

## 2. Look around

```powershell
Get-ZbxHostGroup -Name 'Linux servers' | Select-Object GroupId, Name
Get-ZbxHost -Name 'Zabbix server' | Select-Object HostId, HostName, VisibleName, Status
Get-ZbxHost -Name 'Zabbix server' | Get-Member -MemberType Property
```

```text
GroupId Name
------- ----
2       Linux servers

HostId HostName      VisibleName   Status
------ --------      -----------   ------
10084  Zabbix server Zabbix server Enabled
```

- Results are typed records with the SDK's property names. A host's technical name (the API's `host`) is `HostName`, and its visible name
  (`name`) is `VisibleName`. `Get-Member` lists them: 48 on a 7.0 host.
- `-Name` ignores case. `*` and `?` are wildcards (`Get-ZbxHost -Name 'web-*'`). Without one, it matches the whole name.

## 3. Create a host group, a host, and a trapper item

```powershell
$group = New-ZbxHostGroup -Name 'Web servers'
$web = New-ZbxHost -Name 'web-01' -Group 'Web servers' -InterfaceType Agent -InterfaceIP '10.0.0.11' `
    -Tag @{ env = 'prod'; role = 'web' }
$item = New-ZbxItem -HostId $web.HostId -Name 'Requests' -Key 'app.requests' -Type Trapper -ValueType Unsigned `
    -TrapperHosts '0.0.0.0/0,::/0'
$item | Select-Object ItemId, Name, Key, Type, ValueType
```

```text
ItemId    : 54301
Name      : Requests
Key       : app.requests
Type      : Trapper
ValueType : Unsigned
```

A `New-` command creates the object and writes it back as the server stored it.

| Parameter | What it takes |
| --- | --- |
| `-Name` | The API's `host` field. `-Host` is its alias, and the visible name is `-DisplayName`. |
| `-Group` | Names, ids, or objects. |
| `-InterfaceType`, `-InterfaceIP` | One agent interface, on port 10050 by default. `-Interface` takes any number, as hashtables. |
| `-Tag` | One hashtable per tag, or one hashtable of `name = value` pairs, as here. |
| `-TrapperHosts` | The item's allow-list of senders. 8.0 defaults it to `{$TRAPPER.ALLOWED_HOSTS}` (localhost). `0.0.0.0/0,::/0` accepts everyone, which is right only on a lab server. |
| `-AdditionalProperty` | Any field without a parameter (`Get-Help about_Community.Zabbix_Properties`). |

## 4. Change the host through the pipeline

```powershell
Get-ZbxHost -Name 'web-*' | Set-ZbxHost -Description 'Front-end web server' -WhatIf
Get-ZbxHost -Name 'web-*' | Set-ZbxHost -Description 'Front-end web server' -PassThru | Select-Object HostName, Description
Add-ZbxTag -InputObject 'web-01' -Name 'team' -Value 'platform'
(Get-ZbxHost -Name 'web-01' -SelectTags).Tags | Select-Object Tag, Value
```

```text
What if: Performing the operation "Set host" on target "host 'web-01' (14524)".

HostName Description
-------- -----------
web-01   Front-end web server

Tag  Value
---  -----
role web
env  prod
team platform
```

- `-WhatIf` shows what would change and changes nothing.
- `Set-` and `Remove-` take objects, ids, or names from the pipeline, and change them in one API call when the pipeline ends.
- `Set-` writes nothing by itself. `-PassThru` reads the changed objects back and writes them.

`Get-Help about_Community.Zabbix_Pipeline` has the details.

## 5. Send a value and read it back

```powershell
do {
    $sent = Send-ZbxData -HostName 'web-01' -Key 'app.requests' -Value 42
    if ($sent.Processed -eq 0) { Start-Sleep -Seconds 5 }
} while ($sent.Processed -eq 0)
$sent

do { $last = $item | Get-ZbxHistory -Limit 1 -SortField clock -SortOrder Descending } until ($last)
$last
```

```text
WARNING: Zabbix reported 1 of 1 value(s) failed at zabbix.example:10051

Processed Failed Total TimeSpent        HasFailures
--------- ------ ----- ---------        -----------
1         0      1     00:00:00.0000470 False

ItemId Clock                Value
------ -----                -----
54301  9/28/2026 2:33:57 PM 42
```

- The first loop sends until the value is processed. The server learns about a new item at its next configuration-cache refresh, so the first
  send can come back `failed`, as in the warning above.
- The second loop reads until the value is there, because the server writes it to history a moment after it answers.
- With `-Server`, `Send-ZbxData` needs no connection, as `zabbix_sender` needs no API token.
- A piped item gives `Get-ZbxHistory` its id and its value type, which names the history table to read. `-From` and `-Till` bound the window.
- `-Filter` takes `history.get`'s own parameters instead: `@{ itemids = @($item.ItemId); history = 3 }`. `history` is the value type, and 3 is
  unsigned.

### Send over TLS

A host that accepts only encrypted connections (its `tls_accept`) takes values over TLS. Give the trapper's TLS once, at the connection, or give a
command TLS parameters of its own, which it uses instead:

```powershell
$key = Read-Host -AsSecureString -Prompt 'PSK'               # or -Psk ./web-01.psk, the path of a key file
Connect-ZbxServer -Uri https://zabbix.example/ -ApiToken $token -Trapper zabbix.example:10051 -TrapperPskIdentity 'web-01' -TrapperPsk $key
Send-ZbxData -HostName 'web-01' -Key 'app.requests' -Value 42

# Certificates, as zabbix_sender's --tls-cert-file, --tls-key-file and --tls-ca-file:
Send-ZbxData -Server zabbix.example -HostName 'web-01' -Key 'app.requests' -Value 43 `
    -Certificate ./client.crt -CertificateKey ./client.key -CertificateAuthority ./ca.crt -ServerSubject 'CN=zabbix-server'
```

Every command that speaks the server's own protocol uses the connection's TLS: `Send-ZbxData`, `Get-ZbxQueue`, `Get-ZbxServerStatus`, `Test-ZbxItem`,
and others. [TLS and PSK](../../docs/guides/tls-and-psk.md#use-it-from-powershell) has every parameter.

## 6. Handle an error

```powershell
try {
    New-ZbxHostGroup -Name 'Web servers' -ErrorAction Stop
}
catch {
    $_.FullyQualifiedErrorId
    $_.Exception.Message
}
```

```text
ZabbixApi.Validation,Community.Zabbix.PowerShell.Commands.Generated.NewHostGroupCommand
hostgroup.create: Invalid params. Host group "Web servers" already exists.
```

- A failed API call is a non-terminating error, or an exception with `-ErrorAction Stop`. Branch on the id's kind, not the message.
- Every id is `Zabbix<Layer>.<Kind>` (`ZabbixApi.Validation`, `ZabbixWire.ConnectionRefused`, `ZabbixHttp.ConnectionError`), or one word for the
  module's own rules (`ZabbixReferenceNotFound`).
- One failure is one error record. `Get-ZbxError` lists the recent ones.

## 7. Clean up

```powershell
Get-ZbxHost -Name 'web-*' | Remove-ZbxHost -Confirm:$false
Remove-ZbxHostGroup -InputObject 'Web servers' -Confirm:$false
Disconnect-ZbxServer
```

`Remove-` commands have ConfirmImpact High. PowerShell asks before deleting unless you pass `-Confirm:$false` or `-Force`. `-Force` never overrides `-WhatIf`.

## Next steps

- `Get-Help about_Community.Zabbix`: the module in one page. `about_Community.Zabbix_Sessions`, `about_Community.Zabbix_Properties`,
  `about_Community.Zabbix_Pipeline`, `about_Community.Zabbix_Filters`, and `about_Community.Zabbix_ErrorHandling` have the details.
- `Get-Help <cmdlet> -Examples`: worked examples for every command, with real values. The hand-written ones run against a live server
  (`tools/Test-ZbxHelpExamples.ps1`). `Get-Help <cmdlet> -Online` opens the command's reference page, on the documentation site.
- `Export-ZbxConfiguration` and `Import-ZbxConfiguration`: configuration as one document, and back. `Compare-ZbxConfiguration` and `Test-ZbxConfiguration`
  check it.
- [The PowerShell guide](../../docs/guides/powershell.md) and the [command reference](../../docs/reference/Community.Zabbix/README.md), with one page per
  command.
- [The .NET walk-through](../../docs/GETTING-STARTED.md): the same server from C#.
- The agent and proxy commands, in the sibling modules: `Install-PSResource Community.Zabbix.Agent` and `Install-PSResource Community.Zabbix.Proxy`. Each
  imports `Community.Zabbix` first. Their references are [Community.Zabbix.Agent](../../docs/reference/Community.Zabbix.Agent/README.md) and
  [Community.Zabbix.Proxy](../../docs/reference/Community.Zabbix.Proxy/README.md).
