# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#
# The module against every live server of the matrix, in a real pwsh importing the built module folder. The servers come
# from the live matrix's one environment contract (ZABBIX_TEST_APIS, ZABBIX_TEST_TRAPPERS; printed by
# tests/lab/lab.sh env, read through tests/lab/Lab.psm1's Get-LabServer); none configured is a skip.
#
#   tests/lab/lab.sh start                  # once: creates the lab's throwaway servers (docs/TESTING.md, "The lab")
#   eval "$(tests/lab/lab.sh env)"
#   pwsh -c 'Invoke-Pester ./tests/Zabbix.PowerShell.Pester -Output Detailed'
#
# (tests/lab/live.sh pester does the last two.) The scenarios drive the module Community.Zabbix (the API, the sender,
# the pipeline) and a passive agent of the module Community.Zabbix.Agent that the server polls; ZBX_MODULE and
# ZBX_AGENT_MODULE override their manifests (default: the Debug builds of src/Zabbix.PowerShell and
# src/Zabbix.PowerShell.Agent). Every object a scenario creates is named zbx-it-pester-<8 hex>-…, removed
# through the module, and then the lab's leak sweep must find nothing under that prefix on any type.

BeforeDiscovery {
    Import-Module (Join-Path $PSScriptRoot '../lab/Lab.psm1') -Force
    $script:servers = @(Get-LabServer | Where-Object { $env:ZABBIX_TEST_APIS -and ($_.Token -or ($_.TokenFile -and (Test-Path $_.TokenFile))) })
}

Describe 'Community.Zabbix module against <_.Label> (<_.Url>)' -ForEach $servers -Skip:(-not $servers) {
    BeforeAll {
        Import-Module (Join-Path $PSScriptRoot '../lab/Lab.psm1') -Force
        $manifest = $env:ZBX_MODULE ?? (Join-Path $PSScriptRoot '../../src/Zabbix.PowerShell/bin/Debug/net10.0/Community.Zabbix.psd1')
        Import-Module $manifest -Force -ErrorAction Stop
        $server = $_
        $token = ConvertTo-SecureString (Get-LabServerToken -Server $server) -AsPlainText -Force
        $trapper = if ($server.ServerPort) { @{ Trapper = "$($server.ServerHost):$($server.ServerPort)" } } else { @{} }
        $connection = Connect-ZbxServer -Uri $server.Url -ApiToken $token -Name live @trapper -PassThru -ErrorAction Stop
        $run = 'zbx-it-pester-' + [guid]::NewGuid().ToString('N').Substring(0, 8)

        # Waits on an observable, never on time: $Probe returns something when the awaited state holds.
        function Wait-Live([scriptblock]$Probe, [string]$Awaited, [int]$Seconds = 60) {
            $clock = [System.Diagnostics.Stopwatch]::StartNew()
            while ($true) {
                $value = & $Probe
                if ($value) { return $value }
                if ($clock.Elapsed.TotalSeconds -gt $Seconds) { throw "waited $Seconds s for $Awaited" }
                Start-Sleep -Milliseconds 250
            }
        }
    }

    AfterAll {
        # Whatever a failed scenario left: swept by id over raw JSON-RPC, every type.
        Invoke-LabLeakSweep -ServerId $server.Label -Prefix $run | Out-Null
        Disconnect-ZbxServer -Name live -ErrorAction SilentlyContinue
    }

    Context 'reads' {
        It 'connects and reads the API version it reports' {
            $connection.ApiVersion | Should -BeOfType [version]
            Get-ZbxApiVersion | Should -Be $connection.ApiVersion
            "$($connection.ApiVersion.Major).$($connection.ApiVersion.Minor)" | Should -Be $server.Label
        }

        It 'is the current server of this runspace' {
            (Get-ZbxContext).Current.Name | Should -Be 'live'
        }

        It 'reads hosts as typed records' {
            $hosts = @(Get-ZbxHost -Limit 5)
            $hosts.Count | Should -BeGreaterThan 0
            $hosts[0] | Should -BeOfType [Community.Zabbix.ZabbixHost]
            $hosts[0].HostId | Should -Match '^\d+$'
        }

        It 'selects a host by name, by wildcard and by id' {
            $byName = Get-ZbxHost 'Zabbix server'
            $byName.HostName | Should -Be 'Zabbix server'
            (Get-ZbxHost 'Zabbix*' | Select-Object -First 1).HostName | Should -BeLike 'Zabbix*'
            (Get-ZbxHost -Id $byName.HostId).HostName | Should -Be 'Zabbix server'
        }

        It 'takes the typed get parameters' {
            $filter = [Community.Zabbix.ZabbixHostGetParameters]@{ Limit = 1 }
            @(Get-ZbxHost -Filter $filter).Count | Should -Be 1
        }

        It 'refuses a bad id at binding' {
            { Get-ZbxHost -Id 'web01' -ErrorAction Stop } | Should -Throw -ErrorId 'CannotConvertArgumentNoMessage,Community.Zabbix.PowerShell.Commands.GetZbxHostCommand'
        }

        It 'names an API failure by its kind, and a refused token connects nothing' {
            # Connect-ZbxServer proves the credential: the server's refusal of the token ends the command.
            $refused = { Connect-ZbxServer -Uri $server.Url -ApiToken (ConvertTo-SecureString 'not-a-token' -AsPlainText -Force) -Name bad -NoDefault } |
                Should -Throw -ErrorId 'ZabbixApi.*,Community.Zabbix.PowerShell.Commands.ConnectZbxServerCommand' -PassThru
            $refused.CategoryInfo.Category | Should -Be 'AuthenticationError'
            (Get-ZbxContext).Servers.ContainsKey('bad') | Should -BeFalse
        }

        It 'completes host names from the server' {
            $line = 'Get-ZbxHost -Name Zabb'
            $completion = TabExpansion2 -inputScript $line -cursorColumn $line.Length
            $completion.CompletionMatches.CompletionText | Should -Contain "'Zabbix server'"
        }
    }

    Context 'TLS on the wire' {
        BeforeAll {
            $tlsGroup = New-ZbxHostGroup -Name "$run-tls-group" -Confirm:$false -ErrorAction Stop
            $tlsHost = "$run-tls-host"
            $identity = "$run-psk"
            $hex = -join (1..32 | ForEach-Object { '{0:x2}' -f (Get-Random -Maximum 256) })
            $key = ConvertTo-SecureString $hex -AsPlainText -Force
            # The host takes values over its pre-shared key or a certificate the server's CA signed, never unencrypted.
            $tlsCreated = New-ZbxHost -Host $tlsHost -Group @($tlsGroup.GroupId) -TlsAccept Psk, Certificate -TlsPskIdentity $identity -TlsPsk $key -Confirm:$false -ErrorAction Stop
            Get-ZbxHost $tlsHost | New-ZbxItem -Name "$run-tls-float" -Key 'zbxtest.tls' -Type Trapper -ValueType Float -TrapperHosts '0.0.0.0/0,::/0' -Force -ErrorAction Stop | Out-Null
            $features = @(($env:ZABBIX_TEST_FEATURES ?? '') -split '[;\s]+' | Where-Object { $_ -like "$($server.Label)=*" } | ForEach-Object { ($_ -split '=', 2)[1] -split ',' })
        }

        AfterAll {
            Get-ZbxHost $tlsHost -ErrorAction SilentlyContinue | Remove-ZbxHost -Confirm:$false
            Get-ZbxHostGroup -Id $tlsGroup.GroupId -ErrorAction SilentlyContinue | Remove-ZbxHostGroup -Confirm:$false
        }

        It 'sends over the pre-shared key the connection was given, and plaintext is refused' {
            if (-not $server.ServerPort) { Set-ItResult -Skipped -Because "ZABBIX_TEST_TRAPPERS has no $($server.Label) trapper" }
            Connect-ZbxServer -Uri $server.Url -ApiToken $token -Name tls -Trapper "$($server.ServerHost):$($server.ServerPort)" -TrapperPskIdentity $identity -TrapperPsk $key -NoDefault -ErrorAction Stop

            # The trapper knows the host's key and item after its next configuration sync.
            $sent = Wait-Live { $r = Send-ZbxData -Connection tls -HostName $tlsHost -Key 'zbxtest.tls' -Value '1.5' -Confirm:$false -WarningAction SilentlyContinue -ErrorAction SilentlyContinue; if ($r.Processed -eq 1) { $r } } 'the trapper to take the host over its key'
            $plain = Send-ZbxData -Connection tls -Tls Unencrypted -HostName $tlsHost -Key 'zbxtest.tls' -Value '2.5' -Confirm:$false -WarningAction SilentlyContinue

            $sent.Processed | Should -Be 1
            $plain.Processed | Should -Be 0 -Because 'the host accepts no unencrypted value'
            Disconnect-ZbxServer -Name tls
        }

        It 'sends over the lab certificate with the server pinned' {
            if (-not $server.ServerPort) { Set-ItResult -Skipped -Because "ZABBIX_TEST_TRAPPERS has no $($server.Label) trapper" }
            if ($features -notcontains 'cert' -or -not $env:ZABBIX_TEST_PKI) { Set-ItResult -Skipped -Because "$($server.Label) was not started with the lab's certificates (ZABBIX_TEST_FEATURES, ZABBIX_TEST_PKI)" }
            $pki = $env:ZABBIX_TEST_PKI
            $tls = @{
                Certificate = Join-Path $pki 'client.crt'; CertificateKey = Join-Path $pki 'client.key'; CertificateAuthority = Join-Path $pki 'ca.crt'
                ServerSubject = 'CN=zbxlab-server,O=Zabbix lab'
            }

            $sent = Wait-Live { $r = Send-ZbxData -Server "$($server.ServerHost):$($server.ServerPort)" @tls -HostName $tlsHost -Key 'zbxtest.tls' -Value '3.5' -Confirm:$false -WarningAction SilentlyContinue -ErrorAction SilentlyContinue; if ($r.Processed -eq 1) { $r } } 'the trapper to take the host over the certificate'
            { Send-ZbxData -Server "$($server.ServerHost):$($server.ServerPort)" @tls -ServerSubject 'CN=someone else' -HostName $tlsHost -Key 'zbxtest.tls' -Value '4.5' -Confirm:$false -ErrorAction Stop } |
                Should -Throw

            $sent.Processed | Should -Be 1
        }
    }

    Context 'a passive agent the server polls' {
        BeforeAll {
            # The module Community.Zabbix.Agent; ZBX_AGENT_MODULE overrides its manifest (default: the Debug build).
            $agentManifest = $env:ZBX_AGENT_MODULE ?? (Join-Path $PSScriptRoot '../../src/Zabbix.PowerShell.Agent/bin/Debug/net10.0/Community.Zabbix.Agent.psd1')
            Import-Module $agentManifest -ErrorAction Stop
            $agentName = "$run-passive"
            $agentHost = "$run-passive-host"
            # How the server, inside its pod, reaches a listener on this machine (tests/lab/lab.sh env).
            $fromServer = if ($env:ZABBIX_TEST_HOST_FROM_SERVER) { $env:ZABBIX_TEST_HOST_FROM_SERVER } else { 'host.containers.internal' }
            # The pod's address as this machine sees it depends on the network mode: every peer is let in.
            $null = Start-ZbxPassiveAgent -Name $agentName -HostName $agentHost -AllowedPeer '0.0.0.0/0', '::/0' -Port 0 -Confirm:$false -ErrorAction Stop
            Register-ZbxPassiveAgentHandler -Name $agentName -Key 'zbxtest.passive' -ScriptBlock { '17' } -ErrorAction Stop
        }

        AfterAll {
            Clear-ZbxAgentHost -Session $agentName -Confirm:$false -ErrorAction SilentlyContinue
            Stop-ZbxPassiveAgent -Name $agentName -Confirm:$false -ErrorAction SilentlyContinue
            Get-ZbxHostGroup -Name "$run-agents" -ErrorAction SilentlyContinue | Remove-ZbxHostGroup -Confirm:$false
        }

        It 'provisions the host on the agent''s listener and reads back the value the server polled' {
            $status = Initialize-ZbxAgentHost -Session $agentName -HostGroup "$run-agents" -InterfaceAddress $fromServer -ValueType Unsigned -Confirm:$false -PassThru -ErrorAction Stop
            $item = Get-ZbxItem -Host $agentHost | Where-Object Key -EQ 'zbxtest.passive'
            # Polled every second, so the value follows the server's next configuration sync.
            $item | Set-ZbxItem -Delay '1s' -Confirm:$false -ErrorAction Stop

            $interface = Get-ZbxHostInterface -Host $agentHost
            $value = Wait-Live { $h = @(Get-ZbxHistory -Id $item.ItemId -ValueType Unsigned -Limit 1); if ($h.Count -gt 0) { $h[0] } } 'the server to poll zbxtest.passive' -Seconds 180

            ($status.Items | Where-Object Key -EQ 'zbxtest.passive').HasLocalHandler | Should -BeTrue
            $interface.Dns | Should -Be $fromServer
            $item.Type | Should -Be 'Zabbix'
            $item.ValueType | Should -Be 'Unsigned'
            # A built-in key keeps its own value type whatever -ValueType says: agent.version answers text.
            (Get-ZbxItem -Host $agentHost | Where-Object Key -EQ 'agent.version').ValueType | Should -Be 'Character'
            $item.InterfaceId | Should -Be $interface.InterfaceId
            $value.Value | Should -Be '17'
        }
    }

    Context 'pipelines, end to end' {
        BeforeAll {
            $group = New-ZbxHostGroup -Name "$run-group" -Confirm:$false -ErrorAction Stop
            $hostName = "$run-host"
            $created = New-ZbxHost -Host $hostName -Group @($group.GroupId) -Confirm:$false -ErrorAction Stop
        }

        It '-WhatIf creates nothing' {
            New-ZbxHostGroup -Name "$run-whatif" -WhatIf
            Get-ZbxHostGroup -Name "$run-whatif" | Should -BeNullOrEmpty
        }

        It 'builds an item on the host it is piped (builder to owner)' {
            $item = Get-ZbxHost $hostName | New-ZbxItem -Name "$run-float" -Key 'zbxtest.float' -Type Trapper -ValueType Float -TrapperHosts '0.0.0.0/0,::/0' -Force -ErrorAction Stop
            $item.HostId | Should -Be $created.HostId
            (Get-ZbxItem -Id $item.ItemId).Key | Should -Be 'zbxtest.float'
        }

        It 'sends a pscustomobject stream through Send-ZbxData and reads every clock back to the nanosecond' {
            if (-not $server.ServerPort) { Set-ItResult -Skipped -Because "ZABBIX_TEST_TRAPPERS has no $($server.Label) trapper" }
            $item = Get-ZbxItem -Filter ([Community.Zabbix.ZabbixItemGetParameters]@{ HostIds = @($created.HostId); Filter = @{ key_ = 'zbxtest.float' } })
            # The trapper learns of a new item at its next configuration sync: send a probe until it is taken. Until then the
            # server answers "processed: 0; failed: 1" for the unknown item, which Send-ZbxData reports as a warning: the
            # probe's expected refusals, silenced here.
            $second = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
            Wait-Live { $r = [pscustomobject]@{ HostName = $hostName; Key = 'zbxtest.float'; Value = '0'; Clock = $second - 3600; Ns = 0 } | Send-ZbxData -Confirm:$false -WarningAction SilentlyContinue; if ($r.Processed -eq 1) { $r } } 'the trapper to know zbxtest.float'
            $values = 1..3 | ForEach-Object { [pscustomobject]@{ HostName = $hostName; Key = 'zbxtest.float'; Value = "$($_ * 1.5)"; Clock = $second; Ns = $_ * 111111100 } }

            $response = $values | Send-ZbxData -Confirm:$false

            @($response).Count | Should -Be 1 -Because 'the stream is one batch (End), not a send per record'
            $response.Processed | Should -Be 3
            $read = [Community.Zabbix.ZabbixHistoryGetParameters]@{ History = 'Float'; ItemIds = @($item.ItemId); TimeFrom = [int]$second }
            $history = Wait-Live { $h = @(Get-ZbxHistory -Filter $read -SortField clock, ns -SortOrder Ascending); if ($h.Count -ge 3) { , $h } } 'three values in history.get'
            # A public time holds 100 ns ticks, so the ticks within the second are what -Ns set.
            @($history | ForEach-Object { '{0}.{1:D7}={2}' -f $_.Clock.ToUnixTimeSeconds(), ($_.Clock.UtcTicks % 10000000), $_.Value }) | Should -Be @(
                "$second.1111111=1.5", "$second.2222222=3", "$second.3333333=4.5")
        }

        It 'reads a piped Float item''s history from the Float table, and a bare id from the unsigned one' {
            if (-not $server.ServerPort) { Set-ItResult -Skipped -Because "ZABBIX_TEST_TRAPPERS has no $($server.Label) trapper" }
            # history.get defaults "history" to 3 (unsigned); a Float item's values are in type 0. A piped item's own
            # ValueType picks its table; a bare id reads the server's default table, as history.get itself does, so it
            # names the type with -ValueType.
            $item = Get-ZbxHost $hostName | New-ZbxItem -Name "$run-history" -Key 'zbxtest.history' -Type Trapper -ValueType Float -TrapperHosts '0.0.0.0/0,::/0' -Force -ErrorAction Stop
            # The trapper takes the item after its next configuration sync: resend until it is processed.
            Wait-Live { $r = Send-ZbxData -HostName $hostName -Key 'zbxtest.history' -Value '2.5' -Confirm:$false -WarningAction SilentlyContinue -ErrorAction SilentlyContinue; if ($r.Processed -eq 1) { $r } } 'the trapper to know zbxtest.history' | Out-Null
            $read = [Community.Zabbix.ZabbixHistoryGetParameters]@{ History = 'Float'; ItemIds = @($item.ItemId) }
            $typed = Wait-Live { $h = @(Get-ZbxHistory -Filter $read); if ($h.Count -gt 0) { , $h } } 'a value in history.get'

            @($item | Get-ZbxHistory).Count | Should -Be $typed.Count
            @(Get-ZbxHistory -Id $item.ItemId -ValueType Float).Count | Should -Be $typed.Count
            @(Get-ZbxHistory -Id $item.ItemId).Count | Should -Be 0 -Because 'history.get reads the unsigned table when no type is named'
        }

        It 'reads a page of exactly its limit' {
            $groups = 1..3 | ForEach-Object { New-ZbxHostGroup -Name ('{0}-page-{1}' -f $run, $_) -Confirm:$false }
            @(Get-ZbxHostGroup -Search @{ name = "$run-page-" } -Limit 3).Count | Should -Be 3
            @(Get-ZbxHostGroup -Search @{ name = "$run-page-" } -Limit 2).Count | Should -Be 2
            $groups | Remove-ZbxHostGroup -Confirm:$false
            Get-ZbxHostGroup -Search @{ name = "$run-page-" } | Should -BeNullOrEmpty
        }

        It 'stops a stream at Select-Object -First and the connection keeps working' {
            (Get-ZbxHost | Select-Object -First 1) | Should -BeOfType [Community.Zabbix.ZabbixHost]
            (Get-ZbxHost $hostName).HostId | Should -Be $created.HostId
        }

        It 'carries Get to Set through the pipeline' {
            Get-ZbxHost $hostName | Set-ZbxHost -Status Disabled -Confirm:$false -ErrorAction Stop
            (Get-ZbxHost $hostName).Status | Should -Be 'Disabled'
        }

        It 'reports the one bad record mid-pipeline and removes the rest' {
            $doomed = 1..2 | ForEach-Object { New-ZbxHostGroup -Name ('{0}-doomed-{1}' -f $run, $_) -Confirm:$false }
            $stream = @($doomed[0], [pscustomobject]@{ GroupId = '999999990' }, $doomed[1])

            $stream | Remove-ZbxHostGroup -Confirm:$false -ErrorVariable failures -ErrorAction SilentlyContinue

            Get-ZbxHostGroup -Search @{ name = "$run-doomed-" } | Should -BeNullOrEmpty
            $failures.Count | Should -BeGreaterThan 0
        }

        It 'propagates -WhatIf down a Get to Remove pipeline' {
            Get-ZbxHost $hostName | Remove-ZbxHost -WhatIf
            Get-ZbxHost $hostName | Should -Not -BeNullOrEmpty
        }

        It 'removes what it piped and leaves nothing under its prefix' {
            Get-ZbxHost $hostName | Remove-ZbxHost -Confirm:$false -ErrorAction Stop
            Get-ZbxHostGroup -Id $group.GroupId | Remove-ZbxHostGroup -Confirm:$false -ErrorAction Stop
            @(Invoke-LabLeakSweep -ServerId $server.Label -Prefix $run -ReportOnly) | Should -BeNullOrEmpty
        }
    }
}
