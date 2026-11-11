# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Rename a host group
# -InputObject (position 0) is the group and -Name (position 1) its new name; hosts, permissions and actions follow.
Set-ZbxHostGroup 'Web servers' 'Web servers (production)' -PassThru | Select-Object GroupId, Name
#endregion

#region Preview renaming a group
# -WhatIf names the group the command would change, and changes nothing.
Set-ZbxHostGroup -InputObject 'Linux servers' -Name 'Linux servers (production)' -WhatIf
#endregion
