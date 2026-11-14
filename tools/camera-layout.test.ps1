# Regression: a drift in the measured NiCamera layout (world transform 0x70, translate at +0x30, scale at
# +0x3C, row stride 4) must fail here, in a pull request, instead of surfacing as a wrong camera pose.
#
# The stride check exists because a 3-wide read still produces numbers; only orthonormal rows and a
# column read through the stride prove the decode is the measured one.

param(
    [string]$Plugin = 'plugin/Agent.cpp',
    [string]$Frame = 'plugin/AgentFrame.cpp',
    [string]$Server = 'CKAgent.Mcp/CkTools.cs',
    [string]$Probe = 'tools/cell-probe.ps1',
    [string]$Flow = 'plugin/AgentCellFlow.cpp',
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

# Regression: the cell reply is a contract between three files - the plugin writes the keys, the server
# parses them, and the probe asserts them - so a rename or a moved offset in one of them must fail here
# instead of shipping a load that reports success without naming the cell it opened.
foreach ($pair in @(@('server', $Server), @('probe', $Probe))) {
    if (-not (Test-Path -LiteralPath $pair[1] -PathType Leaf)) {
        $failures.Add("$($pair[0]) source not found: $($pair[1])")
    }
}

if ((Test-Path -LiteralPath $Plugin -PathType Leaf) -and (Test-Path -LiteralPath $Server -PathType Leaf) -and (Test-Path -LiteralPath $Probe -PathType Leaf)) {
    $cellPlugin = Get-Content -LiteralPath $Plugin -Raw
    $serverText = Get-Content -LiteralPath $Server -Raw
    $probeText = Get-Content -LiteralPath $Probe -Raw

    $cellOffset = Get-Constant $cellPlugin 'kCurrentCellOffset'
    $tesOffset = Get-Constant $cellPlugin 'kTesSingletonOffset'
    $observed['kCurrentCellOffset'] = if ($null -ne $cellOffset) { '0x{0:X}' -f $cellOffset } else { 'missing' }
    $observed['kTesSingletonOffset'] = if ($null -ne $tesOffset) { '0x{0:X}' -f $tesOffset } else { 'missing' }

    if ($null -eq $cellOffset) { $failures.Add("kCurrentCellOffset is not declared in $Plugin") }
    if ($null -eq $tesOffset) { $failures.Add("kTesSingletonOffset is not declared in $Plugin") }

    if ($null -ne $cellOffset) {
        # Invariant: _CurrentCell sits at 0xF8 in BGSRenderWindow, the offset ck.cell was measured against.
        Assert-Layout 'kCurrentCellOffset (_CurrentCell in BGSRenderWindow)' ('0x{0:X}' -f $cellOffset) '0xF8'
        if ($cellPlugin -notmatch 'g_renderWindow \+ kCurrentCellOffset') {
            $failures.Add('the cell is not read at g_renderWindow + kCurrentCellOffset, so the constant no longer describes the read')
        }
    }

    if ($null -ne $tesOffset) {
        Assert-Layout 'kTesSingletonOffset (TES singleton pointer)' ('0x{0:X}' -f $tesOffset) '0x3D49150'
    }

    # A read past the object would decode a neighbouring allocation, so the cell read must stay inside it.
    if ($cellPlugin -notmatch 'IsReadable\(reinterpret_cast<const void\*>\(cell\), 0x180\)') {
        $failures.Add('the cell read no longer bounds itself to the 0x180-byte cell object')
    }

    # The placement is one shared read, so status and cell cannot report different grid coordinates.
    $placementCalls = ([regex]::Matches($cellPlugin, 'ReadPlacement\(')).Count
    $flowText = if (Test-Path -LiteralPath $Flow -PathType Leaf) { Get-Content -LiteralPath $Flow -Raw } else { '' }
    $observed['placementCalls'] = $placementCalls

    if ($placementCalls -lt 2) {
        $failures.Add("$Plugin calls ReadPlacement $placementCalls time(s), so the placement is not shared with status")
    }
    if ($flowText -notmatch 'Placement ReadPlacement\(std::uintptr_t slot\)') {
        $failures.Add("$Flow no longer defines ReadPlacement, so the shared placement read is gone")
    }

    # Every key the reply carries, as the plugin emits it inside the JSON format strings.
    $emitted = @('resolved', 'window', 'cell', 'loaded', 'readable', 'editorId', 'formId', 'interior', 'gridX', 'gridY', 'hasWorldSpace')
    foreach ($key in $emitted) {
        if (-not $cellPlugin.Contains('\"' + $key + '\"')) {
            $failures.Add("$Plugin no longer emits '$key' in the cell reply")
        }
    }

    $serverKeys = @([regex]::Matches($serverText, 'Text\(root, "(\w+)"\)') | ForEach-Object { $_.Groups[1].Value })
    $serverKeys += @([regex]::Matches($serverText, 'TryGetProperty\("(\w+)"') | ForEach-Object { $_.Groups[1].Value })
    $serverKeys = @($serverKeys | Select-Object -Unique)

    $probeKeys = @([regex]::Matches($probeText, "PSObject\.Properties\['(\w+)'\]") | ForEach-Object { $_.Groups[1].Value })
    $probeLoop = [regex]::Match($probeText, 'foreach \(\$key in ([^)]*)\)')
    if ($probeLoop.Success) {
        $probeKeys += @([regex]::Matches($probeLoop.Groups[1].Value, "'(\w+)'") | ForEach-Object { $_.Groups[1].Value })
    }
    $probeKeys = @($probeKeys | Select-Object -Unique)

    $observed['serverKeys'] = ($serverKeys -join ',')
    $observed['probeKeys'] = ($probeKeys -join ',')

    if ($serverKeys -notcontains 'editorId' -or $serverKeys -notcontains 'formId') {
        $failures.Add("$Server no longer decodes editorId and formId, so the cell is identified by nothing")
    }
    foreach ($key in 'editorId', 'formId') {
        if ($probeKeys -notcontains $key) {
            $failures.Add("$Probe no longer asserts '$key', so a reply with the wrong cell would pass it")
        }
    }
    foreach ($key in 'interior', 'gridX', 'gridY', 'hasWorldSpace') {
        if ($probeKeys -notcontains $key) {
            $failures.Add("$Probe no longer asserts '$key', so the placement could stop being reported")
        }
    }

    # Every key read by the server or asserted by the probe must be one the plugin actually emits.
    foreach ($key in @($serverKeys + $probeKeys | Select-Object -Unique)) {
        if ($emitted -notcontains $key) {
            $failures.Add("$Server or $Probe reads '$key', which the cell reply does not carry")
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

Write-Output 'PASS camera layout, scene-camera aim check, orbit pivot and cell decode all match what was measured'
Write-Output $evidenceJson
exit 0
