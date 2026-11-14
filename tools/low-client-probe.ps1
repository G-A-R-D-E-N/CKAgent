<#
.SYNOPSIS
Asserts a low-integrity apphost is refused by the strict pipe and answered by the opt-in one.

.DESCRIPTION
The failure this probe is written from is the pair of quiet lies the transport told before: a low-integrity
`ckagent.exe` was answered over a pipe that any low process could write to, and later it was told
`editor_not_running` while the editor sat in front of it. Both look like a working session from the MCP
client's side, so the apphost has to be run as the low client it is and the replies checked.

It runs `ckagent.exe` - not `dotnet ckagent.dll`, whose host lives in Program Files and is medium - through the
protocol, and asserts:

  1. the apphost really is low: `ck.status` reports `clientIntegrity: low`, otherwise the run proves nothing
     and exits 2;
  2. the editor is found at all: `running: true` with a pid, whichever evidence found it;
  3. the default pipe stays strict: `pluginPipeAvailable` is false, `pluginPipe` is absent, and a pipe tool is
     refused with `pipe_denied` whose message names both switches - `--allow-low-pipe` on the host and
     `[transport] LowIntegrityPipe=1` in `CKAgent.ini` beside the plugin;
  4. those refusal assertions are run against two fabricated replies - a plain `CKPEUnavailable` and a
     `pipe_denied` that names nothing - and both must be rejected, which is what proves the check bites;
  5. with the ini and `--allow-low-pipe`, the editor answers: `pluginPipe` is `ckagent-low` and `ck.cell`
     returns `ok` with a payload that names a cell;
  6. a medium host is unaffected: `dotnet ckagent.dll` still reports `pluginPipe: ckagent`, so the opt-in did
     not displace the default.

Exit codes: 0 pass, 1 assertion failed, 2 the apphost did not come up low or the editor is not running,
3 the opt-in pipe is refused or unreachable.

.PARAMETER Exe
Path to the built apphost, the low-integrity client under test.

.PARAMETER Dll
Path to the built MCP assembly, used for the medium-host sanity case.

.PARAMETER Game
Game directory handed to the server.

.PARAMETER Evidence
Optional path for the JSON evidence file (observed replies and failures).

.PARAMETER StrictOnly
Assert only that the default pipe refuses this client. The opt-in half is skipped, which is the right run on a
machine that deliberately does not open the low pipe; without it the probe requires the editor to answer.

.EXAMPLE
# from the CKAgent directory, with the editor running and CKAgent.ini opting the low pipe in
./tools/low-client-probe.ps1 -Evidence low-client-probe.json
#>

param(
    [string]$Exe = 'CKAgent.Mcp/bin/Release/net8.0/ckagent.exe',
    [string]$Dll = 'CKAgent.Mcp/bin/Release/net8.0/ckagent.dll',
    [string]$Game = 'D:\SteamLibrary\steamapps\common\Fallout 4',
    [string]$Evidence = '',
    [switch]$StrictOnly
)

$ErrorActionPreference = 'Stop'

$scratch = Join-Path $PSScriptRoot '.probe-tmp'
New-Item -ItemType Directory -Force $scratch | Out-Null
$env:TEMP = $scratch
$env:TMP = $scratch

$failures = New-Object System.Collections.Generic.List[string]
$observed = [ordered]@{}

function Invoke-Mcp([string]$host_, [string]$tool, [string]$json, [string]$extra = '') {
    # Both streams go to files: PowerShell 5.1 turns a native child's stderr into an error record.
    $outFile = Join-Path $scratch 'mcp-stdout.txt'
    $errFile = Join-Path $scratch 'mcp-stderr.txt'
    $argsFile = Join-Path $scratch 'mcp-args.json'
    Set-Content -LiteralPath $argsFile -Value $json -Encoding utf8

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & node (Join-Path $PSScriptRoot 'mcp-call.mjs') $host_ $tool $argsFile $Game 60000 $extra > $outFile 2> $errFile
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

# The rule the regression breaks: a refusal must say which switch is missing on which side.
function Test-Refusal([object]$reply, [string]$label) {
    $problems = New-Object System.Collections.Generic.List[string]

    if ("$($reply.status)" -ne 'error') { $problems.Add("$label`: the reply is $($reply.status)/$($reply.code), not a refusal") }
    if ("$($reply.code)" -ne 'pipe_denied') { $problems.Add("$label`: the code is $($reply.code), not pipe_denied") }

    $message = "$($reply.message)"
    if ($message -notmatch '--allow-low-pipe') { $problems.Add("$label`: the message does not name --allow-low-pipe") }
    if ($message -notmatch 'LowIntegrityPipe=1') { $problems.Add("$label`: the message does not name LowIntegrityPipe=1") }

    return @($problems)
}

function Test-Answer([object]$reply, [string]$label) {
    $problems = New-Object System.Collections.Generic.List[string]

    if ("$($reply.status)" -ne 'ok') { $problems.Add("$label`: the reply is $($reply.status)/$($reply.code), not ok: $($reply.message)") }

    $payload = $reply.payload
    if ($payload -is [string]) {
        try { $payload = $payload | ConvertFrom-Json } catch { $problems.Add("$label`: the payload is not JSON") }
    }
    if ($null -eq $payload) {
        $problems.Add("$label`: no payload")
    } else {
        foreach ($key in @('editorId', 'formId', 'resolved')) {
            if (-not ($payload.PSObject.Properties.Name -contains $key)) { $problems.Add("$label`: the payload has no $key") }
        }
    }

    return @($problems)
}

# Constraint: the apphost must be the low client, or every later assertion is about the wrong process.
$status = Invoke-Mcp $Exe 'ck.status' '{}'
$observed['apphost'] = [ordered]@{
    clientIntegrity = "$($status.clientIntegrity)"
    running = $status.running
    pid = $status.pid
    editorSight = "$($status.editorSight)"
    pluginPipe = "$($status.pluginPipe)"
    pluginPipeAvailable = $status.pluginPipeAvailable
}

if ("$($status.clientIntegrity)" -ne 'low') {
    Write-Output "SETUP the apphost came up at $($status.clientIntegrity) integrity, not low: run this from the low context that launched it"
    exit 2
}

if (-not $status.running) {
    Write-Output "SETUP the editor is not running, so there is nothing to answer: start it with ckpe_loader.exe"
    exit 2
}

# Case 1: the default pipe must not admit this client.
if ($status.pluginPipeAvailable -ne $false) { $failures.Add('the strict pipe answered a low client, so the default is not strict') }
if ("$($status.pluginPipe)" -ne '') { $failures.Add("ck.status names pluginPipe $($status.pluginPipe) without --allow-low-pipe") }

$refused = Invoke-Mcp $Exe 'ck.cell' '{}'
$observed['strictRefusal'] = [ordered]@{ code = "$($refused.code)"; message = "$($refused.message)" }
foreach ($problem in (Test-Refusal $refused 'the strict pipe')) { $failures.Add($problem) }

# Constraint: the refusal check must reject replies that name nothing, or it would pass on any error.
$fabricated = @(
    [pscustomobject]@{ status = 'error'; code = 'CKPEUnavailable'; message = 'no CKAgent plugin is listening' },
    [pscustomobject]@{ status = 'error'; code = 'pipe_denied'; message = 'the pipe refused the connection' }
)
foreach ($fake in $fabricated) {
    if ((Test-Refusal $fake 'a fabricated refusal').Count -eq 0) {
        $failures.Add("the refusal check accepts a reply it must reject: $($fake.code) $($fake.message)")
    }
}

# Case 2: with both switches, the editor must answer the same client.
$answerProblems = @()
if ($StrictOnly) {
    Write-Output 'SKIP the opt-in half: -StrictOnly was given, so this run only asserts the default refusal'
} else {
$optInStatus = Invoke-Mcp $Exe 'ck.status' '{}' '--allow-low-pipe'
$observed['optIn'] = [ordered]@{
    lowerIntegrityPipeAllowed = $optInStatus.lowIntegrityPipeAllowed
    pluginPipe = "$($optInStatus.pluginPipe)"
    pluginPipeAvailable = $optInStatus.pluginPipeAvailable
}

if ($optInStatus.lowIntegrityPipeAllowed -ne $true) { $failures.Add('--allow-low-pipe was not reported as allowed') }
if ("$($optInStatus.pluginPipe)" -ne 'ckagent-low') { $failures.Add("with --allow-low-pipe the pipe reached is '$($optInStatus.pluginPipe)', not ckagent-low") }

$answer = Invoke-Mcp $Exe 'ck.cell' '{}' '--allow-low-pipe'
$observed['optInAnswer'] = [ordered]@{ status = "$($answer.status)"; code = "$($answer.code)"; message = "$($answer.message)" }
$answerProblems = @(Test-Answer $answer 'the opt-in pipe')
foreach ($problem in $answerProblems) { $failures.Add($problem) }
}

# Case 3: the medium host must still reach the default pipe.
$medium = Invoke-Mcp $Dll 'ck.status' '{}'
$observed['mediumHost'] = [ordered]@{ clientIntegrity = "$($medium.clientIntegrity)"; pluginPipe = "$($medium.pluginPipe)" }
if ("$($medium.pluginPipe)" -ne 'ckagent') {
    $failures.Add("the medium host reached '$($medium.pluginPipe)', not the default ckagent pipe")
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

if ($failures.Count -gt 0) {
    # The opt-in pipe failing to answer is a transport problem, not a wrong assertion.
    if ($answerProblems.Count -gt 0) {
        Write-Output 'FAIL the opt-in pipe did not answer this client, so [transport] LowIntegrityPipe=1 in CKAgent.ini beside the plugin, read at editor start, is not in effect for it'
        exit 3
    }
    exit 1
}

Write-Output 'PASS the low apphost was refused by the strict pipe, answered over ckagent-low, and the medium host still reached ckagent'
