# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$before = Get-ZbxAuthentication

<#
.SYNOPSIS
Changes the server's authentication settings.
.DESCRIPTION
The Set-ZbxAuthentication cmdlet changes the server's authentication settings with authentication.update: each
parameter given sets one setting, and every setting not given keeps its value. It changes how every user logs in, so
check the result before you log out. The command writes the settings, read back from the server, only with -PassThru,
and -WhatIf shows what would change. It needs a Super admin's connection.
#>
#region Require longer passwords
# Applies to passwords set from now on; existing passwords keep working.
Set-ZbxAuthentication -PasswordMinLength 12 -PassThru | Select-Object PasswordMinLength
#endregion

#region Preview turning on LDAP authentication
# -WhatIf shows the change and makes none; LDAP also needs a user directory (New-ZbxUserDirectory).
Set-ZbxAuthentication -LdapAuthenticationEnabled $true -WhatIf
#endregion

Set-ZbxAuthentication -PasswordMinLength $before.PasswordMinLength
