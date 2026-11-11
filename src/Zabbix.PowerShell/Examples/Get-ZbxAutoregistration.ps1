# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Gets the server's autoregistration settings.
.DESCRIPTION
The Get-ZbxAutoregistration cmdlet reads the server's autoregistration settings with autoregistration.get: which
connections an active agent may autoregister over, unencrypted, with a pre-shared key or both. The PSK itself is never
returned. It writes one ZabbixAutoregistrationSettings object and needs a connection made with Connect-ZbxServer as a
Super admin.
#>
#region Read which connections may autoregister
# TlsAccept is a set of flags: Unencrypted, Psk, or both.
Get-ZbxAutoregistration
#endregion

#region Check whether unencrypted agents may autoregister
# -band tests one flag of the set.
[bool]((Get-ZbxAutoregistration).TlsAccept -band [Community.Zabbix.ZabbixTlsAcceptModes]::Unencrypted)
#endregion
