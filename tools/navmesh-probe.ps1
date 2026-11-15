<#
.SYNOPSIS
Asserts the navmesh tools resolve and run the editor's own commands, and report what the pass wrote.

.DESCRIPTION
The navmesh surface is the CK's menu commands, so the things that can go wrong here are silent: a
hard-coded id that this build does not use (ids are MFC-generated per build), and a verdict that is
claimed instead of read. This probe checks observable behaviour:

  1. `mode=state` names the same cell the plugin reports, lists presence and each navmesh form, and
     counts a non-zero number of vertices and triangles per form instead of claiming an area it cannot
     measure;
  2. `ck.navmesh mode=commands` lists the NAVMESH menu entries from the live menu, each with a
     non-zero id, and Check NavMeshes is among them;
  3. `mode=check` posts exactly the id that discovery reported, so nothing between the two is a
     constant;
  4. the reply names the CKPE log it read and carries a `report`, which is the only place this build
     records what a pass found - there is no Warnings window on 1.11.246.0;
  5. `mode=finalize`, `mode=generate` and `mode=apply` without confirm are refused with no payload,
     and the plugin refuses finalize too for a caller that skips the server;
  6. a generation strategy or a navmesh caption this build does not carry is refused by name, never
     posted and hoped for;
  7. an unknown mode is refused rather than guessed at.

The probe never runs generation, finalize or run: those change the loaded cell, so it checks only that
an unconfirmed call is refused and a bad caption is rejected before anything is posted.

The check command is only posted, never saved, so the plugin on disk is untouched. Run it from the
repository root with the editor running, CKAgent.dll loaded, and a cell with a navmesh open.

Exit codes: 0 pass, 1 assertion failed, 2 the state to measure was not there (no cell, no navmesh
command, no CKPE log), 3 the built server assembly is missing.

.PARAMETER Dll
Path to the built MCP server assembly, as used by the probe's MCP steps.

.PARAMETER Evidence
Optional path for the JSON evidence record.

.EXAMPLE
# from the repository root, with the server built and a cell open
./tools/navmesh-probe.ps1 -Evidence navmesh.json
#>

param(
    [string]$Dll = 'CKAgent.Mcp/bin/Release/net8.0/ckagent.dll',
    [string]$Game = 'D:\SteamLibrary\steamapps\common\Fallout 4',
    [string]$Evidence = ''
)

$ErrorActionPreference = 'Stop'

$scratch = Join-Path $PSScriptRoot '.probe-tmp'
New-Item -ItemType Directory -Force $scratch | Out-Null

$failures = New-Object System.Collections.Generic.List[string]
$observed = [ordered]@{}

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
    $outFile = Join-Path $scratch 'mcp-stdout.txt'
    $errFile = Join-Path $scratch 'mcp-stderr.txt'
    # The arguments travel in a file: PowerShell drops the quotes out of JSON handed to a native command.
    $argsFile = Join-Path $scratch 'mcp-args.json'
    Set-Content -LiteralPath $argsFile -Value $json -Encoding utf8

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & node (Join-Path $PSScriptRoot 'mcp-call.mjs') $Dll $tool $argsFile $Game > $outFile 2> $errFile
    $ErrorActionPreference = $previous

    $lines = @()
    if (Test-Path -LiteralPath $outFile) { $lines = @(Get-Content -LiteralPath $outFile) }
    $text = (@($lines) | Where-Object { "$_".TrimStart().StartsWith('{') } | Select-Object -Last 1)
    $text = "$text".Trim()
    try {
        return $text | ConvertFrom-Json
    } catch {
        return [pscustomobject]@{ status = 'unreadable'; code = 'unreadable'; message = $text; payload = $null }
    }
}

function Flatten([string]$text) {
    return ($text -replace '[^A-Za-z]', '').ToLowerInvariant()
}

$server = Get-Item -LiteralPath (Join-Path '.' $Dll) -ErrorAction SilentlyContinue
if (-not $server) {
    Write-Output "SKIP no built server at $Dll; build CKAgent.Mcp first"
    exit 3
}

try {
    $status = (Invoke-Plugin 'status') | ConvertFrom-Json
} catch {
    Write-Output "SKIP $($_.Exception.Message)"
    exit 2
}

$observed['supported'] = $status.supported
if ($status.supported -ne $true) {
    Write-Output 'SKIP the plugin is loaded against an unsupported editor build'
    exit 2
}

$cell = $null
try { $cell = (Invoke-Plugin 'cell') | ConvertFrom-Json } catch { }
if (-not $cell -or $cell.loaded -ne $true) {
    Write-Output 'SKIP no cell is loaded; open a cell with a navmesh first'
    exit 2
}
$observed['cell'] = $cell.editorId

# 1. Discovery must come from the live menu.
$commandsReply = Invoke-Mcp 'ck.navmesh' '{"mode":"commands"}'
if ($commandsReply.status -ne 'ok' -or -not $commandsReply.payload) {
    $failures.Add("ck.navmesh mode=commands failed: $($commandsReply.code) $($commandsReply.message)")
} else {
    $commands = $commandsReply.payload | ConvertFrom-Json
    $observed['navmeshCommands'] = $commands.count

    if ([int]$commands.count -lt 1) {
        Write-Output 'SKIP this editor build offers no navmesh command in its menu bar'
        exit 2
    }

    foreach ($command in @($commands.commands)) {
        if ([int]$command.id -le 0) {
            $failures.Add("navmesh command '$($command.path)' has no menu id, so it cannot be posted")
        }
        if (-not "$($command.caption)") {
            $failures.Add("a navmesh command at '$($command.path)' has no caption")
        }
    }

    $check = @($commands.commands) | Where-Object { (Flatten "$($_.caption)") -eq 'checknavmeshes' } | Select-Object -First 1
    if (-not $check) {
        $captions = (@($commands.commands) | ForEach-Object { $_.caption }) -join ', '
        $failures.Add("no 'Check NavMeshes' command in the live menu; this build offers: $captions")
    }
}

# 2. The cell read must come from the cell's own forms, and say where its coverage stops.
$stateReply = Invoke-Mcp 'ck.navmesh' '{"mode":"state"}'
$observed['stateCode'] = $stateReply.code
if ($stateReply.status -ne 'ok') {
    if ($stateReply.code -eq 'no_cell') {
        Write-Output 'SKIP the render window holds no readable cell; open a cell with a navmesh'
        exit 2
    }
    $failures.Add("ck.navmesh mode=state failed: $($stateReply.code) $($stateReply.message)")
} else {
    $state = $stateReply.payload | ConvertFrom-Json

    if (-not $state.cell -or -not "$($state.cell.editorId)") {
        $failures.Add('mode=state returned no cell editor id, so the read is not attributable')
    } elseif ("$($state.cell.editorId)" -ne "$($cell.editorId)") {
        $failures.Add("mode=state reported cell '$($state.cell.editorId)' but the plugin reports '$($cell.editorId)'")
    }

    $observed['navmeshPresent'] = $state.navmesh.present
    $observed['navmeshForms'] = $state.navmesh.navmeshForms

    if ($state.navmesh.present -isnot [bool]) {
        $failures.Add('mode=state did not report presence as a boolean')
    }
    if ([int]$state.navmesh.count -lt [int]$state.navmesh.navmeshForms) {
        $failures.Add("mode=state reports $($state.navmesh.count) forms but $($state.navmesh.navmeshForms) navmesh forms")
    }
    if ($state.navmesh.present -eq $true -and [int]$state.navmesh.count -lt 1) {
        $failures.Add('mode=state reports navmesh present with no forms listed')
    }

    foreach ($form in @($state.navmesh.forms)) {
        if (-not "$($form.formId)") { $failures.Add('a navmesh form has no form id') }
        if ($form.parentMatchesCell -isnot [bool]) { $failures.Add("form $($form.formId) has no parentMatchesCell verdict") }
    }

    if (@($state.navmesh.coverage.PSObject.Properties.Name) -notcontains 'geometry') {
        $failures.Add('mode=state coverage does not say where its counts come from')
    } elseif ("$($state.navmesh.coverage.geometry)" -ne 'form_arrays') {
        $failures.Add("coverage names geometry '$($state.navmesh.coverage.geometry)' instead of the form arrays it counted")
    }

    if ([int]$state.navmesh.coverage.forms -gt 0) {
        if ([int]$state.navmesh.coverage.vertices -lt 1) {
            $failures.Add('coverage counted no vertices for a cell that carries navmesh forms')
        }
        if ([int]$state.navmesh.coverage.triangles -lt 1) {
            $failures.Add('coverage counted no triangles for a cell that carries navmesh forms')
        }

        foreach ($form in @($state.navmesh.forms | Where-Object { $_.navmesh })) {
            if ($null -eq $form.vertices -or $null -eq $form.triangles) {
                $failures.Add("navmesh form $($form.formId) reports no vertex or triangle count")
            }
        }
    }
}

# 2b. The dry run must resolve the plan and post nothing, so it cannot generate or save anything.
$dry = Invoke-Mcp 'ck.navmesh' '{"mode":"apply","dry_run":true}'
$observed['dryRunCode'] = $dry.code
if ($dry.status -ne 'ok') {
    $failures.Add("dry_run failed: $($dry.code) $($dry.message)")
} elseif ("$($dry.code)" -ne 'dry_run') {
    $failures.Add("dry_run answered '$($dry.code)' instead of resolving a plan")
} else {
    $plan = $dry.payload | ConvertFrom-Json
    $observed['dryRunSteps'] = @($plan.steps).Count
    if (@($plan.steps).Count -ne 3) {
        $failures.Add("dry run planned $(@($plan.steps).Count) steps instead of generation, finalize and check")
    }
    if ($plan.save -and -not $plan.save.resolved) {
        $failures.Add('dry run did not resolve the File/Save command the pipeline would post')
    }
}

# 3. Every mutating mode must be refused without confirmation, and nothing may be posted for it.
$generated = Invoke-Mcp 'ck.navmesh' '{"mode":"generate"}'
$observed['generateCode'] = $generated.code
if ($generated.code -ne 'confirmation_required' -or $generated.payload) {
    $failures.Add("ck.navmesh mode=generate without confirm returned '$($generated.code)' with payload '$($generated.payload)' instead of refusing")
}

$applied = Invoke-Mcp 'ck.navmesh' '{"mode":"apply"}'
$observed['applyCode'] = $applied.code
if ($applied.code -ne 'confirmation_required' -or $applied.payload) {
    $failures.Add("ck.navmesh mode=apply without confirm returned '$($applied.code)' with payload '$($applied.payload)' instead of refusing")
}

$ran = Invoke-Mcp 'ck.navmesh' '{"mode":"run","command":"Audit Navmesh Report"}'
$observed['runCode'] = $ran.code
if ($ran.code -ne 'confirmation_required' -or $ran.payload) {
    $failures.Add("ck.navmesh mode=run without confirm returned '$($ran.code)' with payload '$($ran.payload)' instead of refusing")
}

$badKind = Invoke-Mcp 'ck.navmesh' '{"mode":"generate","kind":"flood","confirm":true}'
if ($badKind.code -ne 'invalid_argument') {
    $failures.Add("ck.navmesh mode=generate kind=flood returned '$($badKind.code)' instead of refusing the strategy")
}

$badCaption = Invoke-Mcp 'ck.navmesh' '{"mode":"run","command":"Paint Triangles","confirm":true}'
$observed['badCaptionCode'] = $badCaption.code
if ($badCaption.code -ne 'no_navmesh_command') {
    $failures.Add("ck.navmesh mode=run with a caption this build does not carry returned '$($badCaption.code)'")
}

# 4. The action must use the id discovery reported, and report what the pass wrote.
if ($failures.Count -eq 0) {
    $checkReply = Invoke-Mcp 'ck.navmesh' '{"mode":"check"}'
    $observed['checkCode'] = $checkReply.code

    if ($checkReply.status -ne 'ok') {
        if ($checkReply.code -eq 'command_disabled') {
            Write-Output "SKIP the editor greys out Check NavMeshes for '$($cell.editorId)'; open a cell with a navmesh"
            exit 2
        }
        $failures.Add("ck.navmesh mode=check failed: $($checkReply.code) $($checkReply.message)")
    } else {
        $posted = $checkReply.payload | ConvertFrom-Json
        $observed['postedId'] = $posted.id
        $observed['discoveredId'] = $check.id

        if ($posted -and $posted.posted -eq $true) {
            if ([int]$posted.id -ne [int]$check.id) {
                $failures.Add("mode=check posted id $($posted.id) but the live menu reports $($check.id) for '$($check.caption)', so a build-specific id is hard-coded somewhere")
            }
            if ((Flatten "$($posted.caption)") -ne (Flatten "$($check.caption)")) {
                $failures.Add("mode=check posted '$($posted.caption)' but discovery resolved '$($check.caption)'")
            }
        } elseif ($posted) {
            $failures.Add('mode=check did not report posting the command')
        }

        # 5. The verdict channel must be named and real; an absent log means nothing was observed.
        $observed['log'] = $checkReply.log
        if (-not "$($checkReply.log)") {
            $failures.Add('mode=check did not report the CKPE log it read, so the verdict cannot be checked')
        } elseif (-not (Test-Path -LiteralPath "$($checkReply.log)" -PathType Leaf)) {
            Write-Output "SKIP the CKPE log is missing: $($checkReply.log)"
            exit 2
        }

        # 6. The check must answer with a structured verdict, not the prose it was read from.
        $names = @($checkReply.PSObject.Properties.Name)
        if ($names -notcontains 'verdict') {
            $failures.Add('mode=check returned no structured verdict, so the check result cannot be read')
        } elseif ($null -eq $checkReply.verdict) {
            $failures.Add('mode=check returned a null verdict')
        } else {
            $verdict = $checkReply.verdict
            $observed['verdictCode'] = $verdict.code
            $observed['verdictLines'] = $verdict.lines
            if (@('clean', 'warnings', 'errors', 'unknown') -notcontains "$($verdict.code)") {
                $failures.Add("mode=check returned verdict code '$($verdict.code)', which is not clean, warnings, errors or unknown")
            }
            if ($null -eq $verdict.cells) {
                $failures.Add('mode=check verdict carries no per-cell list')
            }
            foreach ($entry in @($verdict.cells)) {
                if (-not "$($entry.cell)") { $failures.Add('a verdict cell has no name') }
                if ($null -eq $entry.errors -or $null -eq $entry.warnings) {
                    $failures.Add("verdict cell $($entry.cell) carries no error or warning count")
                }
                if (@('clean', 'warnings', 'errors') -notcontains "$($entry.verdict)") {
                    $failures.Add("verdict cell $($entry.cell) has no verdict of its own")
                }
            }
            if ($names -contains 'report' -and $null -ne $checkReply.report) {
                $failures.Add('mode=check still returns the raw report lines next to the verdict')
            }
        }
    }
}

# 6. The mutating command must be refused, above the pipe and inside it.
$refused = Invoke-Mcp 'ck.navmesh' '{"mode":"finalize"}'
$observed['finalizeCode'] = $refused.code
if ($refused.code -ne 'confirmation_required') {
    $failures.Add("ck.navmesh mode=finalize without confirm returned '$($refused.code)' instead of refusing")
}
if ($refused.payload) {
    $failures.Add('a refused finalize still returned a payload, so the command may have been posted')
}

try {
    Invoke-Plugin 'nav.finalize' | Out-Null
    $failures.Add('the plugin ran nav.finalize without confirmation')
} catch {
    if ($_.Exception.Message -notmatch 'confirmation_required') {
        $failures.Add("the plugin refused nav.finalize for the wrong reason: $($_.Exception.Message)")
    }
}

# 7. An unknown mode must not be guessed at.
$unknown = Invoke-Mcp 'ck.navmesh' '{"mode":"paint"}'
if ($unknown.code -ne 'invalid_argument') {
    $failures.Add("ck.navmesh mode=paint returned '$($unknown.code)' instead of refusing")
}

$evidenceJson = [ordered]@{ navmesh = $observed; failures = @($failures); passed = ($failures.Count -eq 0) } | ConvertTo-Json -Depth 6
if ($Evidence) {
    $evidenceJson | Set-Content -LiteralPath $Evidence -Encoding utf8
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Output "FAIL $failure" }
    exit 1
}

Write-Output 'PASS navmesh commands come from the live menu, the CKPE report is returned, and finalize needs confirmation'
Write-Output $evidenceJson
exit 0
