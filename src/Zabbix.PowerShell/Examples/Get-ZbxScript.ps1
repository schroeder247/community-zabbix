# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxScript -Name 'Reboot host' -Scope ManualHost -Type Script -ExecuteOn Agent -Command 'sudo /sbin/shutdown -r +1' -Confirmation 'Reboot {HOST.NAME}?' -MenuPath 'Maintenance' -GroupId (Get-ZbxHostGroup -Name 'Linux servers').GroupId

#region Get a script by name
# -Name matches exactly; a * turns it into a search. The built-in 'Traceroute' script is a good one to look at.
Get-ZbxScript -Name 'Traceroute' | Select-Object ScriptId, Name, Type, Scope, ExecuteOn, Command
#endregion

#region List the scripts that can run on a host
# -Host returns the scripts available for the host (those without a host group limit, and those limited to one
# of its groups).
Get-ZbxScript -Host 'db-01' | Select-Object Name, MenuPath, Scope
#endregion

#region List the scripts that ask for confirmation
# Confirmation holds the text the frontend shows before running the script; empty means it runs at once.
Get-ZbxScript | Where-Object Confirmation | Select-Object Name, Confirmation
#endregion

$null = Remove-ZbxScript -InputObject 'Reboot host' -Confirm:$false
