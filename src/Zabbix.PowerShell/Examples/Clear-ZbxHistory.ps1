# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Deletes the history and trends of items.
.DESCRIPTION
The Clear-ZbxHistory cmdlet deletes every value the server stored for the items given to -InputObject (names, ids or
piped items), their history and their trends, with history.clear. The items themselves stay and keep collecting. It is
the heaviest delete the server runs, so the command asks twice for each item unless -Force is given, and -WhatIf lists
the items it would clear. It writes nothing.
#>
#region Clear the history of one item
# -Force answers both questions; the item keeps collecting new values.
Get-ZbxItem -Host 'web-01' -Name 'Requests' | Clear-ZbxHistory -Force
#endregion

#region Preview clearing the history of a host's items
# -WhatIf names each item the command would clear, and deletes nothing.
Get-ZbxItem -Host 'web-01' | Clear-ZbxHistory -WhatIf
#endregion
