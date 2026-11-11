# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. Copyright (c) 2026 schroeder247. SPDX-License-Identifier: MPL-2.0
<#
.SYNOPSIS
    Runs every Get-Help example of the three modules against a live, throwaway Zabbix server: the help scripts
    (src/Zabbix.PowerShell*/Examples/<Verb-Noun>.ps1) and the examples the generator writes for the generated commands, so
    an example that stops working fails here and not in a reader's console.

.DESCRIPTION
    Each example file is an ordinary PowerShell script: the text before the first #region is setup (not shown in help;
    a comment-based help block there is the command's .SYNOPSIS and .DESCRIPTION), each #region ... #endregion is one
    Get-Help example (read by Zabbix.PowerShell.HelpGen's HelpScriptParser), and the text after the last #endregion is
    teardown. This script runs every file, whole, in its own runspace with
    $ErrorActionPreference = 'Stop', after:

      1. rewriting the example vocabulary to prefixed names (every name the examples create or change is in
         $Vocabulary below, or on a "# Vocabulary: name; name" line outside the file's regions; a name is rewritten
         wherever it follows a quote, a slash or an opening parenthesis — so 'web-01', "web-*" and
         last(/web-01/app.requests) all reach the prefixed fixture);
      2. rewriting the placeholder endpoints (https://zabbix.example/api_jsonrpc.php, zabbix.example:10051) to the
         server under test, and providing $token (a SecureString) from -TokenFile;
      3. connecting (Connect-ZbxServer) and creating the fixtures ($Fixtures below) under their prefixed names.

    Trapper values: 8.0's default trapper_hosts is {$TRAPPER.ALLOWED_HOSTS} (127.0.0.1,::1), and a container server
    sees the host's sender from its NAT address, so the fixture host sets that macro to 0.0.0.0/0,::/0 (and the
    fixture items name it explicitly). A new item is accepted only after the server's configuration cache has it
    (up to CacheUpdateFrequency, 60 s by default): a file that creates an item and then sends to it must wait on the
    effect (resend until Processed is 1, or read the value back), never sleep a fixed time.

    After each file every object whose name starts with -Prefix is deleted (JSON-RPC, by id, dependents first), so
    the next file starts from the fixtures again. Writes one result row per file. -Prefix carries a run id
    (zbx-doc-ex-<8 hex>-), so the fixtures and sweeps of two runs against one server never touch each other's
    objects; the run deletes only its own.

    The generated examples (the module build writes them to -GeneratedExamplePath; Zabbix.PowerShell.HelpGen's
    GeneratedExamples) run one at a time, each from the fixtures again, under the same rewriting, fixtures and
    sweep. Each first runs its setup: the example that creates the object it works on (the first example of the
    object's New- command, shown or replaced by a help script, for a Get-, Set- or Remove- example) and, before that,
    the creating example of every other object it names by that object's sample name (schema/zabbix-api.json,
    services[].help.sample), unless a fixture already has that name. The names the generated examples create are
    the objects' sample names, the names a Set- example renames an object to, and the first words of the sample
    names when an example searches by them ('Notify' or 'Notify*'). Read-Host answers a throwaway password, so an
    example that asks for one runs. A command the server's version does not have is reported as such, not as a
    failure, and an example the overlay marks with a reason (services[].help.notRun, keyed by the example's title or
    its command's verb) is reported with that reason and not run. An object whose examples need more of the lab than
    the objects they name (a host dashboard needs its template linked to the host) says so in services[].help.liveSetup,
    run as the last step of the setup. Writes one result row per example.

    The server MUST be a throwaway one: examples create, change and delete objects. Never point this at a production
    server; the lab's servers (tests/lab/lab.sh) are throwaway ones, and tests/lab/live.sh runs this against each of
    them as its examples suite.

.EXAMPLE
    pwsh -File tools/Test-ZbxHelpExamples.ps1 `
        -Uri http://localhost:18070/api_jsonrpc.php -TokenFile /tmp/zbxlab/token-18070 -Trapper 127.0.0.1:18051
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [uri]$Uri,

    [Parameter(Mandatory)]
    [string]$TokenFile,

    # The server's trapper, for the sender examples (never the lab's 10051).
    [Parameter(Mandatory)]
    [string]$Trapper,

    # Which example files to run (cmdlet names, wildcards allowed).
    [string[]]$Cmdlet = '*',

    # What every name this run creates starts with: the examples' prefix and a run id of its own, so two runs against
    # one server never meet (tests/lab/live.sh's sweep still deletes whatever any run left under zbx-doc-ex-).
    [ValidatePattern('^zbx-')]
    [string]$Prefix = 'zbx-doc-ex-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '-',

    # The build configuration whose module folders are imported (when -ModulePath is not given).
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    # The manifests imported into each example's runspace, Community.Zabbix first (Community.Zabbix.Agent and Community.Zabbix.Proxy require it).
    [string[]]$ModulePath = @(
        (Join-Path $PSScriptRoot "../src/Zabbix.PowerShell/bin/$Configuration/net10.0/Community.Zabbix.psd1")
        (Join-Path $PSScriptRoot "../src/Zabbix.PowerShell.Agent/bin/$Configuration/net10.0/Community.Zabbix.Agent.psd1")
        (Join-Path $PSScriptRoot "../src/Zabbix.PowerShell.Proxy/bin/$Configuration/net10.0/Community.Zabbix.Proxy.psd1")
    ),

    # The example folders of the three modules.
    [string[]]$ExamplePath = @('Zabbix.PowerShell', 'Zabbix.PowerShell.Agent', 'Zabbix.PowerShell.Proxy' | ForEach-Object { Join-Path $PSScriptRoot "../src/$_/Examples" }),

    # The generated commands' examples, as the module build writes them.
    [string]$GeneratedExamplePath = (Join-Path $PSScriptRoot "../src/Zabbix.PowerShell/obj/$Configuration/net10.0/examples/generated-examples.json"),

    # The API model: each object's help words name what its generated examples create, and which are not run.
    [string]$SchemaPath = (Join-Path $PSScriptRoot '../schema/zabbix-api.json'),

    # Which examples run: the help scripts, the generated examples, or both.
    [ValidateSet('Script', 'Generated')]
    [string[]]$Kind = @('Script', 'Generated'),

    # A file the result rows are also written to, as JSON (tests/lab/live.sh's examples suite reads it).
    [string]$ResultPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The names the examples may use, and nothing else that creates or changes an object. Longest first, so 'web-0'
# never shadows 'web-01'. A trailing * in an example ('web-*') reaches the fixtures through the 'web-' stem.
$Vocabulary = @(
    'Linux servers', 'Web servers', 'Databases', 'Templates/Web', 'Web App', 'Operators', 'Patch window',
    'Notify on disaster', 'proxy-01', 'proxy-group-01', 'jdoe', 'web-01', 'web-02', 'db-01', 'web-', 'db-',
    'Nightly export', 'Web checkout', 'Checkout', 'Local network', 'Ping', 'Office map', 'Ops overview',
    'Service status', 'Mail relay', 'Reboot host', 'Business hours SLA', 'Disaster correlation',
    'Too many requests on web-01'
) | Sort-Object Length -Descending

function ConvertTo-Runnable([string]$Text, [string[]]$Extra = @(), [string[]]$Stems = @()) {
    foreach ($word in @($Vocabulary + $Extra | Sort-Object Length -Descending)) {
        $Text = [regex]::Replace($Text, "(?<=['""/(])" + [regex]::Escape($word), { param($m) $Prefix + $m.Value })
    }
    # A stem only as a whole search word or before a wildcard: 'Server' must not reach the stock image 'Server_(96)'.
    foreach ($stem in $Stems) {
        $Text = [regex]::Replace($Text, "(?<=')" + [regex]::Escape($stem) + "(?=\*?')", { param($m) $Prefix + $m.Value })
    }
    $Text = $Text.Replace('https://zabbix.example/api_jsonrpc.php', $Uri.AbsoluteUri)
    $Text.Replace('zabbix.example:10051', $Trapper)
}

# The token file is read once: the examples get it as a SecureString, the sweep's own JSON-RPC as the header.
$plainToken = (Get-Content $TokenFile -Raw).Trim()
$token = ConvertTo-SecureString $plainToken -AsPlainText -Force
$headers = @{ Authorization = 'Bearer ' + $plainToken }
Remove-Variable plainToken

function Invoke-Rpc([string]$Method, $Params) {
    $body = @{ jsonrpc = '2.0'; method = $Method; params = $Params; id = 1 } | ConvertTo-Json -Depth 8 -Compress
    $r = Invoke-RestMethod -Uri $Uri -Method Post -ContentType 'application/json-rpc' -Headers $headers -Body $body
    if ($r.PSObject.Properties['error']) { throw "$Method : $($r.error.message) $($r.error.data)" }
    $r.result
}

# Dependents before what they hang off (a script limited to a host group blocks the group's delete): (type, id field,
# name field).
$SweepTypes = @(
    @('action', 'actionid', 'name'), @('correlation', 'correlationid', 'name'), @('service', 'serviceid', 'name'),
    @('sla', 'slaid', 'name'), @('maintenance', 'maintenanceid', 'name'), @('drule', 'druleid', 'name'),
    @('report', 'reportid', 'name'), @('dashboard', 'dashboardid', 'name'), @('map', 'sysmapid', 'name'),
    @('token', 'tokenid', 'name'), @('httptest', 'httptestid', 'name'), @('script', 'scriptid', 'name'),
    @('host', 'hostid', 'host'), @('template', 'templateid', 'host'),
    # What an example creates on a host or template and names from the vocabulary goes with that host or template;
    # these catch the ones on any other host. After host and template: an inherited item, trigger, graph or rule
    # cannot be deleted on its own, only with the template it comes from.
    @('trigger', 'triggerid', 'description'), @('graph', 'graphid', 'name'), @('discoveryrule', 'itemid', 'name'),
    @('item', 'itemid', 'name'), @('hostgroup', 'groupid', 'name'),
    @('templategroup', 'groupid', 'name'), @('user', 'userid', 'username'), @('usergroup', 'usrgrpid', 'name'),
    @('role', 'roleid', 'name'), @('mediatype', 'mediatypeid', 'name'), @('proxy', 'proxyid', 'name'),
    @('proxygroup', 'proxy_groupid', 'name'), @('valuemap', 'valuemapid', 'name'), @('regexp', 'regexpid', 'name'),
    @('connector', 'connectorid', 'name'), @('ceprule', 'cep_ruleid', 'name'), @('iconmap', 'iconmapid', 'name'),
    @('image', 'imageid', 'name'), @('userdirectory', 'userdirectoryid', 'name'), @('mfa', 'mfaid', 'name'),
    @('usermacro', 'globalmacroid', 'macro')
)

function Remove-Prefixed {
    foreach ($t in $SweepTypes) {
        ($type, $idField, $nameField) = $t
        $params = if ($type -eq 'usermacro') { @{ globalmacro = $true; output = @($idField, $nameField) } }
                  else { @{ output = @($idField, $nameField); search = @{ $nameField = $Prefix } } }
        # A type this version does not have answers "Incorrect API" (-32601, Method not found); anything else is a
        # failure of the sweep itself, and a sweep that leaves objects behind would fail the next file for no reason.
        try { $hits = @(Invoke-Rpc "$type.get" $params) }
        catch { if ([string]$_ -like '*Incorrect API*' -or [string]$_ -like '*Method not found*') { continue } else { throw } }
        $ids = @($hits | Where-Object { $_.$nameField -like "$Prefix*" -or $_.$nameField -like ('{$' + $Prefix.ToUpperInvariant().Replace('-', '_') + '*') } | ForEach-Object { $_.$idField })
        if ($ids.Count -eq 0) { continue }
        $method = if ($type -eq 'usermacro') { 'usermacro.deleteglobal' } else { "$type.delete" }
        $null = Invoke-Rpc $method $ids
    }
}

# The fixtures every example may assume, created under their prefixed names before each file (a script file of its
# own, so the module tests bind it statically).
$Fixtures = Get-Content -Raw -LiteralPath (Join-Path $PSScriptRoot 'Test-ZbxHelpExamples.Fixtures.ps1')

# The names the fixtures create: a generated example that names one needs no setup for it.
$FixtureNames = @(
    'Linux servers', 'Web servers', 'Templates/Web', 'Web App', 'web-01', 'db-01', 'Requests', 'Status',
    'Too many requests on web-01', '{$APP.PORT}', '{$TRAPPER.ALLOWED_HOSTS}', 'Operators', 'Virtual machines',
    'Database instances'
)

# A runspace with the modules imported and connected to the server under test, its examples' $ErrorActionPreference
# 'Stop'. With -AnswerReadHost, Read-Host answers a throwaway password (or text), as a reader would type one.
function New-ExampleRunspace([switch]$AnswerReadHost) {
    $ps = [powershell]::Create()
    $null = $ps.AddScript({
        param($ModulePath, $Uri, $Token, $AnswerReadHost)
        $ErrorActionPreference = 'Stop'
        Import-Module $ModulePath
        $null = Connect-ZbxServer -Uri $Uri -ApiToken $Token
        if ($AnswerReadHost) {
            function global:Read-Host([Parameter(Position = 0)][object]$Prompt, [switch]$AsSecureString, [switch]$MaskInput) {
                if ($AsSecureString) { ConvertTo-SecureString 'Ex4mple-only-Passw0rd!' -AsPlainText -Force } else { 'example' }
            }
        }
    }).AddArgument($ModulePath).AddArgument($Uri).AddArgument($token).AddArgument([bool]$AnswerReadHost)
    $null = $ps.Invoke(); if ($ps.HadErrors) { $failure = $ps.Streams.Error[0]; $ps.Dispose(); throw $failure }
    $ps.Commands.Clear()
    $ps
}

# Runs $Text in $Runspace and returns the first error record that reached the stream (null when none did), and the
# output count. HadErrors is also set by an error the text catches itself (try/catch, -ErrorAction SilentlyContinue)
# and leaves no record behind, so a failure is an error record that reached the stream.
function Invoke-ExampleText([powershell]$Runspace, [string]$Text) {
    $Runspace.Streams.ClearStreams()
    $null = $Runspace.AddScript("`$ErrorActionPreference = 'Stop'; `$token = `$args[0]`n" + $Text).AddArgument($token)
    try {
        $output = $Runspace.Invoke()
        $record = if ($Runspace.Streams.Error.Count -gt 0) { $Runspace.Streams.Error[0] } else { $null }
        [pscustomobject]@{ Error = $record; Output = @($output).Count }
    }
    catch {
        $record = if ($_.Exception.InnerException -is [System.Management.Automation.IContainsErrorRecord]) { $_.Exception.InnerException.ErrorRecord } else { $_ }
        [pscustomobject]@{ Error = $record; Output = 0 }
    }
    finally {
        $Runspace.Commands.Clear()
    }
}

# A method the server's version does not have: the SDK refuses it before sending (ZabbixVersionException, no Field).
function Test-NotInVersion($Record) {
    for ($e = $Record.Exception; $e; $e = $e.InnerException) {
        if ($e.GetType().FullName -eq 'Community.Zabbix.ZabbixVersionException' -and -not $e.Field) { return $true }
    }
    $false
}

$results = @()
if ($Kind -contains 'Script') {
$files = Get-ChildItem $ExamplePath -Filter *.ps1 | Where-Object { $n = $_.BaseName; @($Cmdlet | Where-Object { $n -like $_ }).Count -gt 0 } | Sort-Object Name
$results += foreach ($file in $files) {
    Remove-Prefixed
    $ps = $null
    try {
        $ps = New-ExampleRunspace
        $fixtureRun = Invoke-ExampleText $ps (ConvertTo-Runnable $Fixtures)
        if ($fixtureRun.Error) { throw "fixtures: $($fixtureRun.Error)" }
        $text = Get-Content $file.FullName -Raw
        $extra = @([regex]::Matches($text, '(?m)^#\s*Vocabulary:\s*(.+)$') | ForEach-Object { $_.Groups[1].Value -split ';' } | ForEach-Object Trim | Where-Object { $_ })
        $run = Invoke-ExampleText $ps (ConvertTo-Runnable $text $extra)
        $failure = if ($run.Error) { [string]$run.Error } else { $null }
        [pscustomobject]@{ Kind = 'Script'; Cmdlet = $file.BaseName; Example = ''; Status = if ($failure) { 'Failed' } else { 'Passed' }; Passed = -not $failure; Output = $run.Output; Error = $failure }
    }
    catch {
        [pscustomobject]@{ Kind = 'Script'; Cmdlet = $file.BaseName; Example = ''; Status = 'Failed'; Passed = $false; Output = 0; Error = [string]$_ }
    }
    finally {
        if ($ps) { $ps.Dispose() }
    }
}
}

if ($Kind -contains 'Generated') {
    if (-not (Test-Path $GeneratedExamplePath)) { throw "No generated examples at $GeneratedExamplePath`: build src/Zabbix.PowerShell ($Configuration) first." }
    $entries = @(Get-Content $GeneratedExamplePath -Raw | ConvertFrom-Json)
    # Each object's help words, by the command noun (the service's stem: ZabbixActionClient -> Action).
    $helpOf = @{}
    foreach ($service in (Get-Content $SchemaPath -Raw | ConvertFrom-Json).services) {
        if ($service.PSObject.Properties['help']) { $helpOf[$service.client -replace '^Zabbix|Client$', ''] = $service.help }
    }
    $sampleOf = { param($noun) $h = $helpOf[$noun]; if ($h -and $h.PSObject.Properties['sample']) { $h.sample } }
    # The first word of a sample name, as the generator's examples search by it (Notify on disaster -> Notify).
    $stemOf = { param($sample) $w = ($sample -split ' ')[0]; $cut = $w.IndexOfAny([char[]]'-./_'); if ($cut -gt 0) { $w = $w.Substring(0, $cut) }; if ($w.Length -ge 3 -and $w -match '^[\p{L}\p{Nd}]+$') { $w } }

    # What creates each object a generated example may name: the first example of its New- command, by sample name.
    $creators = [ordered]@{}
    foreach ($entry in $entries | Where-Object Cmdlet -Like 'New-Zbx*') {
        $sample = & $sampleOf $entry.Cmdlet.Substring(7)
        if ($sample -and -not $creators.Contains($sample) -and $entry.Code.Contains("'$sample'") -and $FixtureNames -notcontains $sample) {
            $creators[$sample] = $entry
        }
    }

    # The setup of an example: the creating example of each object its code names, after that example's own setup.
    function Get-ExampleSetup([string]$Code, [string]$Own, [System.Collections.Generic.List[string]]$Steps, [System.Collections.Generic.HashSet[string]]$Seen) {
        foreach ($literal in [regex]::Matches($Code, "'([^']+)'") | ForEach-Object { $_.Groups[1].Value }) {
            foreach ($sample in @($creators.Keys)) {
                $named = $literal -eq $sample -or ($literal.EndsWith('*') -and $literal.Length -gt 1 -and $sample.StartsWith($literal.TrimEnd('*'), [StringComparison]::Ordinal))
                if (-not $named -or $sample -eq $Own -or -not $Seen.Add($sample)) { continue }
                $creator = $creators[$sample]
                Get-ExampleSetup $creator.Code $sample $Steps $Seen
                $Steps.Add($creator.Code)
            }
        }
    }

    $samples = @($creators.Keys)
    $stems = @($samples | ForEach-Object { & $stemOf $_ } | Where-Object { $_ } | Sort-Object -Unique | Sort-Object Length -Descending)
    $ps = New-ExampleRunspace -AnswerReadHost
    try {
        $results += foreach ($entry in $entries | Where-Object Shown) {
            $command = $entry.Cmdlet
            if (@($Cmdlet | Where-Object { $command -like $_ }).Count -eq 0) { continue }
            ($verb, $noun) = $command -split '-Zbx', 2
            $row = [ordered]@{ Kind = 'Generated'; Cmdlet = $command; Example = $entry.Title; Status = 'Passed'; Passed = $true; Output = 0; Error = $null }
            # The overlay's reason not to run an example, by its title or by its command's verb.
            $help = $helpOf[$noun]
            $notRun = if ($help -and $help.PSObject.Properties['notRun']) {
                $help.notRun.PSObject.Properties | Where-Object { $_.Name -eq $entry.Title -or $_.Name -eq $verb } | Select-Object -First 1 -ExpandProperty Value
            }
            if ($notRun) {
                $row.Status = 'Excluded'; $row.Error = $notRun
                [pscustomobject]$row; continue
            }
            $own = & $sampleOf $noun
            $steps = [System.Collections.Generic.List[string]]::new()
            $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            if ($verb -ne 'New' -and $own -and $creators.Contains($own)) {
                $null = $seen.Add($own)
                Get-ExampleSetup $creators[$own].Code $own $steps $seen
                $steps.Add($creators[$own].Code)
            }
            Get-ExampleSetup $entry.Code ($verb -eq 'New' ? $own : $null) $steps $seen
            # What the lab needs before the object's examples run there, after the objects it names exist.
            if ($help -and $help.PSObject.Properties['liveSetup']) { $steps.Add($help.liveSetup) }
            # Every name the example and its setup create is prefixed, the names a Set- example renames to included.
            $all = @($steps) + $entry.Code
            $renames = @($all | ForEach-Object { [regex]::Matches($_, "Set-Zbx\w+[^|\n]*?-Name '([^']+)'") } | ForEach-Object { $_.Groups[1].Value })
            $extra = @($samples + $renames | Sort-Object -Unique)
            Remove-Prefixed
            $fixtureRun = Invoke-ExampleText $ps (ConvertTo-Runnable $Fixtures)
            if ($fixtureRun.Error) { $row.Status = 'Failed'; $row.Passed = $false; $row.Error = "fixtures: $($fixtureRun.Error)"; [pscustomobject]$row; continue }
            $setup = if ($steps.Count) { Invoke-ExampleText $ps (ConvertTo-Runnable ($steps -join "`n") $extra $stems) }
            $run = if (-not ($setup -and $setup.Error)) { Invoke-ExampleText $ps (ConvertTo-Runnable $entry.Code $extra $stems) }
            $failure = if ($setup -and $setup.Error) { $setup.Error; $row.Error = "setup: $($setup.Error)" } elseif ($run.Error) { $run.Error; $row.Error = [string]$run.Error }
            if ($failure -and (Test-NotInVersion $failure)) { $row.Status = 'NotInVersion' }
            elseif ($failure) { $row.Status = 'Failed'; $row.Passed = $false }
            else { $row.Output = $run.Output }
            [pscustomobject]$row
        }
    }
    finally {
        $ps.Dispose()
    }
}
Remove-Prefixed
if ($ResultPath) { ConvertTo-Json -InputObject @($results) -Depth 3 | Set-Content -Path $ResultPath }
# Rendered before exit: a table still waiting on the formatter's delay is lost when the script exits.
$results | Format-Table -AutoSize -Wrap | Out-String -Width 200
$results | Group-Object Kind, Status | ForEach-Object { '{0,-24} {1,4}' -f $_.Name, $_.Count } | Out-String
if (@($results | Where-Object { -not $_.Passed }).Count -gt 0) { exit 1 }
