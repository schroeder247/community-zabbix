# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.DESCRIPTION
The Show-ZbxPage cmdlet opens, in the default browser, the frontend page of each object piped in from a Get-Zbx
command, such as a host, a trigger, a problem or a dashboard, or of the objects -Kind and -Id name. -View picks
another of the object's pages, such as its latest data, its problems or an item's history graph. The frontend is the
API endpoint's folder unless Connect-ZbxServer -FrontendUri named another. -PassThru writes the page's address
instead, and where no browser can open, as in an SSH session, the command warns and writes the address.
#>
#region Open a host's configuration form
# The piped host's kind picks the page: on 7.4 and 8.0 the host's edit dialog, on 7.0 its configuration page.
Get-ZbxHost -Name 'web-01' | Show-ZbxPage
#endregion

#region Write the address instead of opening it
# -PassThru writes the page's Uri: for a link in a report, a ticket or a chat message. -View picks another page of
# the object; a host has Latest and Problems besides its form.
Get-ZbxHost -Name 'web-01' | Show-ZbxPage -View Latest -PassThru
Get-ZbxHost -Name 'web-01' | Show-ZbxPage -View Problems -PassThru | ForEach-Object AbsoluteUri
#endregion

#region Open an item's history as a graph
# An item read without its host is read again by id, so its form opens in the host's or the template's context.
Get-ZbxItem -Host 'web-01' -Name 'Requests' | Show-ZbxPage -View Graph
#endregion

#region Link an object by its kind and id
# Without a record, -Kind and -Id name the object; it is read by id first, for what its page needs.
Show-ZbxPage -Kind Dashboard -Id 1 -PassThru
(Get-ZbxHost -Name 'web-01', 'db-01').HostId | Show-ZbxPage -Kind Host -PassThru
#endregion
