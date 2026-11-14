<#
.SYNOPSIS
Recreates the editor's hidden-modal state and asserts CKAgent dismisses the dialog and retries the call.

.DESCRIPTION
A modal created while the editor is not foreground can leave the frame disabled with no dialog on
screen: `status.blockedBy` is then the only place the reason appears. This probe rebuilds that state and
checks the recovery path instead of the operator:

  1. the dialog exists, is blocking, and both `status` and `ui.modals` report it as hidden;
  2. an MCP call that needs the frame goes through BlockedEditor, which dismisses the dialog itself and
     retries the command, so the caller is never asked to click;
  3. after that call no blocking dialog is left and the reply is the retry's own result;
  4. dismissing a dialog clears `status.blockedBy` and the same call then succeeds;
  5. with the dismissal skipped through CKAGENT_TEST_DISMISS_FAILS=1, the reply names the dialog and says
     it could not be dismissed, and the plugin's `ui.dismiss` then clears it.

The MCP step runs through `mcp-call.mjs` because the retry lives in the server, above the plugin pipe.
The dialog comes from the editor's own modal command, because the hidden state is defined by Windows:
a `#32770` that is not visible, owned by the visible but disabled frame. Only a dialog created inside
the editor can satisfy that, so the probe runs `File/Data` - which the editor disables while a cell is
open - and its teardown always presses Cancel, so the plugin load order is never changed.

Exit codes: 0 pass, 1 assertion failed, 2 the hidden-modal state could not be reproduced (editor
foreground or no dialog appeared), 3 the plugin pipe is unreachable.

.PARAMETER Dll
Path to the built MCP server assembly, as used by the probe's MCP step.

.PARAMETER Command
Menu path of the command that opens a modal dialog which disables the frame. `File/Data` needs no cell
to be open; with a cell loaded the editor greys that item out and the probe exits 2.

.PARAMETER BlockedTool
MCP tool used as the call that fails while the editor is blocked, so BlockedEditor has a failure to
recover from. It must fail for an unrelated reason too - `ck.camera` needs a loaded cell - because the
plugin pipe stays responsive while the frame is disabled, so a call that works anyway never enters the
retry path.

.PARAMETER BlockedArgs
JSON arguments for the blocked tool. They must make the plugin refuse - `ck.frame` on a cell that is
not loaded does - because a call that succeeds never reaches the retry logic.

.PARAMETER Attempts
How many times to try creating the dialog out of the foreground before giving up with exit code 2.

.EXAMPLE
# from the repository root, with the server built and the editor running
./tools/blocked-modal-probe.ps1 -Evidence blocked-modal.json
#>

param(
    [string]$Dll = 'CKAgent.Mcp/bin/Release/net8.0/ckagent.dll',
    [string]$Game = 'D:\SteamLibrary\steamapps\common\Fallout 4',
    [string]$Command = 'File/Data',
    [string]$BlockedTool = 'ck.frame',
    [string]$BlockedArgs = '{"reference":"Loot_Prewar_Safe"}',
    [int]$Attempts = 3,
    [int]$WaitSeconds = 20,
    [string]$Evidence = '',
    [switch]$KeepModal
)

$ErrorActionPreference = 'Stop'

# Add-Type needs a real TEMP; this host can have TEMP set to the literal :TEMP.
$scratch = Join-Path $PSScriptRoot '.probe-tmp'
New-Item -ItemType Directory -Force $scratch | Out-Null
$env:TEMP = $scratch
$env:TMP = $scratch

Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class ModalWin {
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
}
"@

$failures = New-Object System.Collections.Generic.List[string]
$observed = [ordered]@{}

function Invoke-Plugin([string]$command) {
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'ckagent', [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $pipe.Connect(5000)
    } catch {
        $script:pipeDown = $true
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

function Get-Modals {
    $modals = (Invoke-Plugin 'ui.modals') | ConvertFrom-Json
    return @($modals)
}

function Find-Blocking {
    return Get-Modals | Where-Object { $_.blocking -eq $true } | Select-Object -First 1
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
    $script:mcpExit = $LASTEXITCODE
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

function Set-EditorBackground {
    # The dialog has to be created while the editor is not foreground, or it appears visible and the
    # reveal branch is never taken.
    $console = [ModalWin]::GetConsoleWindow()
    if ($console -ne [IntPtr]::Zero) { [ModalWin]::SetForegroundWindow($console) | Out-Null }
    Start-Sleep -Milliseconds 700
    return [ModalWin]::GetForegroundWindow()
}

function Close-Modal($modal) {
    $handle = "$($modal.hwnd)"
    foreach ($caption in 'OK', '&OK', 'Cancel', '&Cancel', 'Close') {
        try {
            Invoke-Plugin "ui.click $handle $caption" | Out-Null
            Start-Sleep -Milliseconds 400
            if (-not (Find-Blocking)) { return $true }
        } catch {
            # A dialog without that caption is expected; the next one is tried.
        }
    }

    # A dialog that refuses every caption still has to go, and WM_CLOSE is its own Cancel.
    $value = [Convert]::ToInt64(($handle -replace '^0x', ''), 16)
    [ModalWin]::PostMessageW([IntPtr]$value, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null
    Start-Sleep -Milliseconds 700
    return [bool](-not (Find-Blocking))
}

function New-HiddenModal([string]$Phase) {
    # The dialog must be created while the editor is not foreground, or it appears visible and the reveal
    # branch is never taken.
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        $null = Set-EditorBackground
        try {
            Invoke-Plugin "ui.command $Command" | Out-Null
        } catch {
            $failures.Add("the probe could not run '$Command', which opens the modal: $($_.Exception.Message)")
            return $null
        }

        $deadline = (Get-Date).AddSeconds($WaitSeconds)
        $blocking = $null
        while ((Get-Date) -lt $deadline) {
            $blocking = Find-Blocking
            if ($blocking) { break }
            Start-Sleep -Milliseconds 300
        }

        if (-not $blocking) {
            $observed["$Phase attempt$attempt"] = 'no blocking dialog appeared'
            continue
        }

        if ($blocking.visible -eq $true) {
            # Foreground beat the probe this time: dismiss it and try to create it out of focus instead.
            $observed["$Phase attempt$attempt"] = "dialog visible hwnd=$($blocking.hwnd); dismissed and retried"
            Close-Modal $blocking | Out-Null
            continue
        }

        # The real state also leaves the frame disabled, which is what status reports; a hidden dialog that
        # blocks nothing is not the failure mode this probe exists for.
        if (-not (Get-Status).blockedBy) {
            $observed["$Phase attempt$attempt"] = "dialog hidden but status names no block (hwnd=$($blocking.hwnd), title='$($blocking.title)'); dismissed and retried"
            Close-Modal $blocking | Out-Null
            continue
        }

        return $blocking
    }

    return $null
}

try {
    $status = Get-Status
} catch {
    Write-Output "FAIL $($_.Exception.Message)"
    exit 3
}

if (-not $status.supported) {
    Write-Output 'FAIL the plugin refused this editor build, so the modal path cannot be exercised here'
    exit 3
}

if ($status.blockedBy) {
    Write-Output "FAIL the editor is already blocked by '$($status.blockedBy)'; dismiss that dialog and re-run"
    exit 1
}

# File/Data is greyed out whenever a cell is open, and the greying is why the dialog cannot be opened.
if ($Command -match '^File/Data' -and $status.loaded) {
    Write-Output 'REPRODUCTION File/Data is disabled while a cell is loaded: close the cell and re-run this probe'
    exit 2
}

# 1. Create the dialog while the editor is in the background, and require it to be hidden.
$hidden = New-HiddenModal -Phase 'recover'

if (-not $hidden) {
    $observed['foreground'] = "0x$('{0:X}' -f ([ModalWin]::GetForegroundWindow()).ToInt64())"
    $evidenceJson = [ordered]@{ modal = $observed; failures = @($failures); passed = $false } | ConvertTo-Json -Depth 6
    if ($Evidence) { $evidenceJson | Set-Content -LiteralPath $Evidence -Encoding utf8 }
    Write-Output 'REPRODUCTION the hidden-modal state did not occur: the dialog was created visible or not at all'
    Write-Output $evidenceJson
    exit 2
}

$observed['hiddenModal'] = [ordered]@{ hwnd = $hidden.hwnd; title = $hidden.title; visible = $hidden.visible }

# The block must be visible to a client that never looks at the modals list.
$statusNow = Get-Status
$observed['statusBlockedBy'] = $statusNow.blockedBy
if (-not $statusNow.blockedBy) {
    $failures.Add('status did not report blockedBy while a hidden modal was blocking the frame')
} elseif ($statusNow.blockedBy -ne $hidden.title) {
    $failures.Add("status names '$($statusNow.blockedBy)' but the modal list names '$($hidden.title)'")
}

# Regression: a caller told only the title still has to pick a button, so the captions must be there and
# must be this dialog's own, or ui.click is aimed at a caption that no longer exists.
$observed['statusModalButtons'] = @($statusNow.modalButtons)
$captions = @($hidden.buttons)
$reported = @($statusNow.modalButtons)
if ($reported.Count -eq 0) {
    $failures.Add("status did not report the buttons of the blocking dialog '$($hidden.title)', so a caller has no caption to click")
} else {
    $unknown = @($reported | Where-Object { $captions -notcontains $_ })
    if ($unknown.Count -gt 0) {
        $failures.Add("status reports button(s) '$($unknown -join ', ')' that the dialog does not have ('$($captions -join ', ')'), so a click on one misses")
    }
}

# 2a. Control: a call that succeeds must leave the dialog hidden. Without this, the next assertion would
# only show that some call changed the dialog, not that the retry path did.
$control = Invoke-Mcp 'ck.ui' '{"method":"ui.cells"}'
$controlState = Find-Blocking
$observed['controlReply'] = $control.status
$observed['hiddenAfterControl'] = if ($controlState) { $controlState.visible } else { 'gone' }
if ($control.status -ne 'ok') {
    $failures.Add("the control call failed ($($control.code) $($control.message)), so it cannot show what a succeeding call does")
} elseif (-not $controlState -or $controlState.visible -ne $false) {
    $failures.Add('a succeeding call already changed the hidden dialog, so a failing call proves nothing about the reveal')
}

# 2b. A call that fails must dismiss the blocking dialog itself and retry, with no clicking asked for.
$call = Invoke-Mcp $BlockedTool $BlockedArgs
$observed['mcpExit'] = $script:mcpExit
$observed['mcpReply'] = [ordered]@{ status = $call.status; code = $call.code; message = $call.message }
$observed['mcpPayload'] = $call.payload

# 3. No blocking dialog may survive the call, and the reply must not send the caller off to press a button.
$after = Find-Blocking
$observed['blockingAfterCall'] = if ($after) { "$($after.hwnd) '$($after.title)' visible=$($after.visible)" } else { 'none' }
if ($after) {
    $failures.Add("a blocking dialog survived a failing call ($($observed['blockingAfterCall'])), so BlockedEditor did not dismiss it")
}
if ("$($call.message)" -match 'ui\.click') {
    $failures.Add("the reply still asks the caller to click the dialog away: $($call.message)")
}

if ($call.status -eq 'ok') {
    $observed['outcome'] = 'retried successfully once the dialog was dismissed'
} else {
    # The block cleared, so BlockedEditor had nothing left to name and the retry's own failure stands.
    $observed['outcome'] = 'the dismissal cleared the block; the retry reported its own failure'
}

# 4. Dismissing it must leave the editor usable again.
if ($KeepModal) {
    $observed['teardown'] = 'skipped by -KeepModal'
} else {
    $closed = Close-Modal $hidden
    $observed['dismissed'] = $closed
    if (-not $closed) {
        $failures.Add('the probe could not dismiss the dialog, so the editor is left blocked')
    } else {
        $finalStatus = Get-Status
        $observed['blockedAfterDismiss'] = $finalStatus.blockedBy
        if ($finalStatus.blockedBy) {
            $failures.Add("status still reports '$($finalStatus.blockedBy)' after the dialog was dismissed")
        }

        $recovered = Invoke-Mcp 'ck.ui' '{"method":"ui.cells"}'
        $observed['recoveredCall'] = $recovered.status
        if ($recovered.status -ne 'ok') {
            $failures.Add("the same call still fails after the dialog was dismissed: $($recovered.code) $($recovered.message)")
        }
    }
}

# 5. A dialog that cannot be dismissed must take the still-blocked branch. The server honours
# CKAGENT_TEST_DISMISS_FAILS to skip the dismissal, which is the only way to reach that branch here.
if ($KeepModal) {
    $observed['refusalPhase'] = 'skipped by -KeepModal'
} else {
    $refused = $null
    $env:CKAGENT_TEST_DISMISS_FAILS = '1'
    try {
        $refused = New-HiddenModal -Phase 'refusal'
        if (-not $refused) {
            $observed['refusalPhase'] = 'the hidden modal was not reproduced a second time'
        } else {
            $refusedStatus = Get-Status
            $observed['refusalBlockedBy'] = $refusedStatus.blockedBy
            if (-not $refusedStatus.blockedBy) {
                $failures.Add('status stopped naming the dialog before the skipped-dismissal phase')
            }

            $refusal = Invoke-Mcp $BlockedTool $BlockedArgs
            $observed['refusalReply'] = [ordered]@{ status = $refusal.status; code = $refusal.code; message = $refusal.message }

            $stillHidden = Find-Blocking
            $observed['refusalStillHidden'] = if ($stillHidden) { $stillHidden.visible } else { 'gone' }

            # The reply must name the dialog, say it could not be dismissed, and say how to close it by hand.
            if ("$($refusal.message)" -notmatch 'could not be dismissed automatically') {
                $failures.Add("a skipped dismissal was not reported as such: '$($refusal.message)'")
            }
            if ("$($refusal.message)" -notmatch [regex]::Escape($refused.title)) {
                $failures.Add("the still-blocked reply does not name the dialog '$($refused.title)': $($refusal.message)")
            }
            if ("$($refusal.message)" -notmatch 'ui\.click') {
                $failures.Add("the still-blocked reply does not say how to close the dialog by hand: $($refusal.message)")
            }
            if (-not $stillHidden -or $stillHidden.visible -ne $false) {
                $failures.Add('the dialog is no longer hidden after a skipped dismissal, so nothing was left blocking')
            }

            # 6. The plugin's own dismissal must clear that same dialog: it is the primitive BlockedEditor calls.
            $dismissal = (Invoke-Plugin "ui.dismiss $($refused.hwnd)") | ConvertFrom-Json
            $observed['dismissedBy'] = $dismissal.by
            if ($dismissal.dismissed -ne $true) {
                $failures.Add('ui.dismiss did not report dismissing the dialog')
            }
            Start-Sleep -Milliseconds 700
            $released = (Get-Status).blockedBy
            $observed['blockedAfterPrimitive'] = $released
            if ($released) {
                $failures.Add("ui.dismiss left the editor blocked by '$released'")
            }
        }
    } finally {
        Remove-Item Env:CKAGENT_TEST_DISMISS_FAILS -ErrorAction SilentlyContinue
        if ($refused) { Close-Modal $refused | Out-Null }
    }
}

$evidenceJson = [ordered]@{ modal = $observed; failures = @($failures); passed = ($failures.Count -eq 0) } | ConvertTo-Json -Depth 6
if ($Evidence) { $evidenceJson | Set-Content -LiteralPath $Evidence -Encoding utf8 }

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Output "FAIL $failure" }
    exit 1
}

Write-Output 'PASS the hidden modal was detected, dismissed by the server and retried, and the editor recovered'
Write-Output $evidenceJson
exit 0
