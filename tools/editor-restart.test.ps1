<#
.SYNOPSIS
Drives tools/editor-restart.ps1 with real processes standing in for CreationKit.exe and ckpe_loader.exe.

.DESCRIPTION
Replacing CKAgent.dll does nothing until the editor restarts, and the restart only works in one order: prove the
loader, stop the editor, start the loader, poll for the frame. A stand-in process pins each part without a Creation
Kit, so the commands job performs a real stop, a real start and a real poll, and the two failure cases show the wait
polling to its deadline instead of reading the process once.

The frame is the editor's window, which a stand-in cannot own headlessly, so -ReadyCheck is passed here and answers
"the named process is up". Everything else - the loader proof, the stop, the start, the poll count and the deadline -
runs for real.
#>
$ErrorActionPreference = 'Stop'

$restart = Join-Path $PSScriptRoot 'editor-restart.ps1'
$failed = New-Object System.Collections.Generic.List[string]
$observed = [ordered]@{}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('ckagent-restart-' + [guid]::NewGuid().ToString('N'))
$game = Join-Path $root 'game'
$standin = Join-Path $root 'standin'

$isUp = { param($name) Get-Process -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1 }

function Stop-Standins {
    Get-Process -Name 'CreationKit', 'OtherEditor' -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($standin, [System.StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force -ErrorAction SilentlyContinue
}

function Start-StandIn([string]$path) {
    $process = Start-Process -FilePath $path -ArgumentList '-n', '120', '127.0.0.1' -PassThru
    Start-Sleep -Milliseconds 400
    if (-not (Get-Process -Id $process.Id -ErrorAction SilentlyContinue)) {
        throw "the stand-in $path did not stay up"
    }
    return $process
}

function StepIndex([string[]]$lines, [string]$pattern) {
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -like $pattern) { return $i }
    }
    return -1
}

try {
    New-Item -ItemType Directory -Force -Path $game, $standin | Out-Null
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\PING.EXE') -Destination (Join-Path $standin 'CreationKit.exe')
    Copy-Item -LiteralPath (Join-Path $env:SystemRoot 'System32\PING.EXE') -Destination (Join-Path $standin 'OtherEditor.exe')

    $editor = Join-Path $standin 'CreationKit.exe'

    # 1. A slow loader: the stop happens, the loader starts, the first poll misses, a later poll finds the frame.
    # Constraint: the loader is a batch stand-in, so it is named and passed explicitly; a text file called .exe is not
    # a program Windows can start.
    $slow = Join-Path $root 'ckpe_loader-slow.cmd'
    @(
        '@echo off',
        'timeout /t 2 /nobreak >nul',
        ('start "" "' + $editor + '" -n 120 127.0.0.1')
    ) | Set-Content -LiteralPath $slow

    $old = Start-StandIn $editor
    $log = Join-Path $root 'restart.log'
    $json = & $restart -Game $game -Loader $slow -WaitSeconds 30 -PollMs 250 -Log $log -ReadyCheck $isUp |
        ConvertFrom-Json
    $lines = @(Get-Content -LiteralPath $log)

    $observed['restart'] = [ordered]@{ ready = $json.ready; polls = $json.polls }

    if ($json.ready -eq $old.Id) { $failed.Add('the restart reported the editor it had just stopped as the ready one') }
    if ($json.polls -lt 2) { $failed.Add("the frame wait answered after $($json.polls) poll(s), so it was not polling") }
    if (Get-Process -Id $old.Id -ErrorAction SilentlyContinue) { $failed.Add('the old editor stand-in was never stopped') }
    if (-not (Get-Process -Id $json.ready -ErrorAction SilentlyContinue)) { $failed.Add('the ready process is not running') }

    $loaderAt = StepIndex $lines '- loader_confirmed:*'
    $stopAt = StepIndex $lines '- stop:*'
    $startAt = StepIndex $lines '- started:*'
    $firstPollAt = StepIndex $lines '- poll:*'

    if ($loaderAt -lt 0) { $failed.Add('the restart log shows no loader proof') }
    if ($stopAt -lt 0) { $failed.Add('the restart log shows no stop of the running editor') }
    if ($startAt -lt 0) { $failed.Add('the restart log shows no start of the loader') }
    if ($firstPollAt -lt 0) { $failed.Add('the restart log shows no frame poll') }

    if ($loaderAt -ge 0 -and $stopAt -ge 0 -and $loaderAt -gt $stopAt) {
        $failed.Add('the editor was stopped before the loader was proved')
    }
    if ($startAt -ge 0 -and $firstPollAt -ge 0 -and $startAt -gt $firstPollAt) {
        $failed.Add('the frame was polled before the loader was started')
    }

    Stop-Standins

    # 2. A loader that starts nothing: the wait polls to its deadline and then fails, naming the poll count.
    $none = Join-Path $root 'ckpe_loader-none.cmd'
    @('@echo off', 'exit /b 0') | Set-Content -LiteralPath $none
    $old = Start-StandIn $editor
    $timeoutLog = Join-Path $root 'timeout.log'

    try {
        & $restart -Game $game -Loader $none -WaitSeconds 3 -PollMs 250 -Log $timeoutLog -ReadyCheck $isUp | Out-Null
        $failed.Add('the restart answered a frame that never appeared')
    }
    catch {
        if ("$($_.Exception.Message)" -notmatch 'frame appeared within') {
            $failed.Add("a missing frame failed with an unnamed error: $($_.Exception.Message)")
        }
    }

    $polls = @(Select-String -LiteralPath $timeoutLog -Pattern '^- poll:\d+' -AllMatches).Count
    $observed['timeoutPolls'] = $polls

    if ($polls -lt 2) { $failed.Add("a missing frame was waited for with $polls poll(s), so the wait did not poll") }
    if (-not (Select-String -LiteralPath $timeoutLog -Pattern '^- no_frame:' -Quiet)) {
        $failed.Add('a missing frame left no no_frame step in the log')
    }

    Stop-Standins

    # 3. A loader that starts the wrong process: the frame name never matches, so the wait polls and fails.
    $wrong = Join-Path $root 'ckpe_loader-wrong.cmd'
    @(
        '@echo off',
        ('start "" "' + (Join-Path $standin 'OtherEditor.exe') + '" -n 120 127.0.0.1')
    ) | Set-Content -LiteralPath $wrong

    $wrongLog = Join-Path $root 'wrong.log'

    try {
        & $restart -Game $game -Loader $wrong -WaitSeconds 3 -PollMs 250 -Log $wrongLog -ReadyCheck $isUp | Out-Null
        $failed.Add('the restart accepted a process that is not the editor')
    }
    catch {
        if ("$($_.Exception.Message)" -notmatch 'frame appeared within') {
            $failed.Add("the wrong process failed with an unnamed error: $($_.Exception.Message)")
        }
    }

    $wrongPolls = @(Select-String -LiteralPath $wrongLog -Pattern '^- poll:\d+' -AllMatches).Count
    $observed['wrongProcessPolls'] = $wrongPolls

    if ($wrongPolls -lt 2) { $failed.Add('a wrong process was waited for with one poll, so the frame wait is not a wait') }

    Stop-Standins

    # 4. No loader at all: nothing is stopped, and the failure names the loader.
    $absent = Join-Path $root 'ckpe_loader.exe'
    $old = Start-StandIn $editor
    $missingLog = Join-Path $root 'missing.log'

    try {
        & $restart -Game $game -Loader $absent -WaitSeconds 3 -PollMs 250 -Log $missingLog -ReadyCheck $isUp | Out-Null
        $failed.Add('the restart ran with no loader')
    }
    catch {
        if ("$($_.Exception.Message)" -notmatch 'ckpe_loader') {
            $failed.Add("a missing loader failed with an unnamed error: $($_.Exception.Message)")
        }
    }

    if (-not (Get-Process -Id $old.Id -ErrorAction SilentlyContinue)) {
        $failed.Add('the editor was stopped before the loader was proved to exist')
    }

    if (Select-String -LiteralPath $missingLog -Pattern '^- stop:' -Quiet) {
        $failed.Add('the restart log shows a stop even though the loader was missing')
    }
}
finally {
    Stop-Standins
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

$report = [pscustomobject]@{ checks = 4; observed = $observed; failures = $failed }
$report | ConvertTo-Json -Depth 4

if ($failed.Count -gt 0) {
    Write-Host "restart checks failed: $($failed -join '; ')"
    exit 1
}

exit 0
