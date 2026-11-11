# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Build a step that expects a status code
# -Name and -Url are positional; -StatusCodes is a list or ranges such as 200,301-302.
New-ZbxHttpTestStep 'Home page' 'https://shop.example.com/' -StatusCodes '200'
#endregion

#region Build a step that posts a form and checks the answer
# -Posts are the form fields, -Required is text (or a regular expression) the answer must contain, and
# {$SHOP.PASSWORD} is a user macro the server resolves when it runs the step.
New-ZbxHttpTestStep 'Log in' 'https://shop.example.com/login' -Posts @{ name = 'user'; value = 'monitor' }, @{ name = 'password'; value = '{$SHOP.PASSWORD}' } -Required 'Welcome' -Timeout '15s'
#endregion
