<#
.SYNOPSIS
Runs the blocked-modal probe and the camera cross-check in one pass, into one evidence file.

.DESCRIPTION
The two checks need opposite editor states. The probe opens `File/Data`, which the editor greys out while a
cell is open; the cross-check reads the camera transform, which needs a cell. So the wrapper reads
`status.loaded` first and orders the work to match:

  - no cell loaded: run the probe, load the cell through `ck.cell_load`, then run the cross-check;
  - a cell already loaded: run the cross-check, and report the probe as not run, naming the reason, because
    closing a cell is the operator's move and not something this wrapper can do.

Both scripts write their own JSON when given `-Evidence`, so the wrapper points each at a temporary file and
embeds the parsed result, which is what makes one file enough. The child output is kept for the failed step,
because the evidence alone does not say which assertion printed.

Exit codes: 0 both passed, 1 a check failed, 2 the editor state did not allow both checks to run,
3 the plugin pipe is unreachable.

.PARAMETER Dll
Path to the built MCP server assembly, as the cell load goes through it.

.PARAMETER Game
Fallout 4 directory the MCP server should talk to.

.PARAMETER Cell
Cell to load for the cross-check once the probe has finished.

.PARAMETER Evidence
Path of the single JSON file this writes.

.EXAMPLE
# from the CKAgent directory, with the editor running and no cell open
./tools/live-evidence.ps1 -Evidence live-evidence.json
#>
param(
    [string]$Dll = 'CKAgent.Mcp/bin/Release/net8.0/ckagent.dll',
    [string]$Game = 'D:\SteamLibrary\steamapps\common\Fallout 4',
    [string]$Cell = 'DmndRadio01',
    [string]$Evidence = 'live-evidence.json'
)

$ErrorActionPreference = 'Stop'

$probeScript = Join-Path $PSScriptRoot 'blocked-modal-probe.ps1'
$crossScript = Join-Path $PSScriptRoot 'camera-tool-cross-check.ps1'
$scratch = Join-Path $PSScriptRoot '.live-evidence-tmp'
New-Item -ItemType Directory -Force $scratch | Out-Null

$failures = New-Object System.Collections.Generic.List[string]
$steps = [ordered]@{}
$incomplete = $false

function Invoke-Plugin([string]$command) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'ckagent', [System.IO.Pipes.PipeDirection]::InOut)
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

function Get-Status {
    return (Invoke-Plugin 'status') | ConvertFrom-Json
}

# A child check must not take the wrapper down with it: its exit code is the result, not an exception.
function Invoke-Check([string]$name, [string]$script, [string]$evidencePath) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & powershell -NoProfile -ExecutionPolicy Bypass -File $script -Evidence $evidencePath 2>&1
    } finally {
        $ErrorActionPreference = $previous
    }
    $code = $LASTEXITCODE
    $step = [ordered]@{ exit = $code; passed = ($code -eq 0) }

    if (Test-Path -LiteralPath $evidencePath -PathType Leaf) {
        try {
            $step['evidence'] = Get-Content -LiteralPath $evidencePath -Raw | ConvertFrom-Json
        } catch {
            $step['evidenceError'] = "the evidence file is not valid JSON: $($_.Exception.Message)"
        }
    }

    if ($code -ne 0) {
        $step['output'] = @($output | Select-Object -Last 12)
        $failures.Add("$name exited $code")
    }

    return $step
}

function Invoke-Mcp([string]$tool, [string]$json) {
    $argsPath = Join-Path $scratch 'args.json'
    Set-Content -LiteralPath $argsPath -Value $json -Encoding utf8
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $output = & node (Join-Path $PSScriptRoot 'mcp-call.mjs') $Dll $tool $argsPath $Game 120000 2>&1
    } finally {
        $ErrorActionPreference = $previous
    }
    $code = $LASTEXITCODE
    $parsed = $null
    foreach ($line in @($output)) {
        if ("$line" -match '^\s*\{') {
            try { $parsed = $line | ConvertFrom-Json } catch { $parsed = $null }
        }
    }
    return [ordered]@{ exit = $code; reply = $parsed }
}

try {
    $status = Get-Status
} catch {
    Write-Output "BLOCKED $($_.Exception.Message)"
    exit 3
}

$steps['editor'] = [ordered]@{ pid = $status.pid; loaded = $status.loaded; blockedBy = $status.blockedBy }

if ($status.loaded) {
    $steps['cameraCrossCheck'] = Invoke-Check 'the camera cross-check' $crossScript (Join-Path $scratch 'cross.json')
    $steps['blockedModal'] = [ordered]@{
        exit = 2
        passed = $false
        reason = "a cell is loaded, so File/Data is disabled and the hidden-modal state cannot be rebuilt; close the cell and re-run"
    }
    $incomplete = $true
} else {
    $steps['blockedModal'] = Invoke-Check 'the blocked-modal probe' $probeScript (Join-Path $scratch 'probe.json')

    # The cross-check needs a loaded cell, and the probe needed it absent, so the load belongs between them.
    $load = Invoke-Mcp 'ck.cell_load' ('{"name":"' + $Cell + '"}')
    $steps['cellLoad'] = [ordered]@{ cell = $Cell; exit = $load.exit; status = $load.reply.status; code = $load.reply.code }
    if ($load.exit -ne 0 -or "$($load.reply.status)" -ne 'ok') {
        $failures.Add("the cell '$Cell' did not load ($($load.reply.code) $($load.reply.message)), so the cross-check cannot run")
        $incomplete = $true
    } else {
        $steps['cameraCrossCheck'] = Invoke-Check 'the camera cross-check' $crossScript (Join-Path $scratch 'cross.json')
    }
}

$passed = (-not $incomplete) -and ($failures.Count -eq 0)
$report = [ordered]@{
    when = (Get-Date).ToString('o')
    cell = $Cell
    dll = $Dll
    steps = $steps
    failures = @($failures)
    passed = $passed
} | ConvertTo-Json -Depth 12

Set-Content -LiteralPath $Evidence -Value $report -Encoding utf8
Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Output "FAIL $failure" }
}

foreach ($name in $steps.Keys) {
    if ($name -eq 'editor') { continue }
    $step = $steps[$name]
    $verdict = if ("$($step.passed)" -eq 'True') { 'pass' } else { "exit $($step.exit)" }
    Write-Output "$name`: $verdict"
}

if ($incomplete) {
    Write-Output "INCOMPLETE both checks did not run; the reason is in $Evidence"
    exit 2
}

if (-not $passed) {
    Write-Output "FAIL the checks did not all pass; the detail is in $Evidence"
    exit 1
}

Write-Output "PASS the blocked-modal probe and the camera cross-check both passed; the evidence is in $Evidence"
exit 0
