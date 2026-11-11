# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Sends a test message through a media type.
.DESCRIPTION
The Test-ZbxMediaType cmdlet asks the server's trapper to send a test message through the media type in -MediaType to
the recipient in -SendTo, as the Test button of the frontend's media type list does (alert.send). It writes what the
media type returned, such as a webhook's response; a media type that fails ends with the server's error. It needs the
trapper in -Server or the connection's, and -WhatIf shows the message without sending it.
#>
# The examples test a webhook media type created here, whose script only returns "OK": nothing is sent anywhere.
$null = New-ZbxMediaType -Type Webhook -Name 'Service status' -Status Active -Script 'return "OK";'

#region Send a test message through a media type
# The server runs the media type once (the frontend's "Test" button; alert.send on the server's trapper port,
# given with -Server). The result is what the media type returned.
Test-ZbxMediaType -MediaType 'Service status' -SendTo 'ops' -Subject 'Test' -Message 'Hello from PowerShell' -Server 'zabbix.example:10051'
#endregion

#region Preview a test of the e-mail media type without sending
# ConfirmImpact is Medium; -WhatIf prints what would be sent and to whom, and sends nothing.
Test-ZbxMediaType -MediaType 'Email' -SendTo 'jdoe@example.com' -Server 'zabbix.example:10051' -WhatIf
#endregion

#region Test a media type read with Get-ZbxMediaType
# -MediaType also takes the object; the test fails with the server's error text when the media type does not work.
$mediaType = Get-ZbxMediaType -Name 'Service status'
Test-ZbxMediaType -MediaType $mediaType -SendTo 'ops' -Server 'zabbix.example:10051'
#endregion
