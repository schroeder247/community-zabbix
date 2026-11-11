# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
$null = New-ZbxService -Name 'Checkout' -Algorithm MostCritical -Position 0 -Tag @{ tag = 'service'; value = 'checkout' }
$null = New-ZbxSla -Name 'Business hours SLA' -Period Weekly -Slo 99.9 -EffectiveDate ([DateTimeOffset]::UtcNow.AddDays(-30).ToUnixTimeSeconds()) -Timezone 'UTC' -ServiceTag @{ tag = 'service'; value = 'checkout' }

<#
.SYNOPSIS
Gets the service level indicators of an SLA.
.DESCRIPTION
The Get-ZbxSli cmdlet reads an SLA's report with sla.getsli: for each reporting period and each service the SLA covers,
the uptime, downtime, error budget and the SLI reached. By default it reports the SLA's recent periods; -PeriodFrom,
-PeriodTo and -Periods choose others, and -ServiceId narrows the services. It writes a ZabbixSliReport, whose Sli holds
one list per period of one SLI per service, and needs a connection made with Connect-ZbxServer.
#>
#region Get an SLA's report
# -Sla (position 0) takes the SLA's name, id or object.
Get-ZbxSli -Sla 'Business hours SLA'
#endregion

#region Get the last four weeks of an SLA piped from Get-ZbxSla
# -Periods sets how many reporting periods (here weeks) the report covers.
Get-ZbxSla -Name 'Business hours SLA' | Get-ZbxSli -Periods 4
#endregion
