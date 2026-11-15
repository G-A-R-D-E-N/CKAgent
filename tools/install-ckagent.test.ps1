<#
.SYNOPSIS
Drives tools/install-ckagent.ps1 against a throwaway game directory, so adopting a build is tested without an editor.

.DESCRIPTION
The install is the one step a user still had to do by hand before a navmesh run, and the failure that costs the most
time is a silent one: an old CKAgent.dll left in place because the editor was holding it. These cases run the real
script and require the copy to be proved, the lock to be a named failure, and a missing file to be refused.
#>
$ErrorActionPreference = 'Stop'

$install = Join-Path $PSScriptRoot 'install-ckagent.ps1'
$failed = New-Object System.Collections.Generic.List[string]
$observed = [ordered]@{}

$root = Join-Path ([System.IO.Path]::GetTempPath()) ('ckagent-install-' + [guid]::NewGuid().ToString('N'))
$game = Join-Path $root 'game'

try {
    New-Item -ItemType Directory -Force -Path (Join-Path $game 'CKPEPlugins') | Out-Null
    Set-Content -LiteralPath (Join-Path $game 'CreationKit.exe') -Value 'not the editor'

    $dll = Join-Path $root 'CKAgent.dll'
    Set-Content -LiteralPath $dll -Value 'first build'

    # 1. A built plug-in is placed, and the answer proves which bytes were placed.
    $reply = & $install -Game $game -PluginDll $dll | ConvertFrom-Json
    $expected = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash

    $observed['installed'] = $reply.plugin

    if (-not (Test-Path -LiteralPath $reply.plugin)) {
        $failed.Add('the install did not put a plugin in CKPEPlugins')
    } elseif ($reply.sha256 -ne $expected) {
        $failed.Add('the install reported a hash for a file it did not place')
    }

    if ($reply.replaced) { $failed.Add('the install claimed to replace a plug-in that was not there') }

    # 2. A second install replaces it and says so, so an update is provable rather than assumed.
    Set-Content -LiteralPath $dll -Value 'second build'
    $again = & $install -Game $game -PluginDll $dll | ConvertFrom-Json

    if (-not $again.replaced) { $failed.Add('the install did not report replacing the plug-in it overwrote') }
    if ($again.sha256 -ne (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash) {
        $failed.Add('the replaced plug-in is not the build that was handed in')
    }

    # 3. A running editor holds the DLL; that refusal must name the cause instead of passing silently.
    $lock = [System.IO.File]::Open($again.plugin, 'Open', 'Read', 'None')
    try {
        & $install -Game $game -PluginDll $dll | Out-Null
        $failed.Add('the install reported success over a plug-in another process held open')
    }
    catch {
        if ("$($_.Exception.Message)" -notmatch 'close CreationKit') {
            $failed.Add("a locked plug-in failed with an unnamed error: $($_.Exception.Message)")
        }
    }
    finally {
        $lock.Dispose()
    }

    # 4. A missing plug-in is refused rather than installed.
    try {
        & $install -Game $game -PluginDll (Join-Path $root 'missing.dll') | Out-Null
        $failed.Add('the install accepted a plug-in that does not exist')
    }
    catch {
        if ("$($_.Exception.Message)" -notmatch 'does not exist') {
            $failed.Add("a missing plug-in failed with an unnamed error: $($_.Exception.Message)")
        }
    }

    # 5. A restart cannot start the loader when it is absent, and says so before anything is stopped.
    try {
        & $install -Game $game -PluginDll $dll -RestartEditor | Out-Null
        $failed.Add('the install promised a restart with no ckpe_loader.exe in the game directory')
    }
    catch {
        if ("$($_.Exception.Message)" -notmatch 'ckpe_loader') {
            $failed.Add("a missing loader failed with an unnamed error: $($_.Exception.Message)")
        }
    }
}
finally {
    Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}

$report = [pscustomobject]@{ checks = 5; observed = $observed; failures = $failed }
$report | ConvertTo-Json -Depth 4

if ($failed.Count -gt 0) {
    Write-Host "install checks failed: $($failed -join '; ')"
    exit 1
}

exit 0
