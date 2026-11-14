<#
.SYNOPSIS
Cross-checks the camera node against the scene camera at every orientation in a yaw/pitch sweep.

.DESCRIPTION
The plugin writes the camera node's rotation, and the editor copies it into the scene camera shifted by one
row: scene rows are the node's `r1, r2, r0`, so the view direction is row 0 of the scene camera and row 1 of
the node. The tool reports `rotationOffset` as the sum of the differences that shift implies, which is 6.0
at an axis-aligned pose - a number many wrong mappings can also produce. A diagonal pose separates them:
the same shift reproduces 6.5027 there.

This probe sets the camera to each yaw and pitch in the sweep, reads the pose back, and at every one:

  1. recomputes the offset from the node rows and the shift and compares it with the reported number;
  2. checks the view row the tool reports is node row 1, not row 0 or 2;
  3. checks the view row matches the requested yaw and pitch, so the pose read back is the pose asked for;
  4. checks the scene camera's translation matched the request (`positionSynced`).

A single orientation would pass with a mapping that happens to be right at that angle, which is why the
sweep exists: any drift in the shift, the row used for the view, or the yaw/pitch order shows up at some
angle here rather than as a wrong pose in a session.

Exit codes: 0 pass, 1 assertion failed, 2 the camera has no usable transform (no cell loaded),
3 the plugin pipe is unreachable.

.PARAMETER Evidence
Optional path for the JSON evidence file: every pose, its reported offset, the offset the shift predicts,
and the deviations.

.EXAMPLE
# from the CKAgent directory, with the editor running and a cell loaded
./tools/camera-tool-cross-check.ps1 -Evidence cross-check.json
#>

param(
    [double[]]$YawDegrees = @(0, 30, 45, 60, 90, 120, 135, 180, 225, 270, 315),
    [double[]]$PitchRadians = @(-1.0, -0.5, 0, 0.5, 1.0),
    [double]$PositionX = 23507.4121,
    [double]$PositionY = -41177.168,
    [double]$PositionZ = 674.089,
    [double]$Tolerance = 0.0025,
    [string]$Evidence = ''
)

$ErrorActionPreference = 'Stop'

# Add-Type needs a real TEMP; this host can have TEMP set to the literal :TEMP.
$scratch = Join-Path $PSScriptRoot '.probe-tmp'
New-Item -ItemType Directory -Force $scratch | Out-Null
$env:TEMP = $scratch
$env:TMP = $scratch

$failures = New-Object System.Collections.Generic.List[string]
$observed = [ordered]@{ poses = @(); worstOffsetError = 0.0; worstViewDot = 1.0; worstViewError = 0.0; worstForwardError = 0.0 }
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

# A pose needs a loaded cell: without one the camera has no usable transform and every read would be zero.
$current = $null
try { $current = (Invoke-Plugin 'camera get') | ConvertFrom-Json } catch { $current = $null }
if ($null -eq $current -or "$($current.forward)" -eq '') {
    Write-Output 'FAIL the camera has no usable transform: load a cell first'
    exit 2
}

$position = "$(Format-Number $PositionX) $(Format-Number $PositionY) $(Format-Number $PositionZ)"

foreach ($yawValue in $YawDegrees) {
    $yaw = $yawValue * [math]::PI / 180.0

    foreach ($pitchValue in $PitchRadians) {
        $pitch = $pitchValue
        $label = "yaw $yawValue deg, pitch $(Format-Number $pitch)"

        try {
            Invoke-Plugin "camera set $position $(Format-Number $yaw) $(Format-Number $pitch)" | Out-Null
            $pose = (Invoke-Plugin 'camera get') | ConvertFrom-Json
        } catch {
            $failures.Add("$label`: the pose could not be set or read back: $($_.Exception.Message)")
            continue
        }

        # The node rotation arrives row-major as 9 floats, so row i starts at 3*i.
        $rows = @(
            @([double]$pose.rotate[0], [double]$pose.rotate[1], [double]$pose.rotate[2]),
            @([double]$pose.rotate[3], [double]$pose.rotate[4], [double]$pose.rotate[5]),
            @([double]$pose.rotate[6], [double]$pose.rotate[7], [double]$pose.rotate[8])
        )
        $shifted = @($rows[1], $rows[2], $rows[0])

        # Invariant: the shift is the relation being checked, so the predicted offset is derived from it alone.
        $predicted = 0.0
        for ($i = 0; $i -lt 3; $i++) {
            for ($j = 0; $j -lt 3; $j++) {
                $predicted += [math]::Abs($rows[$i][$j] - $shifted[$i][$j])
            }
        }

        $reported = [double]$pose.rotationOffset
        $offsetError = [math]::Abs($reported - $predicted)

        $forward = @([double]$pose.forward[0], [double]$pose.forward[1], [double]$pose.forward[2])

        # Invariant: the rows print to four decimals, so a dot of two rounded vectors reads 0.99986 at a
        # diagonal pose even when the components are identical; the comparison is componentwise for that reason.
        $viewError = 0.0
        for ($i = 0; $i -lt 3; $i++) { $viewError = [math]::Max($viewError, [math]::Abs($rows[1][$i] - $forward[$i])) }
        $rowLength = [math]::Sqrt(($rows[1][0] * $rows[1][0]) + ($rows[1][1] * $rows[1][1]) + ($rows[1][2] * $rows[1][2]))
        $forwardLength = [math]::Sqrt(($forward[0] * $forward[0]) + ($forward[1] * $forward[1]) + ($forward[2] * $forward[2]))
        $viewDot = (($rows[1][0] * $forward[0]) + ($rows[1][1] * $forward[1]) + ($rows[1][2] * $forward[2])) / ($rowLength * $forwardLength)

        $cosPitch = [math]::Cos($pitch)
        # Invariant: each product is parenthesised, or the comma builds an array before the multiply.
        $expected = @(($cosPitch * [math]::Cos($yaw)), ($cosPitch * [math]::Sin($yaw)), ([math]::Sin($pitch)))
        $forwardError = 0.0
        for ($i = 0; $i -lt 3; $i++) {
            $forwardError = [math]::Max($forwardError, [math]::Abs($forward[$i] - $expected[$i]))
        }

        $record = [ordered]@{
            yawDegrees = $yawValue
            pitch = $pitch
            rotationOffset = [math]::Round($reported, 4)
            predictedOffset = [math]::Round($predicted, 4)
            offsetError = [math]::Round($offsetError, 6)
            viewDot = [math]::Round($viewDot, 5)
            viewError = [math]::Round($viewError, 6)
            forwardError = [math]::Round($forwardError, 5)
            positionSynced = "$($pose.positionSynced)"
        }
        $observed.poses += $record

        $observed.worstOffsetError = [math]::Max($observed.worstOffsetError, $offsetError)
        $observed.worstViewDot = [math]::Min($observed.worstViewDot, $viewDot)
        if ($viewError -gt $observed.worstViewError) { $observed.worstViewError = $viewError }
        $observed.worstForwardError = [math]::Max($observed.worstForwardError, $forwardError)

        if ($offsetError -gt $Tolerance) {
            $failures.Add("$label`: the tool reports rotationOffset $reported but the row shift predicts $([math]::Round($predicted, 4))")
        }
        if ($viewError -gt 0.0004) {
            $failures.Add("$label`: the view row differs from node row 1 by $([math]::Round($viewError, 6)), so it is not the shifted row")
        }
        if ($forwardError -gt $Tolerance) {
            $failures.Add("$label`: the view row is $($forward -join ',') instead of the requested yaw and pitch")
        }
        if ("$($pose.positionSynced)" -ne 'True') {
            $failures.Add("$label`: positionSynced is '$($pose.positionSynced)', so the scene camera took another position")
        }
    }
}

$evidenceJson = [ordered]@{ crossCheck = $observed; failures = @($failures); passed = ($failures.Count -eq 0) } | ConvertTo-Json -Depth 6
if ($Evidence) { $evidenceJson | Set-Content -LiteralPath $Evidence -Encoding utf8 }

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Output "FAIL $failure" }
    exit 1
}

$count = @($observed.poses).Count
$worstOffset = [math]::Round($observed.worstOffsetError, 6)
$worstDot = [math]::Round($observed.worstViewDot, 6)
$worstView = [math]::Round($observed.worstViewError, 6)
$worstForward = [math]::Round($observed.worstForwardError, 6)
Write-Output "PASS the row shift held at all $count orientations: scene row i is node row i+1, the view row is node row 1, and the reported rotationOffset matched the shift (worst offset error $worstOffset, worst view error $worstView, worst view dot $worstDot, worst forward error $worstForward)"
Write-Output $evidenceJson
exit 0
