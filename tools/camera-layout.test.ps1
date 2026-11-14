# Regression: a drift in the measured NiCamera layout (world transform 0x70, translate at +0x30, scale at
# +0x3C, row stride 4) must fail here, in a pull request, instead of surfacing as a wrong camera pose.
#
# The stride check exists because a 3-wide read still produces numbers; only orthonormal rows and a
# column read through the stride prove the decode is the measured one.

param(
    [string]$Plugin = 'plugin/Agent.cpp',
    [string]$Frame = 'plugin/AgentFrame.cpp',
    [string]$Evidence = ''
)

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
$observed = [ordered]@{}

function ConvertTo-Number([string]$value) {
    if ($value -match '^0[xX]([0-9A-Fa-f]+)$') { return [Convert]::ToInt64($Matches[1], 16) }
    return [int64]$value
}

function Get-Constant([string]$text, [string]$name) {
    $match = [regex]::Match($text, "constexpr[^\r\n]*\b$name\s*=\s*(0[xX][0-9A-Fa-f]+|\d+)")
    if (-not $match.Success) { return $null }
    return ConvertTo-Number $match.Groups[1].Value
}

function Assert-Layout([string]$what, [string]$actual, [string]$expected) {
    if ($actual -ne $expected) { $failures.Add("$what is $actual but the measured layout is $expected") }
}

if (-not (Test-Path -LiteralPath $Plugin -PathType Leaf)) {
    $failures.Add("plugin source not found: $Plugin")
}

if ($failures.Count -eq 0) {
    $pluginText = Get-Content -LiteralPath $Plugin -Raw
    $world = Get-Constant $pluginText 'kNiCameraWorld'
    $translate = Get-Constant $pluginText 'kNiCameraTranslate'
    $scale = Get-Constant $pluginText 'kNiCameraScale'
    $stride = Get-Constant $pluginText 'kNiRowStride'
    $readable = Get-Constant $pluginText 'kNiCameraReadable'

    $observed['kNiCameraWorld'] = if ($null -ne $world) { '0x{0:X}' -f $world } else { 'missing' }
    $observed['kNiCameraTranslate'] = if ($null -ne $translate) { '0x{0:X}' -f $translate } else { 'missing' }
    $observed['kNiCameraScale'] = if ($null -ne $scale) { '0x{0:X}' -f $scale } else { 'missing' }
    $observed['kNiRowStride'] = $stride

    foreach ($pair in @(@('kNiCameraWorld', $world), @('kNiCameraTranslate', $translate), @('kNiCameraScale', $scale), @('kNiRowStride', $stride))) {
        if ($null -eq $pair[1]) { $failures.Add("$($pair[0]) is not declared in $Plugin") }
    }

    if ($failures.Count -eq 0) {
        # The base transform and the three words must keep their measured places.
        Assert-Layout 'kNiCameraWorld' ('0x{0:X}' -f $world) '0x70'
        Assert-Layout 'kNiCameraTranslate (World + 0x30, after the 0x30-byte rotate)' ('0x{0:X}' -f $translate) ('0x{0:X}' -f ($world + 0x30))
        Assert-Layout 'kNiCameraScale (World + 0x3C)' ('0x{0:X}' -f $scale) ('0x{0:X}' -f ($world + 0x3C))
        Assert-Layout 'kNiRowStride (NiMatrix43 is float[3][4])' "$stride" '4'

        # Invariant: a read must stay inside the object, or a stride change above hides behind a lucky allocation.
        if ($null -eq $readable) {
            $failures.Add('kNiCameraReadable is not declared, so a read past the scale word is unchecked')
        } elseif ($readable -lt ($scale + 4)) {
            $failures.Add("kNiCameraReadable is 0x$('{0:X}' -f $readable) but must cover the scale word at 0x$('{0:X}' -f ($scale + 4))")
        }

        # The stride must actually address columns, otherwise it is a comment rather than a layout.
        foreach ($column in 1, 2) {
            if ($pluginText -notmatch "kNiRowStride\s*\+\s*$column") {
                $failures.Add("the plugin never reads column $column through kNiRowStride, so the stride is not part of the decode")
            }
        }
    }
}

# The aim check must be independent: it grades the scene camera's view row, not the rotation the tool just
# wrote, otherwise a pose the editor rejected still reports aim 1.0.
if (-not (Test-Path -LiteralPath $Frame -PathType Leaf)) {
    $failures.Add("frame source not found: $Frame")
} else {
    $frameText = Get-Content -LiteralPath $Frame -Raw
    $view = [regex]::Match($pluginText, 'bool ReadSceneView\([\s\S]*?\r?\n\t\}')

    $observed['sceneView'] = if ($view.Success) { 'found' } else { 'missing' }

    if (-not $view.Success) {
        $failures.Add("$Plugin has no ReadSceneView, so the aim check has nothing independent to read")
    } else {
        if ($view.Value -notmatch 'AnchoredNiCamera\(') {
            $failures.Add('ReadSceneView does not resolve through AnchoredNiCamera, so it may decode a different object')
        }
        if ($view.Value -notmatch 'out\[0\] = rotate\[0\]') {
            $failures.Add('ReadSceneView does not read row 0, which is the view row the scene camera renders')
        }
        if ($view.Value -match 'kNiRowStride \+ 0') {
            $failures.Add('ReadSceneView still reads the node row through the stride, so the check grades the matrix it wrote')
        }
    }

    if ($frameText -notmatch 'ReadSceneView\(camera') {
        $failures.Add("$Frame does not verify the aim through ReadSceneView")
    }
    if ($frameText -match 'm_pEntry\[0\]\[0\] \* ') {
        $failures.Add("$Frame still dots the aim against the matrix it just wrote")
    }
}

# The orbit must stay a circle: it takes the editor's own pivot and derives the camera from it at the radius
# asked for. A drift here still produces a camera pose, just one that no longer circles the target.
if (-not (Test-Path -LiteralPath $Frame -PathType Leaf)) {
    $failures.Add("frame source not found: $Frame")
} else {
    $frameText = Get-Content -LiteralPath $Frame -Raw
    $orbit = [regex]::Match($frameText, 'bool RunOrbitTool[\s\S]*?\r?\n\t\}')

    $observed['orbit'] = if ($orbit.Success) { 'found' } else { 'missing' }

    if (-not $orbit.Success) {
        $failures.Add("$Frame has no RunOrbitTool, so mode=orbit is missing or unreachable")
    } else {
        $body = $orbit.Value
        $required = @(
            @('SetPivot\(pivot', "the orbit must set the editor's own pivot, or its next orbit and zoom start from a different focus"),
            @('HasPivot\(\)', 'the orbit must report whether the editor took the pivot, or a refused focus looks like a success'),
            @('pivotRead', 'the orbit must report the pivot it read back, because the write alone does not prove the focus'),
            @('x - distance \*', 'the orbit must place the camera a distance away from the pivot on the x axis, not on the far side'),
            @('y - distance \*', 'the orbit must place the camera a distance away from the pivot on the y axis, not on the far side'),
            @('z - distance \*', 'the orbit must place the camera a distance away from the pivot on the z axis, or the circle leaves the plane'),
            @('MakeViewMatrix\(angle, pitch\)', 'the orbit must aim through the shared pose builder so the view row matches the angle'),
            @('!\(distance > 0', 'the orbit must reject a zero or negative radius, which is not a circle')
        )

        foreach ($expectation in $required) {
            if ($body -notmatch $expectation[0]) {
                $failures.Add("RunOrbitTool no longer matches /$($expectation[0])/: $($expectation[1])")
            }
        }

        # A tool nothing dispatches is a dead branch, so the command must be routed with the other poses.
        if ($pluginText -notmatch 'tool == "orbit"') {
            $failures.Add("$Plugin never dispatches the orbit tool, so mode=orbit cannot reach it")
        }
    }
}

$evidenceJson = [ordered]@{ layout = $observed; failures = @($failures); passed = ($failures.Count -eq 0) } | ConvertTo-Json -Depth 6
if ($Evidence) {
    $evidenceJson | Set-Content -LiteralPath $Evidence -Encoding utf8
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Output "FAIL $failure" }
    exit 1
}

Write-Output 'PASS camera layout, scene-camera aim check and orbit pivot all match what was measured'
Write-Output $evidenceJson
exit 0
