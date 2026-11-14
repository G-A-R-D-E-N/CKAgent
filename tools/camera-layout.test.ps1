# Regression: a drift in the measured NiCamera layout (world transform 0x70, translate at +0x30, scale at
# +0x3C, row stride 4) must fail here, in a pull request, instead of surfacing as a wrong camera pose.
#
# The stride check exists because a 3-wide read still produces numbers; only orthonormal rows and a
# column read through the stride prove the decode is the measured one.

param(
    [string]$Plugin = 'plugin/Agent.cpp',
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

$evidenceJson = [ordered]@{ layout = $observed; failures = @($failures); passed = ($failures.Count -eq 0) } | ConvertTo-Json -Depth 6
if ($Evidence) {
    $evidenceJson | Set-Content -LiteralPath $Evidence -Encoding utf8
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Output "FAIL $failure" }
    exit 1
}

Write-Output 'PASS camera layout matches the measured offsets and row stride'
Write-Output $evidenceJson
exit 0
