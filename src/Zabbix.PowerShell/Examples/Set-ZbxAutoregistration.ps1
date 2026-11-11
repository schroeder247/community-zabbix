# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$before = Get-ZbxAutoregistration

<#
.SYNOPSIS
Changes which connections active agents may autoregister over.
.DESCRIPTION
The Set-ZbxAutoregistration cmdlet changes the server's autoregistration settings with autoregistration.update:
-TlsAccept sets the connection modes autoregistration accepts, and -TlsPskIdentity with -TlsPsk the pre-shared key an
agent must present. A setting not given keeps its value. The command writes the settings only with -PassThru, -WhatIf
shows what would change, and it needs a Super admin's connection.
#>
#region Accept only unencrypted autoregistration
# -PassThru writes the settings as the server holds them after the change.
Set-ZbxAutoregistration -TlsAccept Unencrypted -PassThru
#endregion

#region Preview accepting PSK autoregistration
# Several modes are given as a list and combined into one setting; the agents must then present this identity and
# key. -WhatIf shows the change and makes none.
Set-ZbxAutoregistration -TlsAccept Unencrypted, Psk -TlsPskIdentity 'autoreg' -TlsPsk (ConvertTo-SecureString '1f87b595725ac58dd977beef14b97461' -AsPlainText -Force) -WhatIf
#endregion

Set-ZbxAutoregistration -TlsAccept $before.TlsAccept
