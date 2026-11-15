<#
.SYNOPSIS
Restarts the editor through ckpe_loader.exe after a plug-in was replaced, and waits for its frame.

.DESCRIPTION
The plug-in loads at editor startup, so replacing CKAgent.dll only takes effect after a restart. The order matters: a
restart that stops the editor before it knows the loader exists leaves the operator with neither, and a wait that reads
the process once instead of polling reports a slow editor as a failed restart. This script proves the loader, records
the stop, starts the loader, then polls for the editor frame and fails with the number of polls it made when the frame
never appears.

-ReadyCheck exists so this path can be exercised without a Creation Kit: it takes the process name and answers the
process that counts as ready, and it defaults to the editor frame (a non-zero MainWindowHandle).

.EXAMPLE
./tools/editor-restart.ps1 -Game 'D:\SteamLibrary\steamapps\common\Fallout 4'
#>
param(
    [Parameter(Mandatory = $true)][string]$Game,
    [string]$EditorProcessName = 'CreationKit',
    [string]$Loader = '',
    [int]$WaitSeconds = 120,
    [int]$PollMs = 500,
    [int]$StopSettleMs = 2000,
    [string]$Log = '',
    [scriptblock]$ReadyCheck = $null
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Game -PathType Container)) { throw "the game directory $Game does not exist" }

$gameRoot = (Resolve-Path -LiteralPath $Game).Path

if ([string]::IsNullOrWhiteSpace($Loader)) { $Loader = Join-Path $gameRoot 'ckpe_loader.exe' }

$steps = New-Object System.Collections.Generic.List[string]
$lines = New-Object System.Collections.Generic.List[string]

function Note([string]$step) {
    $steps.Add($step)
    $lines.Add('-' + ' ' + $step)
}

# Constraint: the loader is proved before anything is stopped, so a failed restart never leaves the operator with no editor.
if (-not (Test-Path -LiteralPath $Loader -PathType Leaf)) {
    Note "loader_missing:$Loader"
    if ($Log) { Set-Content -LiteralPath $Log -Value $lines }
    throw "$Loader is not in $gameRoot, so the editor cannot be restarted through it; start the editor yourself and call ck.status"
}

Note "loader_confirmed:$Loader"

if (-not $ReadyCheck) {
    $ReadyCheck = { param($name) Get-Process -Name $name -ErrorAction SilentlyContinue |
        Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1 }
}

$running = @(Get-Process -Name $EditorProcessName -ErrorAction SilentlyContinue)

if ($running.Count -eq 0) {
    Note 'stop:none'
}
else {
    foreach ($editor in $running) {
        Note "stop:$($editor.Id)"
        Stop-Process -Id $editor.Id -Force
    }

    # Constraint: a new editor with the same name must not be confused with the one just stopped.
    $deadline = (Get-Date).AddMilliseconds($StopSettleMs)

    while ((Get-Date) -lt $deadline) {
        if (@(Get-Process -Name $EditorProcessName -ErrorAction SilentlyContinue).Count -eq 0) { break }
        Start-Sleep -Milliseconds 100
    }

    $left = @(Get-Process -Name $EditorProcessName -ErrorAction SilentlyContinue)

    if ($left.Count -gt 0) {
        Note "stop_failed:$($left[0].Id)"
        if ($Log) { Set-Content -LiteralPath $Log -Value $lines }
        throw "the running editor (pid $($left[0].Id)) did not stop, so the plug-in could not be reloaded; close CreationKit.exe and retry"
    }
}

$startedAt = Get-Date
Start-Process -FilePath $Loader -WorkingDirectory $gameRoot | Out-Null
Note "started:$Loader"

$deadline = $startedAt.AddSeconds($WaitSeconds)
$polls = 0
$ready = $null

# Constraint: a started loader is not a loaded plug-in, so the frame is polled instead of read once.
while ((Get-Date) -lt $deadline) {
    $polls++
    $candidate = & $ReadyCheck $EditorProcessName

    if ($null -ne $candidate -and @($candidate).Count -gt 0) {
        $ready = @($candidate)[0]
        Note "poll:$polls matched:$($ready.Id)"
        break
    }

    Note "poll:$polls missed"
    Start-Sleep -Milliseconds $PollMs
}

$elapsed = [int](((Get-Date) - $startedAt).TotalMilliseconds)

if ($null -eq $ready) {
    Note "no_frame:$polls"
    if ($Log) { Set-Content -LiteralPath $Log -Value $lines }

    [pscustomobject]@{
        loader = $Loader
        stopBeforeLoaderCheck = $false
        started = $true
        ready = $null
        polls = $polls
        waitedMs = $elapsed
        steps = $steps
    } | ConvertTo-Json -Compress

    throw "$Loader started but no $EditorProcessName frame appeared within $WaitSeconds s after $polls poll(s); wait for the editor and call ck.status before a navmesh run"
}

if ($Log) { Set-Content -LiteralPath $Log -Value $lines }

[pscustomobject]@{
    loader = $Loader
    stopBeforeLoaderCheck = $false
    started = $true
    ready = $ready.Id
    polls = $polls
    waitedMs = $elapsed
    steps = $steps
} | ConvertTo-Json -Compress
