# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
# Vocabulary: asmith

#region Create a user in a user group
# -Password is a SecureString; -RoleId is required (read it with Get-ZbxRole) and -UserGroup takes group names, ids
# or objects. The created user is read back and written to the pipeline.
$password = ConvertTo-SecureString 'Chang3-me-at-first-login!' -AsPlainText -Force
New-ZbxUser -Username 'jdoe' -FirstName 'John' -LastName 'Doe' -Password $password -RoleId (Get-ZbxRole -Name 'User role').RoleId -UserGroup 'Operators' |
    Select-Object UserId, Username, FirstName, LastName, RoleId
#endregion

#region Give the new user an e-mail address to be notified on
# -Media takes one hashtable per media: the media type's id, the address(es) in sendto, and optionally the
# severities (a bit mask, 48 = High and Disaster) and the period when it may be used.
$email = Get-ZbxMediaType -Name 'Email'
$password = ConvertTo-SecureString 'Chang3-me-at-first-login!' -AsPlainText -Force
$null = New-ZbxUser -Username 'asmith' -FirstName 'Ann' -LastName 'Smith' -Password $password -RoleId (Get-ZbxRole -Name 'User role').RoleId -UserGroup 'Operators' -Media @{ mediatypeid = $email.MediaTypeId; sendto = @('asmith@example.com'); severity = 48; period = '1-5,08:00-18:00' }
Get-ZbxUser -Name 'asmith' -SelectUserGroups | Select-Object Username, @{ n = 'Groups'; e = { $_.UserGroups.Name -join ', ' } }
#endregion

#region Create users from a CSV file
# Each row (here inline; Import-Csv reads the same from a file) becomes one New-ZbxUser call with a random
# password, for users who sign in through LDAP or SAML. -WhatIf shows what would be created.
$rows = 'Username,FirstName,LastName', '"jdoe",John,Doe' | ConvertFrom-Csv
$role = (Get-ZbxRole -Name 'User role').RoleId
foreach ($row in $rows) {
    $random = ConvertTo-SecureString ([Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(24))) -AsPlainText -Force
    New-ZbxUser -Username $row.Username -FirstName $row.FirstName -LastName $row.LastName -Password $random -RoleId $role -UserGroup 'Operators' -WhatIf
}
#endregion
