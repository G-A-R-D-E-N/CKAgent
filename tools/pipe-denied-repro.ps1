<#
.SYNOPSIS
Reproduces pipe_denied with the low-integrity apphost and checks the message it carries.

.DESCRIPTION
The refusal message is the only diagnostic a caller gets when a low-integrity host cannot reach the editor, so
it has to say which side to fix. That wording regressed once already - it blamed a plugin that "predates the
low-labelled pipe" when the real cause was the default being strict by design - and a unit test on a fabricated
exception cannot show the Win32 part of it.

This script runs `ckagent.exe`, which is the low-integrity client on this host, against a pipe tool and asserts
what the real refusal says:

  1. the reply is an error with code `pipe_denied`, not a timeout or a plugin-shaped failure;
  2. the message carries the Win32 error the connection actually produced (`0x80070005`, access denied);
  3. it names this client's level, "low integrity";
  4. it names both switches a caller has to set - `--allow-low-pipe` on the host and `[transport]
     LowIntegrityPipe=1` in `CKAgent.ini` beside the plugin;
  5. it does not blame a stale plugin, which is what the message used to do and what sent this investigation
     after the wrong process.

It also runs the same call with `--allow-low-pipe` and records the reply, so the evidence shows what the
diagnosis points at once the opt-in is on. That second run is reported, not asserted: on a machine whose loose
pipe does not admit this client, the honest answer is a recorded line, not a passing check.

Exit codes: 0 the refusal and its message are as documented, 1 the message does not say what it must, 2 the
apphost did not come up low or the editor is not running.

.PARAMETER Exe
Path to the built apphost, the low-integrity client under test.

.PARAMETER Game
Game directory handed to the server.

.PARAMETER Evidence
Optional path for the JSON evidence file (the replies and the message checks).

.EXAMPLE
# from the CKAgent directory, with the editor running and the strict pipe in place
./tools/pipe-denied-repro.ps1 -Evidence pipe-denied.json
#>

param(
    [string]$Exe = 'CKAgent.Mcp/bin/Release/net8.0/ckagent.exe',
    [string]$Game = 'D:\SteamLibrary\steamapps\common\Fallout 4',
    [string]$Evidence = ''
)

$ErrorActionPreference = 'Stop'

$scratch = Join-Path $PSScriptRoot '.probe-tmp'
New-Item -ItemType Directory -Force $scratch | Out-Null
$env:TEMP = $scratch
$env:TMP = $scratch

$failures = New-Object System.Collections.Generic.List[string]
$observed = [ordered]@{}

function Invoke-Mcp([string]$tool, [string]$json, [string]$extra = '') {
    # Both streams go to files: PowerShell 5.1 turns a native child's stderr into an error record.
    $outFile = Join-Path $scratch 'mcp-stdout.txt'
    $errFile = Join-Path $scratch 'mcp-stderr.txt'
    $argsFile = Join-Path $scratch 'mcp-args.json'
    Set-Content -LiteralPath $argsFile -Value $json -Encoding utf8

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & node (Join-Path $PSScriptRoot 'mcp-call.mjs') $Exe $tool $argsFile $Game 60000 $extra > $outFile 2> $errFile
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

# Constraint: the apphost must be the low client, or the refusal comes from the wrong token.
$status = Invoke-Mcp 'ck.status' '{}'
$observed['client'] = [ordered]@{
    clientIntegrity = "$($status.clientIntegrity)"
    running = $status.running
    editorSight = "$($status.editorSight)"
}

if ("$($status.clientIntegrity)" -ne 'low') {
    Write-Output "SETUP the apphost came up at $($status.clientIntegrity) integrity, not low: run this from the low context"
    exit 2
}

if (-not $status.running) {
    Write-Output 'SETUP the editor is not running, so there is no pipe to be refused by: start it with ckpe_loader.exe'
    exit 2
}

$denied = Invoke-Mcp 'ck.cell' '{}'
$message = "$($denied.message)"
$observed['denied'] = [ordered]@{ status = "$($denied.status)"; code = "$($denied.code)"; message = $message }

# The requirements the message has to meet, one line each so a failure names the part that broke.
$checks = [ordered]@{
    'code is pipe_denied' = ("$($denied.code)" -eq 'pipe_denied')
    'carries the Win32 error' = ($message -match '0x80070005')
    'names the client level' = ($message -match 'low integrity')
    'names --allow-low-pipe' = ($message -match '--allow-low-pipe')
    'names LowIntegrityPipe=1' = ($message -match 'LowIntegrityPipe=1')
    'does not blame a stale plugin' = ($message -notmatch 'predates')
}

$observed['checks'] = [ordered]@{}
foreach ($name in $checks.Keys) {
    $observed['checks'][$name] = $checks[$name]
    if (-not $checks[$name]) { $failures.Add("the refusal message fails '$name': $message") }
}

# The same call with the opt-in on: recorded, because the diagnosis is only useful if it leads somewhere.
$optIn = Invoke-Mcp 'ck.cell' '{}' '--allow-low-pipe'
$observed['withOptIn'] = [ordered]@{
    status = "$($optIn.status)"
    code = "$($optIn.code)"
    message = "$($optIn.message)"
    payload = "$($optIn.payload)"
}

foreach ($failure in $failures) { Write-Output "FAIL $failure" }

if ($Evidence) {
    $observed['failures'] = @($failures)
    $evidencePath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Evidence)
    $evidenceDir = Split-Path -Parent $evidencePath
    if ($evidenceDir -and -not (Test-Path -LiteralPath $evidenceDir)) {
        New-Item -ItemType Directory -Force -Path $evidenceDir | Out-Null
    }
    ($observed | ConvertTo-Json -Depth 6) | Set-Content -LiteralPath $evidencePath -Encoding utf8
    Write-Output "evidence $evidencePath"
}

if ($failures.Count -gt 0) { exit 1 }

Write-Output "PASS the low client is refused with 0x80070005 and a message naming both switches"
Write-Output "  with --allow-low-pipe the same call answered $($optIn.status)/$($optIn.code)"
exit 0
