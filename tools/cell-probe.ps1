<#
.SYNOPSIS
Asserts ck.cell names every cell it reports, instead of only handing back a pointer.

.DESCRIPTION
A load is confirmed by what the render window holds, and a pointer alone cannot confirm it: 0x1EF329894B0
says nothing about which cell is open. The failure this probe is written from is a reply that goes back to
that shape - `resolved:true` with a cell address and no `editorId`/`formId` - which would still read as a
successful load.

For each cell it picks from the Cell View it:

  1. opens the cell through the plugin's own `ui.cellload`, the way a double-click does;
  2. waits for the render window to report that cell;
  3. asks `ck.cell` over MCP and checks the answer names the cell: `editorId` equal to the row it opened,
     `formId` equal to the row's form id, both keys actually present;
  4. checks the placement keys are there too (`interior`, `gridX`, `gridY`, `hasWorldSpace`), that an
     interior cell reports grid 0, 0, and that an exterior one reports a worldspace;
  5. checks the pointer moved between two different cells, so the identity is not a stale read.

The assertions are then run against three fabricated replies - pointer-only, a wrong EditorID, and a
missing form id - and each must be rejected. That is what proves the check bites: a regression to
pointer-only reporting fails this probe rather than passing it with fewer fields.

Cells come from the Cell View rather than a hard-coded name, because which list the editor shows depends on
its state (324 interior cells, or the 36 865 Commonwealth cells when a worldspace is selected).

Exit codes: 0 pass, 1 assertion failed, 2 no cell could be loaded (editor not ready for this list),
3 the plugin pipe is unreachable.

.PARAMETER Dll
Path to the built MCP server assembly, used for the `ck.cell` step.

.PARAMETER Game
Game directory handed to the MCP server.

.PARAMETER Count
How many cells to open and check. Each one is loaded and confirmed, so two is enough to prove the pointer
moves and the identity follows it.

.PARAMETER Evidence
Optional path for the JSON evidence file (cells observed, fabricated replies, failures).

.EXAMPLE
# from the CKAgent directory, with the server built and the editor running
./tools/cell-probe.ps1 -Evidence cell-probe.json
#>

param(
    [string]$Dll = 'CKAgent.Mcp/bin/Release/net8.0/ckagent.dll',
    [string]$Game = 'D:\SteamLibrary\steamapps\common\Fallout 4',
    [int]$Count = 2,
    [string]$Evidence = ''
)

$ErrorActionPreference = 'Stop'

# Add-Type needs a real TEMP; this host can have TEMP set to the literal :TEMP.
$scratch = Join-Path $PSScriptRoot '.probe-tmp'
New-Item -ItemType Directory -Force $scratch | Out-Null
$env:TEMP = $scratch
$env:TMP = $scratch

$failures = New-Object System.Collections.Generic.List[string]
$observed = [ordered]@{ cells = @(); fabricated = @() }

function Invoke-Plugin([string]$command) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $(if ($env:CKAGENT_LOW_PIPE -eq '1') { 'ckagent-low' } else { 'ckagent' }), [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(5000)
    } catch {
        throw "the plugin pipe is unreachable: $($_.Exception.Message)"
    }

    $reader = New-Object System.IO.StreamReader($pipe)
    $writer = New-Object System.IO.StreamWriter($pipe)
    $writer.AutoFlush = $true
    $writer.NewLine = "`n"

    if (-not $reader.ReadLine()) { throw 'the plugin did not announce itself on the pipe' }
    $writer.WriteLine("CALL 1 $command")
    $reply = $reader.ReadLine()
    $pipe.Dispose()

    if (-not $reply -or $reply -notmatch '^RPLY \d+ ') { throw "unexpected reply to '$command': $reply" }
    if ($reply -match '^RPLY \d+ ERR (\S+) (.*)$') { throw "the plugin refused '$command': $($Matches[1]) $($Matches[2])" }
    return ($reply -replace '^RPLY \d+ OK ', '')
}

function Invoke-Mcp([string]$tool, [string]$json) {
    # The server logs to stderr, and PowerShell 5.1 turns a native child's stderr into an error record even
    # when it is redirected, so both streams go to files and only the JSON line comes back.
    $outFile = Join-Path $scratch 'mcp-stdout.txt'
    $errFile = Join-Path $scratch 'mcp-stderr.txt'
    # The arguments travel in a file: PowerShell drops the quotes out of JSON handed to a native command.
    $argsFile = Join-Path $scratch 'mcp-args.json'
    Set-Content -LiteralPath $argsFile -Value $json -Encoding utf8

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & node (Join-Path $PSScriptRoot 'mcp-call.mjs') $Dll $tool $argsFile $Game > $outFile 2> $errFile
    $ErrorActionPreference = $previous

    $lines = @()
    if (Test-Path -LiteralPath $outFile) { $lines = @(Get-Content -LiteralPath $outFile) }
    $text = (@($lines) | Where-Object { "$_".TrimStart().StartsWith('{') } | Select-Object -Last 1)
    $text = "$text".Trim()
    try {
        return $text | ConvertFrom-Json
    } catch {
        return [pscustomobject]@{ status = 'unreadable'; code = 'unreadable'; message = $text }
    }
}

function Get-PluginCell {
    return (Invoke-Plugin 'cell') | ConvertFrom-Json
}

# The same rule the regression breaks: a reply must carry the identity, not only an address.
function Test-CellReply([object]$reply, [string]$expectedId, [string]$expectedForm, [string]$label) {
    $problems = New-Object System.Collections.Generic.List[string]

    if ("$($reply.status)" -ne 'ok') { $problems.Add("$label`: the reply is $($reply.status)/$($reply.code), not ok") }

    $payload = $reply.payload
    if ($payload -is [string]) {
        try { $payload = $payload | ConvertFrom-Json } catch { $problems.Add("$label`: the payload is not JSON") }
    }
    if ($null -eq $payload) { return @($problems + "$label`: no payload") }

    if ($payload.resolved -ne $true) { $problems.Add("$label`: resolved is '$($payload.resolved)', so the cell was never read") }
    if ($payload.readable -ne $true) { $problems.Add("$label`: readable is '$($payload.readable)'") }

    # A regression to pointer-only reporting drops these keys, and PowerShell then yields $null for them.
    if (-not $payload.PSObject.Properties['editorId']) { $problems.Add("$label`: the reply has no editorId key, so it names no cell") }
    if (-not $payload.PSObject.Properties['formId']) { $problems.Add("$label`: the reply has no formId key") }

    $editorId = "$($payload.editorId)"
    $formId = "$($payload.formId)"
    if ($editorId -eq '') { $problems.Add("$label`: editorId is empty, so the reply names no cell") }
    if ($formId -notmatch '^[0-9A-Fa-f]{8}$') { $problems.Add("$label`: formId '$formId' is not an 8-digit form id") }

    if ($expectedId -and $editorId -ne $expectedId) {
        $problems.Add("$label`: the reply names '$editorId', not the cell that was opened ('$expectedId')")
    }
    if ($expectedForm -and $formId -ne $expectedForm) {
        $problems.Add("$label`: the reply reports form id '$formId', not the row's $expectedForm")
    }

    foreach ($key in 'interior', 'gridX', 'gridY', 'hasWorldSpace') {
        if (-not $payload.PSObject.Properties[$key]) { $problems.Add("$label`: the reply has no $key") }
    }

    if ($payload.interior -eq $true) {
        if ([int]$payload.gridX -ne 0 -or [int]$payload.gridY -ne 0) {
            $problems.Add("$label`: an interior cell reports grid $($payload.gridX),$($payload.gridY) instead of 0,0")
        }
    } elseif ($payload.PSObject.Properties['interior']) {
        if ($payload.hasWorldSpace -ne $true) { $problems.Add("$label`: an exterior cell reports no worldspace") }
    }

    return @($problems)
}

# 1. The cells to check come from the editor's own list, so the probe works on whichever list it shows.
$listing = (Invoke-Plugin 'ui.cells') | ConvertFrom-Json
$rows = @($listing.rows | Where-Object { "$($_.editorId)" -ne '' } | Select-Object -First $Count)
if ($rows.Count -eq 0) {
    Write-Output 'FAIL the Cell View lists no named cell, so nothing can be loaded'
    exit 2
}

$pointers = @()

foreach ($row in $rows) {
    $requested = "$($row.editorId)"
    $expectedForm = "$($row.formId)"
    $record = [ordered]@{ requested = $requested; formId = $expectedForm }

    try {
        Invoke-Plugin "ui.cellload $requested" | Out-Null
    } catch {
        $failures.Add("$requested`: the Cell View refused the load: $($_.Exception.Message)")
        continue
    }

    $deadline = (Get-Date).AddSeconds(25)
    $loaded = Get-PluginCell
    while ((Get-Date) -lt $deadline -and "$($loaded.editorId)" -ne $requested) {
        Start-Sleep -Milliseconds 400
        $loaded = Get-PluginCell
    }

    $record['pluginEditorId'] = "$($loaded.editorId)"
    $record['pointer'] = "$($loaded.cell)"
    if ("$($loaded.editorId)" -ne $requested) {
        $failures.Add("$requested`: the render window reports '$($loaded.editorId)' after 25 s, so the load itself failed")
        $observed.cells += $record
        continue
    }
    $pointers += "$($loaded.cell)"

    $reply = Invoke-Mcp 'ck.cell' '{}'
    $record['reply'] = $reply.payload

    foreach ($problem in (Test-CellReply $reply $requested $expectedForm $requested)) {
        $failures.Add($problem)
    }

    $observed.cells += $record
}

# 2. Two different cells must not share a pointer: a stale read would name the previous cell with a new name.
if ($pointers.Count -ge 2 -and ($pointers | Select-Object -Unique).Count -lt 2) {
    $failures.Add("both loads report the same cell pointer ($($pointers[0])), so the identity was read from a stale window")
}

# 3. The assertions must reject the replies a regression would produce, or this probe proves nothing.
# Each case carries the expectation a real check would hold, so it can only be rejected for its own fault.
$fabricated = @(
    @{ why = 'pointer-only'; expect = 'AndrewStation01'; form = '00074D28'; payload = '{"resolved":true,"window":"0x10","cell":"0x20","loaded":true,"readable":true}' },
    @{ why = 'wrong editorId'; expect = 'AndrewStation01'; form = '00074D28'; payload = '{"resolved":true,"cell":"0x20","loaded":true,"readable":true,"editorId":"SomeOtherCell","formId":"00074D28","interior":true,"gridX":0,"gridY":0,"hasWorldSpace":false}' },
    @{ why = 'no form id'; expect = 'AndrewStation01'; form = '00074D28'; payload = '{"resolved":true,"cell":"0x20","loaded":true,"readable":true,"editorId":"AndrewStation01","interior":true,"gridX":0,"gridY":0,"hasWorldSpace":false}' }
)

foreach ($case in $fabricated) {
    $fake = [pscustomobject]@{ status = 'ok'; code = 'ok'; message = 'cell'; payload = $case.payload }
    $problems = @(Test-CellReply $fake $case.expect $case.form 'fabricated')
    $observed.fabricated += [ordered]@{ why = $case.why; rejected = ($problems.Count -gt 0); problems = $problems }

    if ($problems.Count -eq 0) {
        $failures.Add("the assertion accepted a fabricated $($case.why) reply, so a regression would pass this probe")
    }
}

$evidenceJson = [ordered]@{ cell = $observed; failures = @($failures); passed = ($failures.Count -eq 0) } | ConvertTo-Json -Depth 6
if ($Evidence) { $evidenceJson | Set-Content -LiteralPath $Evidence -Encoding utf8 }

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Output "FAIL $failure" }
    exit 1
}

$named = @($observed.cells | ForEach-Object { "$($_.requested) ($($_.formId))" }) -join ', '
Write-Output "PASS ck.cell named every loaded cell with its form id, interior flag and grid: $named"
Write-Output $evidenceJson
exit 0
