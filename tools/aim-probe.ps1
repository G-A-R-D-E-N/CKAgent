<#
.SYNOPSIS
Asserts ck.aim keeps the camera where it stands and centres the target, whether it is level with the
camera, above or below it, or directly overhead.

.DESCRIPTION
Rotating in place is the whole contract of ck.aim, and it has two ways to break that no single reading
catches: the camera drifting while it turns, and the tilt being computed from somewhere other than the
camera's real position - which leaves the object off centre and, for a target straight above, used to be
refused outright. This probe drives the MCP server for every case and then re-reads the camera, so the
report and the pose are checked separately:

  1. the cell is loaded through ck.frame, which also gives the target's own position and form id, so the
     probe never assumes where the object is;
  2. for each case the camera is placed on the pipe, ck.aim is called over MCP, and the reply must be
     aligned with aim 1.0 while the camera stays where it was put;
  3. the tilt is compared against the angle to the target computed here from the two positions, and the
     view row is re-read afterwards and dotted with that direction, so a pose the editor rejected cannot
     pass on the reply alone;
  4. one case asks for a standoff, where the camera is expected to move to exactly that distance and the
     tilt to be computed from the new position rather than the old one.

The aim direction and the view row are both read back through the plugin, which is the same source the
server reports from - so this checks behaviour and arithmetic, not the plugin's decode of the scene camera.
That decode belongs to tools/ck-surface-probe/camera-tool-cross-check.ps1.

Exit codes: 0 pass, 1 assertion failed, 2 the cell or reference could not be prepared, 3 the plugin pipe is
unreachable.

.PARAMETER Cell
Cell to load for the run. Its reference named by -Reference must exist there.

.PARAMETER Reference
Reference EditorID, base object EditorID or form id to aim at. The probe aims by the form id ck.frame
reports, so a name that matches several references still pins one.

.PARAMETER Distance
How far to the side the camera is placed for the level, above and overhead cases.

.PARAMETER Tolerance
Allowed error on the reported aim.

.EXAMPLE
# from the repository root, with the server built and the editor running
./tools/aim-probe.ps1 -Evidence aim-probe.json
#>

param(
    [string]$Dll = 'CKAgent.Mcp/bin/Release/net8.0/ckagent.dll',
    [string]$Game = 'D:\SteamLibrary\steamapps\common\Fallout 4',
    [string]$Cell = 'AndrewStation01',
    [string]$Reference = 'Loot_Prewar_Safe',
    [double]$Distance = 512,
    [double]$Tolerance = 1e-3,
    [string]$Evidence = ''
)

$ErrorActionPreference = 'Stop'

# Add-Type and the native helper both need a real TEMP; this host can have TEMP set to the literal :TEMP.
$scratch = Join-Path $PSScriptRoot '.probe-tmp'
New-Item -ItemType Directory -Force $scratch | Out-Null
$env:TEMP = $scratch
$env:TMP = $scratch

$failures = New-Object System.Collections.Generic.List[string]
$observed = [ordered]@{ cell = $Cell; reference = $Reference }
$invariant = [System.Globalization.CultureInfo]::InvariantCulture

function Format-Number([double]$value) {
    return $value.ToString('0.######', $invariant)
}

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

function Get-Camera {
    return (Invoke-Plugin 'camera get') | ConvertFrom-Json
}

# 1. Load the cell here rather than through ck.frame's own cell argument: that argument waits for a new
# cell and times out when the requested one is already open, which is the state a rerun starts from.
function Get-LoadedCell {
    try {
        $status = (Invoke-Plugin 'status') | ConvertFrom-Json
        if (-not $status.loaded) { return '' }
        return "$(((Invoke-Plugin 'cell') | ConvertFrom-Json).editorId)"
    } catch {
        return ''
    }
}

$loaded = Get-LoadedCell
if ($loaded -ne $Cell) {
    Invoke-Plugin "ui.cellload $Cell" | Out-Null
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline -and (Get-LoadedCell) -ne $Cell) { Start-Sleep -Milliseconds 400 }
    $loaded = Get-LoadedCell
}

$observed['loadedCell'] = $loaded
if ($loaded -ne $Cell) {
    Write-Output "FAIL the editor did not open '$Cell'; it reports '$loaded'"
    exit 2
}

# The target's position and form id come from the tool that had to find it, so neither is assumed here.
$frame = Invoke-Mcp 'ck.frame' (@{ reference = $Reference; distance = 256 } | ConvertTo-Json -Compress)
if ($frame.status -ne 'ok') {
    Write-Output "FAIL ck.frame could not prepare $Cell / $Reference ($($frame.code)): $($frame.message)"
    exit 2
}

$framed = $frame.payload | ConvertFrom-Json
$target = @([double]$framed.target[0], [double]$framed.target[1], [double]$framed.target[2])
$targetId = "$($framed.formId)"
$observed['target'] = "$($target[0]) $($target[1]) $($target[2])"
$observed['targetId'] = $targetId
$observed['matches'] = $framed.matches

# 2. The three in-place cases. Offsets are from the target, so the expectations do not depend on the cell.
$cases = @(
    @{ name = 'level';          dx = $Distance; dy = 0.0; dz = 0.0 },
    @{ name = 'above';          dx = $Distance; dy = 0.0; dz = 300.0 },
    @{ name = 'directly-above'; dx = 0.0;       dy = 0.0; dz = 400.0 },
    @{ name = 'below';          dx = $Distance; dy = 0.0; dz = -300.0 }
)

foreach ($case in $cases) {
    $name = $case.name
    $px = $target[0] + $case.dx
    $py = $target[1] + $case.dy
    $pz = $target[2] + $case.dz

    $placed = "camera set $(Format-Number $px) $(Format-Number $py) $(Format-Number $pz) 0 0"
    Invoke-Plugin $placed | Out-Null
    $before = Get-Camera

    $reply = Invoke-Mcp 'ck.aim' (@{ reference = $targetId } | ConvertTo-Json -Compress)
    $record = [ordered]@{
        placed   = "$([math]::Round($before.x, 4)) $([math]::Round($before.y, 4)) $([math]::Round($before.z, 4))"
        status   = $reply.status
        code     = $reply.code
    }

    if ($reply.status -ne 'ok') {
        $failures.Add("$name`: ck.aim refused ($($reply.code)): $($reply.message)")
        $observed[$name] = $record
        continue
    }

    $payload = $reply.payload | ConvertFrom-Json
    $record['aim'] = $payload.aim
    $record['aligned'] = $payload.aligned
    $record['pitch'] = $payload.pitch
    $record['yaw'] = $payload.yaw
    $record['camera'] = "$($payload.camera[0]) $($payload.camera[1]) $($payload.camera[2])"

    if ([math]::Abs([double]$payload.aim - 1.0) -gt $Tolerance) {
        $failures.Add("$name`: aim is $($payload.aim), not 1: the target is not centred")
    }
    if ($payload.aligned -ne $true) {
        $failures.Add("$name`: the reply says aligned=$($payload.aligned) while aim is $($payload.aim)")
    }

    # Rotating in place is the contract, so the camera must not have moved at all.
    foreach ($axis in @('x', 'y', 'z')) {
        $slot = @{ x = 0; y = 1; z = 2 }[$axis]
        $drift = [math]::Abs([double]$payload.camera[$slot] - [double]$before.$axis)
        if ($drift -gt 0.01) {
            $failures.Add("$name`: the camera moved $([math]::Round($drift, 4)) on $axis, but aim must rotate in place")
        }
    }

    # The tilt must be the angle between the camera and the target, not an angle to somewhere else.
    $dx = $target[0] - [double]$before.x
    $dy = $target[1] - [double]$before.y
    $dz = $target[2] - [double]$before.z
    $flat = [math]::Sqrt($dx * $dx + $dy * $dy)
    $expectedPitch = if ($flat -gt 1e-3) { [math]::Atan2($dz, $flat) } elseif ($dz -gt 0) { [math]::PI / 2 } else { -[math]::PI / 2 }
    $record['expectedPitch'] = [math]::Round($expectedPitch, 4)
    if ([math]::Abs([double]$payload.pitch - $expectedPitch) -gt 2e-3) {
        $failures.Add("$name`: pitch is $($payload.pitch) but the camera-target angle is $([math]::Round($expectedPitch, 4))")
    }

    # Re-read the view row and dot it with the direction to the target: the reply is not the only witness.
    $after = Get-Camera
    $length = [math]::Sqrt($dx * $dx + $dy * $dy + $dz * $dz)
    if ($length -lt 1e-3) {
        $failures.Add("$name`: the camera ended up on the target, so the dot product says nothing")
    } else {
        $dot = (([double]$after.forward[0] * $dx) + ([double]$after.forward[1] * $dy) + ([double]$after.forward[2] * $dz)) / $length
        $record['forwardDot'] = [math]::Round($dot, 5)
        if ($dot -lt 0.9999) {
            $failures.Add("$name`: the view row dots the target at $([math]::Round($dot, 5)), so the object is off centre")
        }
    }

    $observed[$name] = $record
}

# 3. A standoff moves the camera, so the tilt must come from where it lands rather than from where it was.
$standoff = 256
Invoke-Plugin ("camera set $(Format-Number ($target[0] + 300)) $(Format-Number ($target[1] - 200)) $(Format-Number ($target[2] + 200)) 0 0") | Out-Null
$reply = Invoke-Mcp 'ck.aim' (@{ reference = $targetId; distance = $standoff } | ConvertTo-Json -Compress)
$record = [ordered]@{ status = $reply.status; code = $reply.code }

if ($reply.status -ne 'ok') {
    $failures.Add("standoff: ck.aim refused ($($reply.code)): $($reply.message)")
} else {
    $payload = $reply.payload | ConvertFrom-Json
    $record['aim'] = $payload.aim
    $record['pitch'] = $payload.pitch
    $record['camera'] = "$($payload.camera[0]) $($payload.camera[1]) $($payload.camera[2])"

    $hx = [double]$payload.camera[0] - $target[0]
    $hy = [double]$payload.camera[1] - $target[1]
    $settled = [math]::Sqrt($hx * $hx + $hy * $hy)
    $record['horizontalDistance'] = [math]::Round($settled, 4)
    if ([math]::Abs($settled - $standoff) -gt 1.0) {
        $failures.Add("standoff: the camera settled $([math]::Round($settled, 4)) away, not $standoff")
    }
    if ([math]::Abs([double]$payload.aim - 1.0) -gt $Tolerance) {
        $failures.Add("standoff: aim is $($payload.aim), so the tilt came from somewhere other than the final position")
    }

    $expectedPitch = [math]::Atan2($target[2] - [double]$payload.camera[2], $settled)
    $record['expectedPitch'] = [math]::Round($expectedPitch, 4)
    if ([math]::Abs([double]$payload.pitch - $expectedPitch) -gt 2e-3) {
        $failures.Add("standoff: pitch is $($payload.pitch) but the settled camera-target angle is $([math]::Round($expectedPitch, 4))")
    }
}

$observed['standoff'] = $record

$evidenceJson = [ordered]@{ aim = $observed; failures = @($failures); passed = ($failures.Count -eq 0) } | ConvertTo-Json -Depth 6
if ($Evidence) { $evidenceJson | Set-Content -LiteralPath $Evidence -Encoding utf8 }

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Output "FAIL $failure" }
    exit 1
}

Write-Output 'PASS ck.aim kept the camera in place and centred the target level, above, below, overhead and at a standoff'
Write-Output $evidenceJson
exit 0
