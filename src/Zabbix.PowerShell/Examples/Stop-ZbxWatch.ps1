# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
Watch-ZbxItem -Name 'requests' -HostName 'web-01' -Key 'app.requests'
Watch-ZbxItem -Name 'linux' -Group 'Linux servers'

<#
.SYNOPSIS
Stops a watch.
.DESCRIPTION
The Stop-ZbxWatch cmdlet stops the watch named in -Name, or each watch piped from Get-ZbxWatch: it stops reading, ends
every Receive-ZbxWatch reading from it, and removes it from the runspace. A watch also stops when the runspace closes.
-WhatIf shows which watch it would stop. It writes nothing.
#>
#region Stop a watch by name
# -Name (position 0) is the name Watch-ZbxItem gave the watch.
Stop-ZbxWatch -Name 'requests'
#endregion

#region Stop every watch of the session
# Get-ZbxWatch writes each running watch, and its Name binds to -Name.
Get-ZbxWatch | Stop-ZbxWatch
#endregion
