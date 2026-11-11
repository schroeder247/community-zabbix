# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#region Create a trigger on an item's last value
# -Name (position 0) is the trigger's name and -Expression (position 1) the problem expression; -Severity is
# the severity of the problems it raises.
New-ZbxTrigger -Name 'Request backlog on web-01' -Expression 'last(/web-01/app.requests)>500' -Severity Average |
    Select-Object TriggerId, Name, Severity
#endregion

#region Create a trigger that recovers on its own expression
# With -RecoveryMode RecoveryExpression the problem ends only when -RecoveryExpression is true, not when the problem
# expression stops being true, so a value that hovers at the limit does not flap.
New-ZbxTrigger -Name 'Sustained load on web-01' -Expression 'avg(/web-01/app.requests,5m)>200' `
    -RecoveryMode RecoveryExpression -RecoveryExpression 'avg(/web-01/app.requests,5m)<150' -Severity High |
    Select-Object Name, RecoveryMode
#endregion

#region Create a trigger with tags and an operational data line
# -Tag are copied to the problems the trigger raises; -OperationalData shows the current value beside the problem.
New-ZbxTrigger -Name 'No requests on web-01' -Expression 'nodata(/web-01/app.requests,10m)=1' -Severity Warning `
    -Tag @{ tag = 'service'; value = 'shop' } -OperationalData 'Last value: {ITEM.LASTVALUE1}' | Select-Object Name
#endregion
