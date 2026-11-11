# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
Evaluates trigger expressions on the server.
.DESCRIPTION
The Test-ZbxExpression cmdlet sends the expressions in -Expression to the server's trapper (expressions.evaluate) and
writes, for each, whether it evaluates to true or false, or the error that stops it, such as an unknown function. It
checks an expression's syntax and its constant parts before a trigger uses it. It needs a connection made with
Connect-ZbxServer and the trapper in -Server or the connection's.
#>
#region Evaluate expressions
# Each result has the expression, its value (1 is true, 0 false) and any error.
Test-ZbxExpression -Expression '1+1=2', '10>20' -Server 'zabbix.example:10051'
#endregion

#region Find the expressions with an error
# A result with an Error did not evaluate; here the function name is misspelled.
'abs(-5)=5', 'abz(-5)=5' | Test-ZbxExpression -Server 'zabbix.example:10051' | Where-Object Error
#endregion
