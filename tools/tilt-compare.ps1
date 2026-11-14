<#
.SYNOPSIS
Runs the aim probe in an interior cell and an exterior worldspace, and writes both readings into one evidence
file.

.DESCRIPTION
ck.aim's tilt, its level hold and ck.frame's fixed elevation should not depend on which kind of world the object
sits in: the arithmetic is about the camera and the target, not about the cell. That is easy to assume and hard
to see, because the two kinds of world are reached differently - an interior name lives under the Cell View's
`Interiors` entry, an exterior one under its worldspace - so they end up measured in separate runs and compared
by hand.

This runs tools/aim-probe.ps1 twice, once against each, passing the worldspace through when one is needed, and
writes a single file holding both probes' evidence plus a `comparison` list that puts the tilted and level
readings of each case side by side across the two worlds. It asserts the thing the comparison exists to show:
with the tilt the camera centres the target from either side, and with `level` it writes pitch 0 and reports the
cosine instead, in both worlds. A difference between the worlds therefore appears in the file rather than
needing two logs read together.

Each world's probe still makes its own assertions; this adds the cross-world reading on top, so a failure names
the world it came from.

Exit codes: 0 both worlds passed, 1 a cross-world comparison failed, 2 a world could not be prepared, 3 the
plugin pipe was unreachable.

.PARAMETER Dll
The built MCP server the probe drives. A relative path is resolved from the current directory.

.PARAMETER Game
The Fallout 4 install the editor is running from.

.PARAMETER InteriorCell
Interior cell for the interior half, loaded through the Cell View's `Interiors` entry.

.PARAMETER InteriorReference
Reference in that cell to aim at.

.PARAMETER ExteriorWorldspace
Cell View entry selected before the exterior cell is loaded.

.PARAMETER ExteriorCell
Exterior cell for the other half.

.PARAMETER ExteriorReference
Reference in that cell to aim at.

.PARAMETER Evidence
Where the one combined evidence file is written. Defaults to tilt-compare.json in the current directory.

.EXAMPLE
# from the repository root, with the server built and the editor running
./tools/tilt-compare.ps1 -Evidence tilt-compare.json
#>

param(
    [string]$Dll = 'CKAgent.Mcp/bin/Release/net8.0/ckagent.dll',
    [string]$Game = 'D:\SteamLibrary\steamapps\common\Fallout 4',
    [string]$InteriorCell = 'AndrewStation01',
    [string]$InteriorReference = 'Loot_Prewar_Safe',
    [string]$ExteriorWorldspace = 'DiamondCity',
    [string]$ExteriorCell = 'DiamondCityOrigin',
    [string]$ExteriorReference = 'DiamondStandsMayor01',
    [string]$Evidence = 'tilt-compare.json'
)

$ErrorActionPreference = 'Stop'

$probe = Join-Path $PSScriptRoot 'aim-probe.ps1'
if (-not (Test-Path -LiteralPath $probe)) {
    Write-Output "FAIL the aim probe is not beside this script: $probe"
    exit 2
}

# The probe resolves the server from its own working directory, so a relative path is taken from the project.
$project = Split-Path $PSScriptRoot -Parent
if (-not [System.IO.Path]::IsPathRooted($Dll)) {
    $fromProject = Join-Path $project $Dll
    if (Test-Path -LiteralPath $fromProject) { $Dll = $fromProject }
    elseif (Test-Path -LiteralPath $Dll) { $Dll = (Resolve-Path -LiteralPath $Dll).Path }
}

if (-not (Test-Path -LiteralPath $Dll)) {
    Write-Output "FAIL the MCP server is not built where this script looks: $Dll"
    exit 2
}

# The probe rewrites TEMP to a scratch directory of its own, so a stale TEMP on this host cannot reach here.
$scratch = Join-Path $PSScriptRoot '.probe-tmp'
New-Item -ItemType Directory -Force $scratch | Out-Null

$failures = New-Object System.Collections.Generic.List[string]

$worlds = @(
    @{ name = 'interior'; cell = $InteriorCell; worldspace = ''; reference = $InteriorReference },
    @{ name = 'exterior'; cell = $ExteriorCell; worldspace = $ExteriorWorldspace; reference = $ExteriorReference }
)

$reports = @()
$worstExit = 0

foreach ($world in $worlds) {
    $partFile = Join-Path $scratch ("tilt-$($world.name).json")
    if (Test-Path -LiteralPath $partFile) { Remove-Item -LiteralPath $partFile -Force }

    $probeArgs = @(
        '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $probe,
        '-Dll', $Dll, '-Game', $Game,
        '-Cell', $world.cell, '-Reference', $world.reference,
        '-Evidence', $partFile
    )
    if ($world.worldspace) { $probeArgs += @('-Worldspace', $world.worldspace) }

    $lines = @()
    & powershell @probeArgs 2>&1 | ForEach-Object { $lines += "$_" }
    $code = $LASTEXITCODE

    $part = $null
    if (Test-Path -LiteralPath $partFile) {
        $part = Get-Content -LiteralPath $partFile -Raw | ConvertFrom-Json
    }

    $verdict = @($lines) | Where-Object { "$_".StartsWith('FAIL') -or "$_".StartsWith('PASS') } | Select-Object -First 1
    Write-Output "$($world.name) ($($world.cell)): exit $code - $verdict"

    # A world that could not be prepared or reached outranks an assertion that failed, so exit codes add up.
    if ($code -eq 3 -or $code -eq 2) { $worstExit = [math]::Max($worstExit, 2) }
    elseif ($code -ne 0) { $worstExit = [math]::Max($worstExit, 1) }

    $reports += [ordered]@{
        world = $world.name
        cell = $world.cell
        worldspace = $world.worldspace
        reference = $world.reference
        exit = $code
        verdict = "$verdict"
        evidence = $part
    }
}

# The comparison is the point of the file: the same three heights, tilted and level, in each world.
$comparison = @()
$pairs = @(
    @{ case = 'above'; tilted = 'above'; level = 'level-above' },
    @{ case = 'below'; tilted = 'below'; level = 'level-below' },
    @{ case = 'level'; tilted = 'level'; level = 'level-level' }
)

foreach ($report in $reports) {
    if (-not $report.evidence -or -not $report.evidence.aim) {
        $failures.Add("$($report.world): the probe wrote no aim evidence, so there is nothing to compare")
        continue
    }

    foreach ($pair in $pairs) {
        $tilted = $report.evidence.aim.($pair.tilted)
        $level = $report.evidence.aim.($pair.level)
        if (-not $tilted -or -not $level) {
            $failures.Add("$($report.world): the $($pair.case) readings are missing from the probe's evidence")
            continue
        }

        $comparison += [ordered]@{
            world = $report.world
            case = $pair.case
            tiltPitch = $tilted.pitch
            levelPitch = $level.pitch
            tiltAim = $tilted.aim
            levelAim = $level.aim
            levelYawAim = $level.yawAim
            levelCamera = $level.camera
        }

        # What the comparison must show in every world: level refuses the tilt and reports the cosine instead.
        if ([math]::Abs([double]$level.pitch) -gt 1e-6) {
            $failures.Add("$($report.world) $($pair.case): level wrote pitch $($level.pitch) instead of 0")
        }
        if ($pair.case -ne 'level' -and [double]$level.aim -ge 0.99) {
            $failures.Add("$($report.world) $($pair.case): level reports aim $($level.aim) from off the target's height, so it tilted")
        }
        if ([math]::Abs([double]$tilted.aim - 1.0) -gt 0.01) {
            $failures.Add("$($report.world) $($pair.case): the tilted aim is $($tilted.aim) rather than centring the target")
        }
    }
}

# Both worlds have to have produced the same cases, or the comparison is between different things.
$caseSets = @($reports | ForEach-Object { if ($_.evidence -and $_.evidence.aim) { "$(@($pairs | ForEach-Object { $_.case }) -join ',')" } } | Select-Object -Unique)
if ($caseSets.Count -gt 1) {
    $failures.Add('the worlds did not produce the same case set, so the comparison is not like for like')
}

$result = [ordered]@{
    generatedUtc = (Get-Date).ToUniversalTime().ToString('o')
    probe = 'tools/aim-probe.ps1'
    worlds = $reports
    comparison = $comparison
    failures = @($failures)
    passed = ($failures.Count -eq 0)
}

$json = $result | ConvertTo-Json -Depth 10
Set-Content -LiteralPath $Evidence -Value $json -Encoding utf8

foreach ($failure in $failures) { Write-Output "FAIL $failure" }

if ($failures.Count -eq 0) {
    Write-Output "PASS both worlds give the same tilt and level comparison: $Evidence"
}

Write-Output $json

if ($failures.Count -gt 0) { exit ([math]::Max($worstExit, 1)) }
exit $worstExit
