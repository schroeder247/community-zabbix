# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Gets the server's authentication settings.
.DESCRIPTION
The Get-ZbxAuthentication cmdlet reads the server's authentication settings with authentication.get: the default
authentication method, the password rules, LDAP, SAML and HTTP authentication, just-in-time provisioning and
multi-factor authentication. It writes one ZabbixAuthenticationSettings object and needs a connection made with
Connect-ZbxServer as a Super admin. Set-ZbxAuthentication changes the settings.
#>
#region Read the password rules
# PasswordCheckRules is a set of flags: the character classes a password must contain.
Get-ZbxAuthentication | Select-Object AuthenticationType, PasswordMinLength, PasswordCheckRules
#endregion

#region Check whether multi-factor authentication is on
# MfaStatus is true when users must give a second factor; MfaId is the default method.
(Get-ZbxAuthentication).MfaStatus
#endregion
