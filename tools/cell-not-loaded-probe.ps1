<#
.SYNOPSIS
Forces a cell load the editor ignores, so the cell_not_loaded path is measured instead of reviewed.

.DESCRIPTION
`ck.cell_load` reports `cell_not_loaded` when the Cell View accepted the name and the render window still holds
the previous cell fifteen seconds later. That branch is the one that catches a wrong-cell load - the plugin
double-clicks a row, the editor does nothing, and a tool that only trusted its own click would call it a
success. It is easy to write and easy to break, so this probe makes the editor ignore a load on purpose.

The plugin matches the name against every list and tree in the editor, not only the Cell View. Driving it with a
name that matches an item in another pane - the Object Window's record tree - finds a row, notifies its parent
of the double-click, and the render window keeps its cell: exactly the shape the branch exists for. The name is
taken from another pane at run time and checked against the Cell View's own items first, so it cannot
accidentally match a cell there.

It then asserts:

  1. the reply is `cell_not_loaded`, not `ok` and not `no_cell`;
  2. the message names both sides - the name it was asked for and the cell that is still held;
  3. the payload carries `requested` and `opened.editorId`, so a caller can see which of the two it ended on,
     and `opened.editorId` equals the cell that was open before the attempt;
  4. a fresh `ck.cell` still holds that same cell, which is what "ignored" means;
  5. the control case bites the other way: loading the EditorID of the cell already open answers `ok`, so the
     probe cannot pass by a tool that reports `cell_not_loaded` for everything.

Exit codes: 0 pass, 1 assertion failed, 2 setup (no cell is open, or another pane offered no usable name).

.PARAMETER Dll
Path to the built MCP server assembly.

.PARAMETER Game
Game directory handed to the MCP server.

.PARAMETER Evidence
Optional path for the JSON evidence file (the panes, the ignored name, and both replies).

.EXAMPLE
# from the CKAgent directory, with the server built, the editor running and a cell open
./tools/cell-not-loaded-probe.ps1 -Evidence cell-not-loaded.json
#>

param(
    [string]$Dll = 'CKAgent.Mcp/bin/Release/net8.0/ckagent.dll',
    [string]$Game = 'D:\SteamLibrary\steamapps\common\Fallout 4',
    [string]$Evidence = '',
    [string]$Cell = ''
)

$ErrorActionPreference = 'Stop'

$scratch = Join-Path $PSScriptRoot '.probe-tmp'
New-Item -ItemType Directory -Force $scratch | Out-Null
$env:TEMP = $scratch
$env:TMP = $scratch

$failures = New-Object System.Collections.Generic.List[string]
$observed = [ordered]@{}

function Invoke-Mcp([string]$tool, [string]$json) {
    # Both streams go to files: PowerShell 5.1 turns a native child's stderr into an error record.
    $outFile = Join-Path $scratch 'mcp-stdout.txt'
    $errFile = Join-Path $scratch 'mcp-stderr.txt'
    $argsFile = Join-Path $scratch 'mcp-args.json'
    Set-Content -LiteralPath $argsFile -Value $json -Encoding utf8

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & node (Join-Path $PSScriptRoot 'mcp-call.mjs') $Dll $tool $argsFile $Game 60000 > $outFile 2> $errFile
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

# A tree pane reports its nodes as objects, a list pane as strings; both have to yield plain names.
function Get-ItemTexts([object]$items) {
    $texts = New-Object System.Collections.Generic.List[string]
    foreach ($item in @($items)) {
        if ($item -is [string]) { $texts.Add($item); continue }
        if ($item -is [System.ValueType]) { $texts.Add("$item"); continue }
        $names = @($item.PSObject.Properties.Name)
        if ($names -contains 'text' -and "$($item.text)") { $texts.Add("$($item.text)") }
        if ($names -contains 'nodes') {
            foreach ($node in @($item.nodes)) {
                if ($node -is [string]) { $texts.Add($node) }
                elseif ($node -and @($node.PSObject.Properties.Name) -contains 'text') { $texts.Add("$($node.text)") }
                elseif ($node) { $texts.Add("$node") }
            }
        }
    }
    return $texts
}

function Get-Payload([object]$reply) {
    $payload = $reply.payload
    if ($payload -is [string]) {
        try { $payload = $payload | ConvertFrom-Json } catch { return $null }
    }
    return $payload
}

function Get-HeldCell {
    $reply = Invoke-Mcp 'ck.cell' '{}'
    $payload = Get-Payload $reply
    if ("$($reply.status)" -ne 'ok' -or -not $payload.readable) { return $null }
    $id = "$($payload.editorId)"
    if (-not $id) { return $null }
    return [pscustomobject]@{ editorId = $id; formId = "$($payload.formId)" }
}

$candidates = Invoke-Mcp 'ck.ui' '{ "method": "ui.cellload", "args": "" }'
$candidatesPayload = Get-Payload $candidates
$panes = @($candidatesPayload.candidates)

# The probe needs a cell to hold, so it opens the first row the Cell View offers when none is open.
$anchor = Get-HeldCell
if (-not $anchor) {
    $cellPane = $panes | Sort-Object { @($_.items).Count } -Descending | Select-Object -First 1
    if ($cellPane) {
        $notify = Invoke-Mcp 'ck.ui' (@{ method = 'ui.notify'; args = "$($cellPane.control) dblclk 0" } | ConvertTo-Json -Compress)
        $observed['openedForSetup'] = [ordered]@{ control = "$($cellPane.control)"; status = "$($notify.status)"; code = "$($notify.code)" }
        for ($i = 0; $i -lt 20 -and -not $anchor; $i++) {
            Start-Sleep -Milliseconds 1000
            $anchor = Get-HeldCell
        }
    }
}

$held = if ($anchor) { $anchor.editorId } else { '' }
$observed['held'] = [ordered]@{ editorId = $held; formId = if ($anchor) { $anchor.formId } else { '' } }

if (-not $held) {
    Write-Output 'SETUP no cell could be opened to hold; open one in the Cell View first, or pass -Cell'
    exit 2
}
$observed['panes'] = @($panes | ForEach-Object { [ordered]@{ window = "$($_.window)"; title = "$($_.title)"; control = "$($_.control)" } })

if ($panes.Count -eq 0) {
    Write-Output "SETUP no pane offered items to pick a name from: $($candidates.message)"
    exit 2
}

$cellPane = $panes | Where-Object { (Get-ItemTexts $_.items) -contains $held } | Select-Object -First 1
$cellItems = @()
if ($cellPane) { $cellItems = @(Get-ItemTexts $cellPane.items) }

$ignoredName = ''
foreach ($pane in $panes) {
    if ($pane -eq $cellPane) { continue }
    foreach ($item in (Get-ItemTexts $pane.items)) {
        $text = "$item".Trim()
        # A name has to be one line, short, and absent from the Cell View's own rows.
        if ($text.Length -lt 4 -or $text.Length -gt 60) { continue }
        if ($text -match '\s{2,}' -or $text -match '[0-9A-Fa-f]{8}') { continue }
        if ($cellItems -contains $text) { continue }
        if ($text -ieq $held) { continue }
        $ignoredName = $text
        break
    }
    if ($ignoredName) { break }
}

if (-not $ignoredName) {
    Write-Output 'SETUP no other pane offered a name that the Cell View does not also list'
    exit 2
}

$observed['ignoredName'] = $ignoredName

# Case 1: a row that is not a cell cannot open one, so the wait must end in cell_not_loaded.
$attempt = Invoke-Mcp 'ck.cell_load' (@{ name = $ignoredName } | ConvertTo-Json -Compress)
$attemptPayload = Get-Payload $attempt
$observed['attempt'] = [ordered]@{
    status = "$($attempt.status)"
    code = "$($attempt.code)"
    message = "$($attempt.message)"
    requested = "$($attemptPayload.requested)"
    opened = "$($attemptPayload.opened.editorId)"
}

if ("$($attempt.status)" -ne 'error') { $failures.Add("the ignored load answered $($attempt.status), not an error") }
if ("$($attempt.code)" -ne 'cell_not_loaded') { $failures.Add("the ignored load answered $($attempt.code), not cell_not_loaded") }
if ("$($attempt.message)" -notmatch [regex]::Escape($ignoredName)) { $failures.Add('the message does not name the cell that was asked for') }
if ("$($attempt.message)" -notmatch [regex]::Escape($held)) { $failures.Add('the message does not name the cell that is still held') }
if ("$($attemptPayload.requested)" -ne $ignoredName) { $failures.Add("the payload reports requested '$($attemptPayload.requested)'") }
if ("$($attemptPayload.opened.editorId)" -ne $held) { $failures.Add("the payload reports opened '$($attemptPayload.opened.editorId)', not the held cell") }

# Case 2: the editor really did ignore it, which is the fact the branch is reporting.
$after = Invoke-Mcp 'ck.cell' '{}'
$afterPayload = Get-Payload $after
$observed['afterAttempt'] = [ordered]@{ editorId = "$($afterPayload.editorId)"; formId = "$($afterPayload.formId)" }
if ("$($afterPayload.editorId)" -ne $held) { $failures.Add("the render window moved to $($afterPayload.editorId) on an ignored load") }

# Case 3: the control, so a tool that always says cell_not_loaded cannot pass this probe.
$control = Invoke-Mcp 'ck.cell_load' (@{ name = $held } | ConvertTo-Json -Compress)
$observed['control'] = [ordered]@{ status = "$($control.status)"; code = "$($control.code)"; message = "$($control.message)" }
if ("$($control.status)" -ne 'ok') { $failures.Add("loading the cell already open answered $($control.status)/$($control.code), so the probe cannot tell the two apart") }

foreach ($failure in $failures) { Write-Output "FAIL $failure" }

if ($Evidence) {
    $observed['failures'] = @($failures)
    $evidencePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Evidence)
    $evidenceDir = Split-Path -Parent $evidencePath
    if ($evidenceDir -and -not (Test-Path -LiteralPath $evidenceDir)) {
        New-Item -ItemType Directory -Force -Path $evidenceDir | Out-Null
    }
    ($observed | ConvertTo-Json -Depth 8) | Set-Content -LiteralPath $evidencePath -Encoding utf8
    Write-Output "evidence $evidencePath"
}

if ($failures.Count -gt 0) { exit 1 }

Write-Output "PASS '$ignoredName' was accepted and ignored, cell_not_loaded named $held as the cell that stayed, and the control load answered ok"
exit 0
