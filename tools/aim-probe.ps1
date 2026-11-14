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
  4. three cases ask for a standoff - from above the target, from below it, and level with it - where the
     camera must move to exactly that distance, the tilt must come from the new position rather than the
     old one, and its sign must follow which side the camera stands on;
  5. four cases ask for level, which trades centring for a horizon: pitch must be written as 0 and the
     view row must come back flat, the heading must still reach the target (yawAim 1), and the aim
     reported must drop to the cosine of the target's elevation rather than claiming a centre it did not
     take - from above, from below, level, and level beside a standoff;
  6. one case aims at two references in a single call, which must rotate in place for both, report each
     step's own tilt, finish on the last target, and refuse a standoff that would move the camera;
  7. next, ck.frame poses the camera as well as aims it, so its centring is checked the same way: the
     reported aim must be 1.0, the camera must stand exactly the reported distance from the box centre
     it framed, and the view row re-read afterwards must dot that centre - not the object's position,
     which differs from it whenever the bounding box is offset from the reference's origin;
  8. that frame runs twice, at the fitted distance and at three times it, because its tilt is a fixed 30
     degrees: the height above the centre must be half the distance and the flat part cos 30 of it at
     both, which is the only thing that would show a tilt taken from the other standoff, since a pose
     derived from a wrong pitch still centres the box and reports aim 1.

The aim direction and the view row are both read back through the plugin, which is the same source the
server reports from - so this checks behaviour and arithmetic, not the plugin's decode of the scene camera.
That decode belongs to tools/ck-surface-probe/camera-tool-cross-check.ps1.

Exit codes: 0 pass, 1 assertion failed, 2 the cell or reference could not be prepared, 3 the plugin pipe is
unreachable.

.PARAMETER Cell
Cell to load for the run. Its reference named by -Reference must exist there.

.PARAMETER Worldspace
Cell View entry to select before loading, for a cell that is not an interior one. Without it the probe selects
`Interiors` when the first load is refused, because that is where an interior cell name lives.

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
    [string]$Worldspace = '',
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
    $pipe = New-Object System.IO.Pipes.NamedPipeClientStream('.', $(if ($env:CKAGENT_LOW_PIPE -eq '1') { 'ckagent-low' } else { 'ckagent' }), [System.IO.Pipes.PipeDirection]::InOut)
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

# The Cell View lists one collection at a time, so a name only resolves once the matching entry is selected.
# Its combo is rebuilt while a worldspace is being shown, so both the entry and the rows behind it are retried.
function Select-CellViewCollection([string]$name) {
    $deadline = (Get-Date).AddSeconds(20)
    while ((Get-Date) -lt $deadline) {
        # The window list has to be a variable first: an array piped straight into Where-Object passes as one
        # item, and every property read then returns an array, so the title filter matches nothing.
        $windows = (Invoke-Plugin 'ui.windows') | ConvertFrom-Json
        $pane = @($windows) | Where-Object { $_.title -eq 'Cell View' } | Select-Object -First 1
        if ($pane) {
            $tree = (Invoke-Plugin "ui.tree $($pane.hwnd) 3 40") | ConvertFrom-Json
            $combo = @($tree.children) | Where-Object { $_.class -eq 'ComboBox' } | Select-Object -First 1
            if ($combo -and $combo.items) {
                $rows = @($combo.items.rows)
                for ($i = 0; $i -lt $rows.Count; $i++) {
                    if ("$($rows[$i])".Trim() -eq $name) {
                        # Constraint: selecting the entry already showing leaves the list stale, and a stale list
                        # refuses the cell name, so the combo is stepped through another entry and back.
                        $away = if ($i -eq 0) { 1 } else { 0 }
                        if ($away -lt $rows.Count) {
                            try { Invoke-Plugin "ui.select $($combo.hwnd) $away" | Out-Null } catch { }
                            Start-Sleep -Milliseconds 1200
                        }

                        Invoke-Plugin "ui.select $($combo.hwnd) $i" | Out-Null
                        Start-Sleep -Milliseconds 2500
                        return $true
                    }
                }
            }
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

# A load is refused rather than queued while the list behind a fresh worldspace is still filling, so it is
# retried for as long as a big worldspace can take to list its cells.
function Open-Cell([string]$name) {
    for ($try = 0; $try -lt 3; $try++) {
        try {
            Invoke-Plugin "ui.cellload $name" | Out-Null
        } catch {
        }

        $deadline = (Get-Date).AddSeconds(12)
        while ((Get-Date) -lt $deadline -and (Get-LoadedCell) -ne $name) { Start-Sleep -Milliseconds 400 }
        if ((Get-LoadedCell) -eq $name) { return $true }
        Start-Sleep -Milliseconds 1500
    }

    return $false
}

$loaded = Get-LoadedCell
if ($loaded -ne $Cell) {
    if ($Worldspace -and -not (Select-CellViewCollection $Worldspace)) {
        Write-Output "FAIL the Cell View has no '$Worldspace' entry to select"
        exit 2
    }

    $opened = Open-Cell $Cell
    if (-not $opened -and -not $Worldspace) {
        # An interior cell is listed only while Interiors is selected, which a previous run can leave behind.
        if (Select-CellViewCollection 'Interiors') { $opened = Open-Cell $Cell }
    }

    $loaded = Get-LoadedCell
    if (-not $opened) {
        Write-Output "FAIL the editor did not open '$Cell'; it reports '$loaded'. The Cell View lists one collection at a time, so pass -Worldspace when the cell is an exterior one."
        exit 2
    }
}

$observed['loadedCell'] = $loaded

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

# 3. A standoff moves the camera, so the tilt must come from where it lands rather than from where it was, and
# the tilt has a sign: from above it must point down, from below up, and level it must not tilt at all.
$standoff = 256
$standoffCases = @(
    @{ name = 'standoff-from-above'; dx = 300.0; dy = -200.0; dz = 200.0;  sign = -1 },
    @{ name = 'standoff-from-below'; dx = 300.0; dy = -200.0; dz = -200.0; sign = 1 },
    @{ name = 'standoff-level';      dx = 300.0; dy = -200.0; dz = 0.0;    sign = 0 }
)

foreach ($case in $standoffCases) {
    $name = $case.name
    $placed = "camera set $(Format-Number ($target[0] + $case.dx)) $(Format-Number ($target[1] + $case.dy)) $(Format-Number ($target[2] + $case.dz)) 0 0"
    Invoke-Plugin $placed | Out-Null
    $before = Get-Camera

    $reply = Invoke-Mcp 'ck.aim' (@{ reference = $targetId; distance = $standoff } | ConvertTo-Json -Compress)
    $record = [ordered]@{
        placed = "$([math]::Round($before.x, 4)) $([math]::Round($before.y, 4)) $([math]::Round($before.z, 4))"
        status = $reply.status
        code   = $reply.code
    }

    if ($reply.status -ne 'ok') {
        $failures.Add("$name`: ck.aim refused ($($reply.code)): $($reply.message)")
        $observed[$name] = $record
        continue
    }

    $payload = $reply.payload | ConvertFrom-Json
    $record['aim'] = $payload.aim
    $record['pitch'] = $payload.pitch
    $record['camera'] = "$($payload.camera[0]) $($payload.camera[1]) $($payload.camera[2])"

    $hx = [double]$payload.camera[0] - $target[0]
    $hy = [double]$payload.camera[1] - $target[1]
    $settled = [math]::Sqrt($hx * $hx + $hy * $hy)
    $record['horizontalDistance'] = [math]::Round($settled, 4)
    if ([math]::Abs($settled - $standoff) -gt 1.0) {
        $failures.Add("$name`: the camera settled $([math]::Round($settled, 4)) away, not $standoff")
    }
    if ([math]::Abs([double]$payload.aim - 1.0) -gt $Tolerance) {
        $failures.Add("$name`: aim is $($payload.aim), so the tilt came from somewhere other than the final position")
    }

    # The standoff moves the camera sideways, so a tilt taken from the old distance would be visibly shallow here.
    $expectedPitch = [math]::Atan2($target[2] - [double]$payload.camera[2], $settled)
    $record['expectedPitch'] = [math]::Round($expectedPitch, 4)
    if ([math]::Abs([double]$payload.pitch - $expectedPitch) -gt 2e-3) {
        $failures.Add("$name`: pitch is $($payload.pitch) but the settled camera-target angle is $([math]::Round($expectedPitch, 4))")
    }

    # A tilt that is right in size but backwards points the camera at the sky, which aim 1.0 would not catch.
    $tilt = [double]$payload.pitch
    $wrongWay = ($case.sign -gt 0 -and $tilt -lt 0.01) -or ($case.sign -lt 0 -and $tilt -gt -0.01) -or
        ($case.sign -eq 0 -and [math]::Abs($tilt) -gt 0.01)
    if ($wrongWay) {
        $failures.Add("$name`: pitch is $tilt but a camera $(if ($case.sign -gt 0) { 'below' } elseif ($case.sign -lt 0) { 'above' } else { 'level with' }) the target must tilt $(if ($case.sign -gt 0) { 'up' } elseif ($case.sign -lt 0) { 'down' } else { 'not at all' })")
    }

    # The reply is not the only witness: the view row is re-read and dotted with the target as well.
    $after = Get-Camera
    $dx = $target[0] - [double]$payload.camera[0]
    $dy = $target[1] - [double]$payload.camera[1]
    $dz = $target[2] - [double]$payload.camera[2]
    $length = [math]::Sqrt($dx * $dx + $dy * $dy + $dz * $dz)
    $dot = (([double]$after.forward[0] * $dx) + ([double]$after.forward[1] * $dy) + ([double]$after.forward[2] * $dz)) / $length
    $record['forwardDot'] = [math]::Round($dot, 5)
    if ($dot -lt 0.9999) {
        $failures.Add("$name`: the view row dots the target at $([math]::Round($dot, 5)), so the object is off centre")
    }

    $observed[$name] = $record
}

# 4. level is the opposite trade: the heading reaches the target and the tilt is refused, so the reply must
# write pitch 0 and measure the swing with yawAim, while aim falls below 1 whenever the target is not level
# with the camera. The last case asks for a standoff as well, so both words have to survive the parse.
$levelCases = @(
    @{ name = 'level-above';        dx = $Distance; dy = 0.0;    dz = 300.0; standoff = $null },
    @{ name = 'level-below';        dx = $Distance; dy = 0.0;    dz = -300.0; standoff = $null },
    @{ name = 'level-level';        dx = $Distance; dy = 0.0;    dz = 0.0;    standoff = $null },
    @{ name = 'level-with-standoff'; dx = 300.0;    dy = -200.0; dz = 200.0; standoff = 256 }
)

foreach ($case in $levelCases) {
    $name = $case.name
    $placed = "camera set $(Format-Number ($target[0] + $case.dx)) $(Format-Number ($target[1] + $case.dy)) $(Format-Number ($target[2] + $case.dz)) 0 0"
    Invoke-Plugin $placed | Out-Null
    $before = Get-Camera

    $request = @{ reference = $targetId; level = $true }
    if ($null -ne $case.standoff) { $request['distance'] = $case.standoff }
    $reply = Invoke-Mcp 'ck.aim' ($request | ConvertTo-Json -Compress)
    $record = [ordered]@{
        placed = "$([math]::Round($before.x, 4)) $([math]::Round($before.y, 4)) $([math]::Round($before.z, 4))"
        asked  = if ($null -ne $case.standoff) { "level $($case.standoff)" } else { 'level' }
        status = $reply.status
        code   = $reply.code
    }

    if ($reply.status -ne 'ok') {
        $failures.Add("$name`: ck.aim level was refused ($($reply.code)): $($reply.message)")
        $observed[$name] = $record
        continue
    }

    $payload = $reply.payload | ConvertFrom-Json
    $cam = @([double]$payload.camera[0], [double]$payload.camera[1], [double]$payload.camera[2])
    $record['aim'] = $payload.aim
    $record['yawAim'] = $payload.yawAim
    $record['aligned'] = $payload.aligned
    $record['level'] = $payload.level
    $record['pitch'] = $payload.pitch
    $record['camera'] = "$($cam[0]) $($cam[1]) $($cam[2])"

    if ("$($payload.level)" -ne 'True') {
        $failures.Add("$name`: the reply does not say it held the horizon (level=$($payload.level))")
    }
    if ([math]::Abs([double]$payload.pitch) -gt 1e-6) {
        $failures.Add("$name`: pitch is $($payload.pitch), but a level aim must write 0 and keep the horizon")
    }

    # What the camera was asked to do is turn its heading onto the target, and that is what yawAim measures.
    if ([double]$payload.yawAim -lt 0.9999) {
        $failures.Add("$name`: yawAim is $($payload.yawAim), so the heading did not reach the target")
    }
    if ("$($payload.aligned)" -ne 'True') {
        $failures.Add("$name`: the reply says aligned=$($payload.aligned) while yawAim is $($payload.yawAim)")
    }

    # Centring is measured honestly: the target is only centred if the camera is level with it.
    $dx = $target[0] - $cam[0]
    $dy = $target[1] - $cam[1]
    $dz = $target[2] - $cam[2]
    $flat = [math]::Sqrt($dx * $dx + $dy * $dy)
    $length = [math]::Sqrt($dx * $dx + $dy * $dy + $dz * $dz)
    $expectedAim = if ($length -gt 1e-3) { $flat / $length } else { 1.0 }
    $record['expectedAim'] = [math]::Round($expectedAim, 4)
    if ([math]::Abs([double]$payload.aim - $expectedAim) -gt 2e-3) {
        $failures.Add("$name`: aim is $($payload.aim) but a level camera sees this target at $([math]::Round($expectedAim, 4)), so the report and the pose disagree")
    }
    if ([math]::Abs($case.dz) -gt 1.0 -and [double]$payload.aim -gt 0.99) {
        $failures.Add("$name`: aim is $($payload.aim) from $(if ($case.dz -gt 0) { 'above' } else { 'below' }) the target, so it was tilted after all")
    }

    # The heading is the yaw to the target, so the reply's yaw must equal the angle to it.
    if ($flat -gt 1e-3) {
        $expectedYaw = [math]::Atan2($dy, $dx)
        $record['expectedYaw'] = [math]::Round($expectedYaw, 4)
        if ([math]::Abs([double]$payload.yaw - $expectedYaw) -gt 2e-3) {
            $failures.Add("$name`: yaw is $($payload.yaw) but the target lies at $([math]::Round($expectedYaw, 4))")
        }
    }

    if ($null -ne $case.standoff) {
        $settled = [math]::Sqrt(([math]::Pow($cam[0] - $target[0], 2)) + ([math]::Pow($cam[1] - $target[1], 2)))
        $record['horizontalDistance'] = [math]::Round($settled, 4)
        if ([math]::Abs($settled - $case.standoff) -gt 1.0) {
            $failures.Add("$name`: the camera settled $([math]::Round($settled, 4)) away, not the $($case.standoff) asked for beside level")
        }
    } else {
        foreach ($axis in @('x', 'y', 'z')) {
            $slot = @{ x = 0; y = 1; z = 2 }[$axis]
            $drift = [math]::Abs($cam[$slot] - [double]$before.$axis)
            if ($drift -gt 0.01) {
                $failures.Add("$name`: the camera moved $([math]::Round($drift, 4)) on $axis, but level only turns it")
            }
        }
    }

    # A level horizon means the view row has no height, so this is what proves the tilt was refused.
    $after = Get-Camera
    $record['forwardUp'] = [math]::Round([double]$after.forward[2], 5)
    if ([math]::Abs([double]$after.forward[2]) -gt 2e-3) {
        $failures.Add("$name`: the view row points $([math]::Round([double]$after.forward[2], 4)) up, so the horizon is not level")
    }
    if ($flat -gt 1e-3) {
        $heading = (([double]$after.forward[0] * $dx) + ([double]$after.forward[1] * $dy)) / $flat
        $record['forwardFlatDot'] = [math]::Round($heading, 5)
        if ($heading -lt 0.9999) {
            $failures.Add("$name`: the flat view dots the target's direction at $([math]::Round($heading, 5)), so the heading is off")
        }
    }

    $observed[$name] = $record
}

# 5. A chain must rotate in place for every target and report each step, so aiming at only the first one fails.
# The names come from the cell itself: ck.frame names candidates when a reference is not found.
$missed = Invoke-Mcp 'ck.frame' (@{ reference = 'ZZZNoSuchReference' } | ConvertTo-Json -Compress)
$candidates = @()
if ("$($missed.message)" -match 'for example: (.*)$') {
    $candidates = @($Matches[1] -split ',\s*' | Where-Object { $_ -and $_ -ne $Reference } | Select-Object -First 2)
}

$chainTargets = @()
foreach ($name in $candidates) {
    # Framing moves the camera, so every position is learned before the chain is aimed.
    $framed = Invoke-Mcp 'ck.frame' (@{ reference = $name; distance = 256 } | ConvertTo-Json -Compress)
    if ($framed.status -ne 'ok') {
        $failures.Add("chain: '$name' could not be framed to learn its position: $($framed.code) $($framed.message)")
        continue
    }
    $point = $framed.payload | ConvertFrom-Json
    $chainTargets += , @("$($point.reference)", "$($point.formId)", [double]$point.target[0], [double]$point.target[1], [double]$point.target[2])
}

if ($chainTargets.Count -lt 2) {
    $failures.Add('the cell offered fewer than two usable references, so the chain case could not run')
} else {
    $midX = ($chainTargets[0][2] + $chainTargets[1][2]) / 2
    $midY = ($chainTargets[0][3] + $chainTargets[1][3]) / 2
    $midZ = ($chainTargets[0][4] + $chainTargets[1][4]) / 2
    Invoke-Plugin ("camera set $(Format-Number ($midX + 400)) $(Format-Number $midY) $(Format-Number ($midZ + 120)) 0 0") | Out-Null
    $before = Get-Camera

    $names = "$($chainTargets[0][0]),$($chainTargets[1][0])"
    $reply = Invoke-Mcp 'ck.aim' (@{ reference = $names } | ConvertTo-Json -Compress)
    $record = [ordered]@{ request = $names; status = $reply.status; code = $reply.code }

    if ($reply.status -ne 'ok') {
        $failures.Add("chain: ck.aim refused ($($reply.code)): $($reply.message)")
    } else {
        $payload = $reply.payload | ConvertFrom-Json
        $record['steps'] = $payload.steps
        $record['aim'] = $payload.aim
        $record['aligned'] = $payload.aligned
        $record['camera'] = "$($payload.camera[0]) $($payload.camera[1]) $($payload.camera[2])"

        if ([int]$payload.steps -ne 2 -or @($payload.chain).Count -ne 2) {
            $failures.Add("chain: the reply reports $($payload.steps) steps and $(@($payload.chain).Count) chain entries, not 2")
        }

        foreach ($axis in @('x', 'y', 'z')) {
            $slot = @{ x = 0; y = 1; z = 2 }[$axis]
            $drift = [math]::Abs([double]$payload.camera[$slot] - [double]$before.$axis)
            if ($drift -gt 0.01) {
                $failures.Add("chain: the camera moved $([math]::Round($drift, 4)) on $axis, but a chain must rotate in place")
            }
        }

        for ($i = 0; $i -lt [math]::Min(2, @($payload.chain).Count); $i++) {
            $step = @($payload.chain)[$i]
            $targetPoint = $chainTargets[$i]
            $dx = [double]$targetPoint[2] - [double]$before.x
            $dy = [double]$targetPoint[3] - [double]$before.y
            $dz = [double]$targetPoint[4] - [double]$before.z
            $flat = [math]::Sqrt($dx * $dx + $dy * $dy)
            $expected = if ($flat -gt 1e-3) { [math]::Atan2($dz, $flat) } elseif ($dz -gt 0) { [math]::PI / 2 } else { -[math]::PI / 2 }
            $record["step$($i + 1)Pitch"] = $step.pitch
            $record["step$($i + 1)Expected"] = [math]::Round($expected, 4)

            if ([math]::Abs([double]$step.aim - 1.0) -gt $Tolerance) {
                $failures.Add("chain step $($i + 1) ($($step.reference)): aim is $($step.aim), so that target was not centred")
            }
            if ([math]::Abs([double]$step.pitch - $expected) -gt 2e-3) {
                $failures.Add("chain step $($i + 1) ($($step.reference)): pitch is $($step.pitch) but its own camera-target angle is $([math]::Round($expected, 4))")
            }
        }

        # The final pose must belong to the last target, or the chain stopped early and still said ok.
        $last = $chainTargets[1]
        $ldx = [double]$last[2] - [double]$payload.camera[0]
        $ldy = [double]$last[3] - [double]$payload.camera[1]
        $ldz = [double]$last[4] - [double]$payload.camera[2]
        $length = [math]::Sqrt($ldx * $ldx + $ldy * $ldy + $ldz * $ldz)
        $after = Get-Camera
        $dot = (([double]$after.forward[0] * $ldx) + ([double]$after.forward[1] * $ldy) + ([double]$after.forward[2] * $ldz)) / $length
        $record['forwardDotLast'] = [math]::Round($dot, 5)
        if ($dot -lt 0.9999) {
            $failures.Add("chain: the final view dots the last target at $([math]::Round($dot, 5)), so the chain did not finish on it")
        }

        if ("$($payload.reference)" -ne "$($last[0])") {
            $failures.Add("chain: the reply names '$($payload.reference)' as its target, not the last chain entry '$($last[0])'")
        }

        # A standoff moves the camera, which is the opposite of what a chain is for, so it must be refused.
        $refused = Invoke-Mcp 'ck.aim' (@{ reference = $names; distance = 128 } | ConvertTo-Json -Compress)
        $record['refusedWithDistance'] = "$($refused.status)/$($refused.code)"
        if ($refused.status -ne 'error' -or "$($refused.code)" -ne 'bad_args') {
            $failures.Add("chain: a chain with a distance was not refused ($($refused.status)/$($refused.code)); it must not move the camera")
        }
    }

    $observed['chain'] = $record
}

# Framing moves the camera by design, so this runs last and cannot disturb the rotate-in-place cases above.
# It runs twice at different distances: a tilt taken from the previous frame's standoff would still centre the
# object while putting the camera at the wrong height, so the height and the flat part are fractions of the
# distance asked for - a half and cos 30 - rather than something only the reply knows.
$frameChecks = @(
    @{ name = 'frame-fit';   distance = $null },
    @{ name = 'frame-half';  distance = 'half' }
)
$fitted = 0.0
foreach ($check in $frameChecks) {
    # Constraint: a frame moves the camera out of the cell when it stands far enough back, so the cell is named
    # on every frame call; ck.frame skips the load while it is still the one open.
    $request = @{ reference = $Reference; yaw = 1.5; cell = $Cell }
    # Constraint: a closer standoff, not a wider one, because a frame far enough back leaves the cell and a
    # reference is then looked up in whatever cell the camera moved into.
    if ("$($check.distance)" -eq 'half') { $request['distance'] = [math]::Round($fitted * 0.5, 2) }

    $framed = Invoke-Mcp 'ck.frame' ($request | ConvertTo-Json -Compress)
    $record = [ordered]@{ name = $check.name; request = $request.reference; asked = $request['distance']; status = $framed.status; code = $framed.code }

    if ($framed.status -ne 'ok') {
        $failures.Add("$($check.name)`: ck.frame refused ($($framed.code)): $($framed.message)")
        $observed[$check.name] = $record
        continue
    }

    $payload = $framed.payload | ConvertFrom-Json
    $pose = Get-Camera
    $centre = @([double]$payload.center[0], [double]$payload.center[1], [double]$payload.center[2])
    $target = @([double]$payload.target[0], [double]$payload.target[1], [double]$payload.target[2])
    $distance = [double]$payload.distance
    $dx = $centre[0] - [double]$pose.x
    $dy = $centre[1] - [double]$pose.y
    $dz = $centre[2] - [double]$pose.z
    $span = [math]::Sqrt($dx * $dx + $dy * $dy + $dz * $dz)
    $dot = (([double]$pose.forward[0] * $dx) + ([double]$pose.forward[1] * $dy) + ([double]$pose.forward[2] * $dz)) / $span
    $offset = [math]::Sqrt(([math]::Pow($centre[0] - $target[0], 2)) + ([math]::Pow($centre[1] - $target[1], 2)) + ([math]::Pow($centre[2] - $target[2], 2)))
    # $dz runs from the camera to the centre, so the camera's height above the centre is its negative.
    $rise = -$dz / $distance
    $flat = ([math]::Sqrt($dx * $dx + $dy * $dy)) / $distance

    $record['aim'] = $payload.aim
    $record['aligned'] = $payload.aligned
    $record['distance'] = $payload.distance
    $record['span'] = [math]::Round($span, 2)
    $record['elevationFraction'] = [math]::Round($rise, 4)
    $record['flatFraction'] = [math]::Round($flat, 4)
    $record['forwardDotCentre'] = [math]::Round($dot, 5)
    $record['centerOffsetFromTarget'] = [math]::Round($offset, 2)
    if ($null -eq $check.distance) { $fitted = $distance }

    if ([math]::Abs([double]$payload.aim - 1.0) -gt $Tolerance) {
        $failures.Add("$($check.name)`: the reply reports aim $($payload.aim), so the framed centre was not centred")
    }
    if ("$($payload.aligned)" -ne 'True') {
        $failures.Add("$($check.name)`: the reply says aligned=$($payload.aligned), but frame must centre what it framed")
    }
    if ([math]::Abs($span - $distance) -gt 0.05) {
        $failures.Add("$($check.name)`: the camera stands $([math]::Round($span, 2)) from the centre it reports, not the $($payload.distance) it reports as its distance")
    }
    if ($dot -lt 0.9999) {
        $failures.Add("$($check.name)`: the view row dots the framed centre at $([math]::Round($dot, 5)), so the object is not centred")
    }

    # The 30-degree elevation is a fraction of the distance, so a tilt taken from another standoff shows here.
    if ([math]::Abs($rise - 0.5) -gt 0.002) {
        $failures.Add("$($check.name)`: the camera stands $([math]::Round($rise, 4)) of the distance above the centre, not the half a 30 degree tilt puts it at")
    }
    if ([math]::Abs($flat - [math]::Cos([math]::PI / 6)) -gt 0.002) {
        $failures.Add("$($check.name)`: the flat part is $([math]::Round($flat, 4)) of the distance, not the $([math]::Round([math]::Cos([math]::PI / 6), 4)) a 30 degree tilt gives")
    }

    $observed[$check.name] = $record
}

$evidenceJson = [ordered]@{ aim = $observed; failures = @($failures); passed = ($failures.Count -eq 0) } | ConvertTo-Json -Depth 6
if ($Evidence) { $evidenceJson | Set-Content -LiteralPath $Evidence -Encoding utf8 }

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Output "FAIL $failure" }
    exit 1
}

Write-Output 'PASS ck.aim kept the camera in place and centred every target - level, above, below, overhead, at a standoff, in a two-step chain - held the horizon when asked from above, below, level and beside a standoff, and ck.frame centred the box centre it framed at two distances'
Write-Output $evidenceJson
exit 0
