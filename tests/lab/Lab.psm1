# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
#
# The lab from PowerShell: which throwaway servers the live matrix's environment contract names (ZABBIX_TEST_APIS,
# ZABBIX_TEST_TRAPPERS, printed by lab.sh env), each one's API token, and a leak sweep over raw JSON-RPC that finds and
# deletes every object whose name starts with a prefix a live test owns. The Pester live suite and live.sh use it.

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-LabServer {
    <#
    .SYNOPSIS
        The lab's throwaway servers, from the live matrix's environment contract.
    .DESCRIPTION
        ZABBIX_TEST_APIS ('7.0=http://localhost:18070/api_jsonrpc.php|@/tmp/zbxlab/token-18070;7.4=…;8.0=…', printed
        by tests/lab/lab.sh env) and ZABBIX_TEST_TRAPPERS ('7.0=127.0.0.1:18051;…') name them; a token written
        '@file' is read from that file. Unset, the lab's three servers are assumed (18070/18074/18080, tokens in
        /tmp/zbxlab). Each row's Id is the API port and its Label the version (7.0, 7.4, 8.0); either selects it.
    #>
    [CmdletBinding()]
    param()

    $defaults = @(
        @{ Label = '7.0'; Port = 18070; Trapper = 18051 }
        @{ Label = '7.4'; Port = 18074; Trapper = 18053 }
        @{ Label = '8.0'; Port = 18080; Trapper = 18052 }
    )
    $trappers = @{}
    foreach ($entry in (($env:ZABBIX_TEST_TRAPPERS ?? '') -split ';' | Where-Object { $_ -match '=' })) {
        $label, $node = $entry.Trim() -split '=', 2
        $trappers[$label] = $node
    }
    $entries = @(($env:ZABBIX_TEST_APIS ?? '') -split ';' | Where-Object { $_.Trim() })
    if (-not $entries) {
        $entries = @($defaults | ForEach-Object { "$($_.Label)=http://localhost:$($_.Port)/api_jsonrpc.php|@/tmp/zbxlab/token-$($_.Port)" })
        foreach ($d in $defaults) { if (-not $trappers[$d.Label]) { $trappers[$d.Label] = "127.0.0.1:$($d.Trapper)" } }
    }
    foreach ($entry in $entries) {
        $entry = $entry.Trim()
        $label = $null
        if ($entry -match '^([^=:/]+)=(.*)$') { $label = $Matches[1]; $entry = $Matches[2] }
        $url, $token = $entry -split '\|', 2
        if ($url -notmatch '\.php$') { $url = $url.TrimEnd('/') + '/api_jsonrpc.php' }
        $port = ([uri]$url).Port
        if (-not $label) { $label = @{ 18070 = '7.0'; 18074 = '7.4'; 18080 = '8.0' }[$port] ?? "$port" }
        $tokenFile = if ($token -like '@*') { $token.Substring(1) } else { $null }
        $node = $trappers[$label]
        $trapperHost, $trapperPort = if ($node) { $node -split ':', 2 } else { '127.0.0.1', $null }
        [PSCustomObject]@{
            Id         = "$port"
            Label      = $label
            Url        = $url
            TokenFile  = $tokenFile
            Token      = if ($tokenFile) { $null } else { $token }
            ServerHost = $trapperHost
            ServerPort = if ($trapperPort) { [int]$trapperPort } else { $null }
        }
    }
}

function Resolve-LabServer {
    <#
    .SYNOPSIS
        One server of Get-LabServer, by Id (the API port) or Label (the version); throws naming the choices.
    #>
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$ServerId)

    $all = @(Get-LabServer)
    $server = $all | Where-Object { $_.Id -eq $ServerId -or $_.Label -eq $ServerId } | Select-Object -First 1
    if (-not $server) { throw "Unknown lab server '$ServerId'; configured: $(($all | ForEach-Object { "$($_.Label) ($($_.Id))" }) -join ', ')." }
    $server
}

function Get-LabServerToken {
    <#
    .SYNOPSIS
        The server's API token, from its file or the contract; throws naming the remedy when there is none.
    #>
    [CmdletBinding()]
    [OutputType([string])]
    param([Parameter(Mandatory)][object]$Server)

    if ($Server.Token) { return $Server.Token }
    if (-not $Server.TokenFile -or -not (Test-Path $Server.TokenFile)) {
        throw "No API token for $($Server.Label) ($($Server.Url)): '$($Server.TokenFile)' is missing. Run tests/lab/lab.sh start."
    }
    (Get-Content $Server.TokenFile -Raw).Trim()
}

function Invoke-LabLeakSweep {
    <#
    .SYNOPSIS
        Module-independent leak scan (and, unless -ReportOnly, delete) of every object on a
        throwaway server whose name starts with -Prefix.
    .DESCRIPTION
        A live test removes what it created through the module under test; this sweep is the check behind it. It
        talks JSON-RPC directly (Invoke-RestMethod, bearer token), so it works however broken the module under test
        is. For each type: `<type>.get` with `search: {<name field>: <prefix>}` and NO searchWildcardsEnabled; each
        hit's name is re-checked for the prefix (a user macro as '{$PREFIX...'); survivors are deleted by id with
        `<type>.delete`, dependents first. A method the server version does not have is skipped. Returns one row per
        object found.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ServerId,

        [switch]$ReportOnly,

        # The object-name prefix swept, never empty or blank: every hit is re-checked for it before anything is
        # deleted, so a prefix only ever deletes what starts with it.
        [Parameter(Mandatory)]
        [ValidateNotNullOrEmpty()]
        [ValidateScript({ $_.Trim().Length -ge 3 }, ErrorMessage = 'A sweep prefix must be at least three non-blank characters: it decides what is deleted.')]
        [string]$Prefix
    )

    # Parenthesized: `'a' + $x, 'b' + $y` parses as `'a' + ($x, 'b' + $y)`, one string, and [1] then throws under
    # StrictMode as soon as a hit does not start with $Prefix (LLD-discovered `Proxy [<prefix>_…]` items do).
    $macroPrefixes = @(('{$' + $Prefix.ToUpperInvariant()), ('{$' + $Prefix.ToUpperInvariant().Replace('-', '_')))
    $server = Resolve-LabServer -ServerId $ServerId
    $token = Get-LabServerToken -Server $server
    $headers = @{ Authorization = "Bearer $token" }

    function Invoke-Rpc([string]$Method, $Params) {
        $body = @{ jsonrpc = '2.0'; method = $Method; params = $Params; id = 1 } | ConvertTo-Json -Depth 6 -Compress
        $r = Invoke-RestMethod -Uri $server.Url -Method Post -ContentType 'application/json-rpc' -Header $headers -Body $body
        if ($r.PSObject.Properties['error']) { throw "$Method : $($r.error.message) $($r.error.data)" }
        $r.result
    }

    # Dependents before the objects they hang off. (type, id field, name field)
    $types = @(
        @('action', 'actionid', 'name'), @('correlation', 'correlationid', 'name'),
        @('service', 'serviceid', 'name'), @('sla', 'slaid', 'name'),
        @('maintenance', 'maintenanceid', 'name'), @('drule', 'druleid', 'name'),
        @('report', 'reportid', 'name'), @('dashboard', 'dashboardid', 'name'),
        @('map', 'sysmapid', 'name'), @('token', 'tokenid', 'name'),
        @('httptest', 'httptestid', 'name'), @('trigger', 'triggerid', 'description'),
        @('graph', 'graphid', 'name'), @('item', 'itemid', 'name'),
        @('valuemap', 'valuemapid', 'name'),
        @('host', 'hostid', 'host'), @('template', 'templateid', 'host'),
        @('hostgroup', 'groupid', 'name'), @('templategroup', 'groupid', 'name'),
        @('user', 'userid', 'username'), @('usergroup', 'usrgrpid', 'name'),
        @('role', 'roleid', 'name'), @('mediatype', 'mediatypeid', 'name'),
        @('script', 'scriptid', 'name'), @('proxy', 'proxyid', 'name'),
        @('proxygroup', 'proxy_groupid', 'name'), @('connector', 'connectorid', 'name'),
        @('iconmap', 'iconmapid', 'name'), @('image', 'imageid', 'name'),
        @('regexp', 'regexpid', 'name'), @('userdirectory', 'userdirectoryid', 'name'),
        @('mfa', 'mfaid', 'name'), @('usermacro', 'globalmacroid', 'macro')
    )

    foreach ($t in $types) {
        $type, $idField, $nameField = $t
        $getParams = @{ output = @($idField, $nameField); search = @{ $nameField = $Prefix } }
        if ($type -eq 'usermacro') { $getParams.globalmacro = $true }
        try { $hits = @(Invoke-Rpc "$type.get" $getParams) }
        catch {
            if ($_.Exception.Message -match 'does not exist|not found|Incorrect method') { continue }
            Write-Warning "leak scan $type.get on $ServerId : $($_.Exception.Message)"; continue
        }
        $mine = @($hits | Where-Object {
                $n = [string]$_.$nameField
                $n.StartsWith($Prefix, [StringComparison]::OrdinalIgnoreCase) -or
                $n.StartsWith($macroPrefixes[0], [StringComparison]::OrdinalIgnoreCase) -or
                $n.StartsWith($macroPrefixes[1], [StringComparison]::OrdinalIgnoreCase)
            })
        foreach ($o in $mine) {
            [PSCustomObject]@{ Server = $ServerId; Type = $type; Id = [string]$o.$idField; Name = [string]$o.$nameField }
        }
        if ($ReportOnly -or $mine.Count -eq 0) { continue }
        $method = if ($type -eq 'usermacro') { 'usermacro.deleteglobal' } else { "$type.delete" }
        foreach ($o in $mine) {
            # One id per call: a dependent deleted with its parent must not fail the batch.
            try { $null = Invoke-Rpc $method @([string]$o.$idField) }
            catch { Write-Verbose "leak delete $type $($o.$idField): $($_.Exception.Message)" }
        }
    }
}

Export-ModuleMember -Function @(
    'Get-LabServer'
    'Resolve-LabServer'
    'Get-LabServerToken'
    'Invoke-LabLeakSweep'
)
