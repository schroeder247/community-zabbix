# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: web-98; web-99
#region See the failures a script caught
# Get-ZbxError reads the session's own $Error, keeping only the module's failures, newest first, so failures a
# script caught (or hid with -ErrorAction SilentlyContinue) can still be looked at afterwards.
'web-98', 'web-99' | ForEach-Object {
    try { Remove-ZbxHost -InputObject $_ -Confirm:$false -ErrorAction Stop } catch { Write-Warning $_ }
}
Get-ZbxError -Last 2 | Select-Object FullyQualifiedErrorId, @{ n = 'Message'; e = { $_.Exception.Message } }
#endregion

#region Tell failures apart by their error id
# Each failure's FullyQualifiedErrorId names its kind (ZabbixReferenceNotFound, ZabbixApi.Validation...), so a
# script can react to one kind without parsing messages. Here web-01 already exists, so the server refuses it.
'web-02', 'web-01' | ForEach-Object {
    New-ZbxHost -Host $_ -Group 'Web servers' -ErrorAction SilentlyContinue
} | Select-Object TechnicalName
Get-ZbxError -Last 1 | ForEach-Object {
    if ($_.FullyQualifiedErrorId -like 'ZabbixApi.*') { 'The server refused: ' + $_.Exception.Message }
    else { 'Other failure: ' + $_.Exception.Message }
}
#endregion

#region Summarise every buffered failure
# -AllErrors reads every failure still in $Error (bounded by $MaximumErrorCount); group them by kind.
Get-ZbxError -AllErrors | Group-Object { $_.FullyQualifiedErrorId.Split(',')[0] } | Select-Object Count, Name
#endregion
