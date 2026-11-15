<#
.SYNOPSIS
Installs a built CKAgent.dll where CKPE loads it, and optionally restarts the editor through ckpe_loader.exe.

.DESCRIPTION
Adopting a build used to be a hand copy into <game>\CKPEPlugins while the editor was closed, and a hand restart
afterwards. This does both, reports what it placed (path, size, hash) so the installed file is provable, and names the
cause when the copy cannot happen - a running editor holds the old DLL and Windows refuses to replace it.

The build itself is not guessed: pass -PluginDll, or leave it out to take the newest CKAgent.dll under the repository's
own build directory. Nothing is built here unless -Build is passed.

.EXAMPLE
./tools/install-ckagent.ps1 -Game 'D:\SteamLibrary\steamapps\common\Fallout 4' -RestartEditor
#>
param(
    [Parameter(Mandatory = $true)][string]$Game,
    [string]$PluginDll = '',
    [switch]$RestartEditor,
    [int]$WaitSeconds = 120
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Game -PathType Container)) { throw "the game directory $Game does not exist" }

$gameRoot = (Resolve-Path -LiteralPath $Game).Path
$plugins = Join-Path $gameRoot 'CKPEPlugins'

if (-not (Test-Path -LiteralPath $plugins -PathType Container)) {
    throw "$plugins does not exist: install CKPE first, because CKAgent.dll is a CKPE plug-in and has nowhere to load from"
}

$loader = Join-Path $gameRoot 'ckpe_loader.exe'

# Constraint: the loader is proved before the editor is stopped, so a restart cannot leave the operator with neither.
if ($RestartEditor -and -not (Test-Path -LiteralPath $loader)) {
    throw "$loader is not in $gameRoot, so the editor cannot be restarted through it; start the editor yourself and call ck.status"
}

if ([string]::IsNullOrWhiteSpace($PluginDll)) {
    $repoBuild = Join-Path (Split-Path -Parent $PSScriptRoot) 'build'
    $built = @()

    if (Test-Path -LiteralPath $repoBuild -PathType Container) {
        # Constraint: CMake names the target CKAgentPlugin.dll and renames it only when CKPE_PLUGIN_DIR is set, so an
        # install has to accept the build output under either name.
        $built = @(Get-ChildItem -LiteralPath $repoBuild -Recurse -File |
            Where-Object { $_.Name -eq 'CKAgent.dll' -or $_.Name -eq 'CKAgentPlugin.dll' } |
            Sort-Object LastWriteTime -Descending)
    }

    if ($built.Count -eq 0) {
        throw "no built CKAgent.dll was found under $repoBuild; build the plugin, or pass -PluginDll with the file to install"
    }

    $PluginDll = $built[0].FullName
}

if (-not (Test-Path -LiteralPath $PluginDll -PathType Leaf)) { throw "the plug-in to install, $PluginDll, does not exist" }

$source = (Resolve-Path -LiteralPath $PluginDll).Path
$target = Join-Path $plugins 'CKAgent.dll'
$sourceHash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash

$replaced = Test-Path -LiteralPath $target
$before = $null

# Constraint: a target another process holds open cannot even be hashed, and that is the case this script has to name,
# so the previous hash is best effort and the copy below reports the lock.
if ($replaced) {
    try { $before = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash } catch { $before = $null }
}

try {
    Copy-Item -LiteralPath $source -Destination $target -Force
}
catch {
    # Constraint: a held DLL is the failure the operator hits, so it is named with what to do about it.
    throw "the plug-in could not be replaced at $target (close CreationKit.exe and retry): $($_.Exception.Message)"
}

$after = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash

if ($after -ne $sourceHash) { throw "$target does not carry the plug-in that was installed ($after, expected $sourceHash)" }

$restarted = $false

if ($RestartEditor) {
    $running = @(Get-Process -Name CreationKit -ErrorAction SilentlyContinue)

    foreach ($editor in $running) {
        Stop-Process -Id $editor.Id -Force
    }

    if ($running.Count -gt 0) { Start-Sleep -Seconds 3 }

    Start-Process -FilePath $loader -WorkingDirectory $gameRoot | Out-Null

    $deadline = (Get-Date).AddSeconds($WaitSeconds)
    $frame = $null

    # Constraint: a started loader is not a loaded plug-in, so the editor frame is waited for instead of assumed.
    while ((Get-Date) -lt $deadline) {
        $frame = @(Get-Process -Name CreationKit -ErrorAction SilentlyContinue |
            Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1)

        if ($frame.Count -gt 0) { break }

        Start-Sleep -Milliseconds 500
    }

    if ($frame.Count -eq 0) {
        throw "$loader started but the editor had no window within $WaitSeconds s; wait for it and call ck.status before a navmesh run"
    }

    $restarted = $true
}

[pscustomobject]@{
    plugin = $target
    source = $source
    sha256 = $after
    bytes = (Get-Item -LiteralPath $target).Length
    replaced = $replaced
    previousSha256 = $before
    restarted = $restarted
    loader = if ($RestartEditor) { $loader } else { $null }
} | ConvertTo-Json -Compress
