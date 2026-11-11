# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Tests the connection to an LDAP directory and a user's login through it.
.DESCRIPTION
The Test-ZbxUserDirectory cmdlet sends the settings of an LDAP user directory with userdirectory.test, as the Test
button of the frontend's directory form does: the server connects, binds, searches for the test user and checks the
password. -Request carries the settings, cast from a hashtable keyed by the API's field names; a directory that already
exists is named by its userdirectoryid. A failed test is an error that names what went wrong, so use -ErrorAction Stop
to catch it.
#>
#region Test a directory's settings before saving them
# The test logs in as jdoe through the directory; the error says which step failed.
try {
    Test-ZbxUserDirectory -Request @{
        idp_type = 1; host = 'ldap.example.com'; port = 389; base_dn = 'ou=people,dc=example,dc=com'; search_attribute = 'uid'
        test_username = 'jdoe'; test_password = 'Chang3-me'
    } -ErrorAction Stop
    'The directory accepted the test login.'
}
catch {
    "The directory test failed: $($_.Exception.Message)"
}
#endregion
