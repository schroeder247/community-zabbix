# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Creates or updates a dashboard from a document.
.DESCRIPTION
The Publish-ZbxDashboard cmdlet reads a dashboard document, JSON text, files (-Path, which takes wildcards, or -LiteralPath) or an object with the same
fields as New-ZbxDashboard's request, and makes the server hold it: a dashboard with the document's name is updated in
place with dashboard.update, otherwise one is created with dashboard.create. A script can keep its dashboards as files
and publish them again after every change. It writes the dashboard only with -PassThru, and -WhatIf shows what it would
do.
#>
#region Publish a dashboard from JSON text
# The document names the dashboard and its pages; each widget has a type, a position and a size.
'{"name":"Ops overview","pages":[{"widgets":[{"type":"problems","x":0,"y":0,"width":36,"height":5}]}]}' |
    Publish-ZbxDashboard -PassThru | Select-Object DashboardId, Name
#endregion

#region Publish a dashboard kept as a file
# -LiteralPath reads the document from a file; publishing it again after a change updates the same dashboard.
$folder = New-Item -ItemType Directory -Path (Join-Path ([IO.Path]::GetTempPath()) "dashboards-$PID") -Force
'{"name":"Ops overview","pages":[{"widgets":[{"type":"clock","x":0,"y":0,"width":12,"height":3}]}]}' | Set-Content (Join-Path $folder 'ops.json')
Publish-ZbxDashboard -LiteralPath (Join-Path $folder 'ops.json')
#endregion

Remove-Item $folder -Recurse -Force
