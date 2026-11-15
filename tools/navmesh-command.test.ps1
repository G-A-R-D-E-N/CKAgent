# Regression: the navmesh tools must keep addressing the editor's commands by caption, because the menu
# ids in the docs belong to 1.11.246.0 alone. A hard-coded id still posts successfully - it just runs
# whatever command that number means on another build - so nothing downstream would notice.
#
# The second regression is the finalize gate: without it, one call changes the loaded cell's navmesh
# data with no way to tell whether the caller meant it.
#
# The third is real data: run with -Live against a running editor, this reads the loaded cell's navmesh
# forms from the plugin and fails if any form reports null geometry while its array head is merely empty.
#
# The fourth is the check itself: -Live -Check runs Check NavMeshes through the real MCP server and fails
# when the verdict is unknown, names no finding it claims, disagrees with its own totals, or does not name
# a finding matched by -Expect (a regex over each finding's message or source line).
#
# -Live -Apply drives a staged apply against the loaded cell and fails when a stage answer, or an independent
# call made while a stage is pending, takes longer than its budget: the stages exist so no call sleeps on the
# editor thread, and a stage that waited would freeze the editor for its whole deadline.
# -Live -ApplyRetry starts a run, aborts it mid-sequence, and requires the next call to start at generate
# rather than resume the dead run, so a failed apply is recoverable without restarting the editor.
# A Recast generation empties the forms it replaces, so a generated cell is the case that used to look
# partly unknown. Nothing live runs without -Live, so the static pass is unchanged.

param(
    [switch]$Live,
    [string]$Cell = 'DmndRadio01',
    [switch]$Generate,
    [switch]$Check,
    [switch]$Apply,
    [switch]$ApplyRetry,
    [string]$ApplyKind = 'recast',
    [int]$StageBudgetMs = 2000,
    [int]$ProbeBudgetMs = 2000,
    [int]$ApplyBudgetSeconds = 420,
    [string[]]$Expect = @(),
    [string]$McpDll = '',
    [string]$GamePath = $env:CKAGENT_GAME,
    [int]$CheckTimeoutMs = 120000,
    [string]$Pipe = 'ckagent',
    [int]$LiveTimeoutSeconds = 240,
    [string]$Plugin = 'plugin/AgentNavmesh.cpp',
    [string]$Read = 'plugin/AgentNavmeshRead.cpp',
    [string]$CheckSource = 'plugin/AgentNavmeshCheck.cpp',
    [string]$Dispatcher = 'plugin/Agent.cpp',
    [string]$Ui = 'plugin/AgentEditorUi.cpp',
    [string]$Tool = 'CKAgent.Mcp/NavmeshTools.cs',
    [string]$VerdictSource = 'CKAgent.Mcp/NavmeshVerdict.cs',
    [string]$FindingsSource = 'plugin/AgentNavmeshFindings.cpp',
    [string]$Build = 'plugin/CMakeLists.txt',
    [string]$Evidence = ''
)

$ErrorActionPreference = 'Stop'
$failures = New-Object System.Collections.Generic.List[string]
$observed = [ordered]@{}

. (Join-Path $PSScriptRoot 'navmesh-command.live.ps1')

function Read-Source([string]$path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $failures.Add("source not found: $path")
        return ''
    }
    return Get-Content -LiteralPath $path -Raw
}











$pluginText = Read-Source $Plugin
$readText = Read-Source $Read
$pluginText = $pluginText + [Environment]::NewLine + $readText
$dispatcherText = Read-Source $Dispatcher
$uiText = Read-Source $Ui
$toolText = Read-Source $Tool
# Constraint: the generation driver lives in its own file, so the assertions read it beside the tool.
$loadSource = Join-Path (Split-Path -Parent $Tool) 'NavmeshCellLoad.cs'
if (Test-Path -LiteralPath $loadSource -PathType Leaf) {
    $toolText = $toolText + [Environment]::NewLine + (Get-Content -LiteralPath $loadSource -Raw)
}

$batchSource = Join-Path (Split-Path -Parent $Tool) 'NavmeshBatch.cs'
if (Test-Path -LiteralPath $batchSource -PathType Leaf) {
    $toolText = $toolText + [Environment]::NewLine + (Get-Content -LiteralPath $batchSource -Raw)
}

$generateSource = Join-Path (Split-Path -Parent $Tool) 'NavmeshGenerate.cs'
if (Test-Path -LiteralPath $generateSource -PathType Leaf) {
    $toolText = $toolText + [Environment]::NewLine + (Get-Content -LiteralPath $generateSource -Raw)
}

# The save is what turns a mesh into delivery, so its coordinator is checked beside the tool too.
$saveSource = Join-Path (Split-Path -Parent $Tool) 'SaveCoordinator.cs'
if (Test-Path -LiteralPath $saveSource -PathType Leaf) {
    $toolText = $toolText + [Environment]::NewLine + (Get-Content -LiteralPath $saveSource -Raw)
}
# The pipe client is what made a cover pass wait past the caller's patience, so it is checked beside the tool.
$clientSource = Join-Path (Split-Path -Parent $Tool) 'PluginClient.cs'
$clientText = if (Test-Path -LiteralPath $clientSource -PathType Leaf) { Get-Content -LiteralPath $clientSource -Raw } else { '' }
if ($clientText -eq '') {
    $failures.Add("$clientSource is missing, so nothing bounds a call that the editor never answers")
} elseif ($clientText -notmatch 'plugin_busy') {
    $failures.Add('the pipe client waits for a reply with no deadline, so one busy editor holds the caller until the client timeout')
} elseif ($clientText -notmatch 'CallDeadlineMs') {
    $failures.Add('the pipe client has no named deadline, so the read timeout cannot be reviewed or tightened')
}

$verdictText = Read-Source $VerdictSource
$findingsText = Read-Source $FindingsSource
$buildText = Read-Source $Build
$checkText = Read-Source $CheckSource

if ($pluginText) {
    # The ids the findings recorded are the ones a "simplification" would paste in.
    foreach ($id in '40568', '40608', '40560', '40862', '40561') {
        if ($pluginText -match "\b$id\b") {
            $failures.Add("$Plugin contains the build-specific menu id $id; navmesh commands must be resolved from the live menu")
        }
    }

    foreach ($required in @(
        @('GetMenuStringW', 'the command caption must be read from the live menu'),
        @('GetMenuItemInfoW', 'the id and enabled state must be read from the live menu item'),
        @('MFS_GRAYED', 'a greyed command must be seen before it is posted, not posted and hoped for'),
        @('command_disabled', 'a disabled navmesh command must be refused with a reason'),
        @('PostMessageW\(frame, WM_COMMAND', 'the resolved id must be posted to the editor frame'),
        @('confirmation_required', 'nav.finalize must refuse an unconfirmed call'),
        @('Finalize Cell NavMeshes', 'the finalize action must name the editor command it runs'),
        @('Check NavMeshes', 'the check action must name the editor command it runs'),
        @('IsNavMesh', 'the cell read must ask whether the cell carries navmesh'),
        @('GetNavMeshesCount', 'the presence read must report how many navmesh forms the cell holds'),
        @('QNavmeshID', 'each navmesh form must report the id the editor uses'),
        @('GetSaveParentCell', 'each navmesh form must report the cell it belongs to'),
        @('!cell->IsNavMesh\(\)', 'the count dereferences the array, so IsNavMesh must be checked first'),
        @('kNavVerticesOffset', 'coverage must count vertices from the form, not from a guess'),
        @('kNavTrianglesOffset', 'coverage must count triangles from the form, not from a guess'),
        @('ReadNavArraySize', 'the array count must be read through one bounds-checked helper'),
        @('Recast-Based Generation', 'generation must name the editor command it runs'),
        @('Havok-Based Generation', 'the havok strategy must name the editor command it runs'),
        @('nav\.apply', 'the apply sequence must exist as its own verb'),
        @('nav\.plan', 'the dry run must resolve the plan without posting'),
        @('nav\.save', 'the pipeline must be able to save through the editor command'),
        @('ApplyRun', 'the apply pipeline must keep its stage between calls, because no call may sleep'),
        @('FindButtonByText\(dialog, "OK"\)', 'the generation dialog must be accepted through its own OK button'),
        @('generation_incomplete', 'a dialog that never closes must stop the sequence, not let finalize race a modal'),
        @('no_geometry_change', 'apply must fail when generation moved no geometry, instead of reporting success'),
        @('File/Save', 'the save must be resolved by menu path, not by a build id'),
        @('ProbeGenerationDialog', 'dialog acceptance must be reachable as a single non-blocking pass'),
        @('nav\.accept', 'acceptance must be its own verb, because the handler cannot sleep while the dialog opens'),
        @('captions\[1\] = "Finalize Cell NavMeshes"', 'apply must post finalize before check'),
        @('StripCellGeometry', 'emptying a cell must be a plugin verb, because no caption zeroes a navmesh outside the edit session'),
        @('kNavArraySizeOffset', 'the strip must zero the count the readers use, or nav.state keeps reporting the old geometry'),
        @('JsonKey\(', 'MSVC mis-lexes the escaped cover key literal, so the plugin stops building unless keys are built from Quote')
    )) {
        if ($pluginText -notmatch $required[0]) {
            $failures.Add("$Plugin no longer matches /$($required[0])/: $($required[1])")
        }
    }

    # Regression: a Recast generation empties the navmesh forms it replaces, leaving a zero element count on
    # their array head. Reading that as unknown is what made every superseded form answer null vertices,
    # triangles and area while the generated form read fine.
    $triState = [regex]::Match($readText, 'enum class NavArrayRead[\s\S]*?return NavArrayRead::Present;\s*\}')
    $observed['emptyArray'] = if ($triState.Success) { 'found' } else { 'missing' }
    if (-not $triState.Success) {
        $failures.Add("$Read has no NavArrayRead tri-state, so an emptied array reads the same as an unreadable one")
    } elseif ($triState.Value -notmatch 'if \(count == 0\) return NavArrayRead::Empty;') {
        $failures.Add("$Read never maps a zero element count to Empty, so a replaced navmesh still answers null geometry")
    }

    foreach ($required in @(
        @('GeometryName', 'each form must name whether its geometry was read, emptied or unreadable'),
        @('out\.empty = true', 'an emptied pair must be reported as empty rather than as unreadable'),
        @('\\\"geometry\\\":', 'the per-form reply must carry the geometry state, so a replaced form is visible'),
        @('emptyForms', 'the coverage must count emptied forms instead of voiding the whole total'),
        @('form\.coverOk', 'each form must report its own cover, or the meshes in one cell cannot be compared one by one'),
        @('unreadableForms', 'the coverage must count the forms it could not read'),
        @('unreadableForms == 0', 'the area total must only depend on unreadable forms, not on emptied ones')
    )) {
        if ($readText -notmatch $required[0]) {
            $failures.Add("$Read no longer matches /$($required[0])/: $($required[1])")
        }
    }

    # Regression: the check was classified from a log offset read before its report existed, so a run whose
    # report was written a moment later came back unknown. The check now carries the lines it caused.
    if ($checkText) {
        foreach ($required in @(
            @('CheckRun', 'the check must keep its run between calls, because no call may sleep'),
            @('kCheckSettleMs', 'a check that never settles must still answer, with a deadline rather than a sleep'),
            @('\\\"lines\\\":', 'the reply must carry the report lines the check produced'),
            @('\\\"report\\\":', 'the reply must name the report it read, so an empty one is visible'),
            @('startOffset', 'the offset must be taken before the post, or the check reads the previous report')
        )) {
            if ($checkText -notmatch $required[0]) {
                $failures.Add("$CheckSource no longer matches /$($required[0])/: $($required[1])")
            }
        }

        $observed['checkSleeps'] = if ($checkText -match '\bSleep\(') { 'found' } else { 'missing' }
        if ($checkText -match '\bSleep\(') {
            $failures.Add("$CheckSource sleeps inside the pipe handler, which runs on the editor thread, so the editor freezes")
        }

        $offsetAt = $checkText.IndexOf('startOffset = FileSize')
        $postAt = $checkText.IndexOf('PostNavmeshCommand(')
        $observed['checkOffset'] = if ($offsetAt -ge 0 -and $postAt -ge 0 -and $offsetAt -lt $postAt) { 'found' } else { 'missing' }
        if ($offsetAt -lt 0 -or $postAt -lt 0 -or $offsetAt -gt $postAt) {
            $failures.Add("$CheckSource takes its report offset after posting, so it would read the previous check's lines")
        }

        if ($pluginText -notmatch 'navmeshcheck::RunCheck\(') {
            $failures.Add("$Plugin never calls the check module, so nav.check answers without a report")
        }
    }

    # Regression: the plugin classifies a check as well as the server, so one finding could read as an error to
    # a raw pipe client and a warning to the MCP client. Every server rule must be recognised here, as it is.
    if ($findingsText -and $verdictText) {
        $rules = [regex]::Matches($findingsText, '\{\s*"([^"]+)",\s*Severity::(Error|Warning)\s*\}')
        $serverRules = [regex]::Matches($verdictText, 'Make\(@"([^"]*)",\s*"(error|warning)"')
        $observed['findingRules'] = $rules.Count
        $observed['verdictRules'] = $serverRules.Count

        if ($rules.Count -eq 0) {
            $failures.Add("$FindingsSource declares no finding rule, so the pipe reply cannot name a severity")
        }

        foreach ($serverRule in $serverRules) {
            $pattern = $serverRule.Groups[1].Value.ToLowerInvariant()
            $severity = $serverRule.Groups[2].Value
            $match = $rules | Where-Object { $pattern.Contains($_.Groups[1].Value.ToLowerInvariant()) } | Select-Object -First 1

            if (-not $match) {
                $failures.Add("$FindingsSource does not recognise the finding /$($serverRule.Groups[1].Value)/, so a raw pipe client would miss it")
                continue
            }

            $pluginSeverity = if ($match.Groups[2].Value -eq 'Error') { 'error' } else { 'warning' }
            if ($pluginSeverity -ne $severity) {
                $failures.Add("$FindingsSource calls '$($match.Groups[1].Value)' a $pluginSeverity while $VerdictSource calls it a $severity")
            }
        }

        foreach ($rule in $rules) {
            if (-not $verdictText.ToLowerInvariant().Contains($rule.Groups[1].Value.ToLowerInvariant())) {
                $failures.Add("$FindingsSource matches '$($rule.Groups[1].Value)', which no message in $VerdictSource writes")
            }
        }

        foreach ($info in [regex]::Matches($verdictText, 'new\(@"([^"]*)"')) {
            $literal = $info.Groups[1].Value -replace '^\^\\s\*', '' -replace '\\s\*\$$', ''
            if (-not $findingsText.ToLowerInvariant().Contains($literal.ToLowerInvariant())) {
                $failures.Add("$FindingsSource does not treat /$literal/ as check output, so a clean report would read as unknown")
            }
        }

        foreach ($required in @(
            @($checkText, 'navmeshfindings::FieldsJson\(g_check\.lines\)', 'the check reply must carry the findings, not only its lines'),
            @($checkText, 'navmeshfindings::FieldsJson\(g_last\.lines\)', 'the staged apply report must carry the findings too'),
            @($findingsText, '\\"severity\\":', 'each finding must name its severity'),
            @($findingsText, '\\"cell\\":', 'each finding must name the cell it belongs to')
        )) {
            if (-not $required[0] -or $required[0] -notmatch $required[1]) {
                $failures.Add("$FindingsSource no longer matches /$($required[1])/: $($required[2])")
            }
        }
    }

    # Regression: cover lives in the per-edge bits of the triangle flags word, so a coverage total that only
    # summed triangles could not say whether a mesh carries cover at all.
    if ($readText) {
        foreach ($required in @(
            @('kNavCoverEdgeShift', 'the cover bits must be read from the shifted flags word'),
            @('kNavCoverEdgeMask', 'one mask must describe all three edge bits'),
            @('out\.cover\.edge01', 'the per-edge counts must stay apart, not collapse into one total'),
            @('cover\.heightSet', 'the cover height must be counted beside the flags, so the two can be compared'),
            @('\\\"edgeCover\\\":', 'nav.state coverage must carry the per-edge cover counts'),
            @('\\\"edge01\\\":', 'a cover count must be named after the link it sits on, so a reader does not map bit numbers to edges')
        )) {
            if ($readText -notmatch $required[0]) {
                $failures.Add("$Read no longer matches /$($required[0])/: $($required[1])")
            }
        }

        $countAt = $readText.IndexOf('++counted;')
        $coverAt = $readText.IndexOf('out.cover.any')
        $observed['coverInWalk'] = if ($countAt -ge 0 -and $coverAt -gt $countAt) { 'found' } else { 'missing' }
        if ($countAt -lt 0 -or $coverAt -lt 0 -or $coverAt -lt $countAt) {
            $failures.Add('cover is counted outside the accepted-triangle walk, so its counts cannot be compared with areaTriangles')
        }
    }

    # The gate must guard the action, not merely appear somewhere in the file.
    # The apply sequence must keep its order: generation, then finalize, then check.
    # Constraint: $apply is the -Apply switch, so the match needs its own name or the assignment throws.
    $applyOrderMatch = [regex]::Match($pluginText, 'captions\[0\] = generation;[\s\S]*?captions\[2\] = "Check NavMeshes";')
    $observed['applyOrder'] = if ($applyOrderMatch.Success) { 'found' } else { 'missing' }
    if (-not $applyOrderMatch.Success) {
        $failures.Add("$Plugin has no apply caption sequence, so generation, finalize and check could be reordered")
    }

    # Regression: generation is dialog-gated, so a pipeline that posts and moves on reports success while the
    # old arrays are still in place and the save gate sees a stale check. The gate must run inside apply, and it
    # must come before the success payload is built, or a no-op still answers ok.
    # Constraint: the body ends at the next function, because a nested block can close at the same indent.
    $applyStart = $pluginText.IndexOf('bool RunApply(')
    $applyStop = if ($applyStart -ge 0) { $pluginText.IndexOf("`n`t`tbool ", $applyStart + 10) } else { -1 }
    $applyBody = [pscustomobject]@{ Success = ($applyStart -ge 0 -and $applyStop -gt $applyStart); Value = '' }
    if ($applyBody.Success) { $applyBody.Value = $pluginText.Substring($applyStart, $applyStop - $applyStart) }
    $observed['applyAcceptsDialog'] = if ($applyBody.Success -and $applyBody.Value -match 'ProbeGenerationDialog') { 'found' } else { 'missing' }
    if (-not $applyBody.Success) {
        $failures.Add("$Plugin has no RunApply body, so the generation dialog could go unanswered")
    } else {
        if ($applyBody.Value -notmatch 'ProbeGenerationDialog') {
            $failures.Add('apply posts generation without accepting its dialog, so generation never runs and the finalize step runs against a modal')
        }

        # Regression: the pipe handler runs on the editor thread, and a sleep there froze the editor so the
        # generation dialog could never open and the call never returned.
        $observed['applySleeps'] = if ($applyBody.Value -match '\bSleep\(') { 'found' } else { 'missing' }
        if ($applyBody.Value -match '\bSleep\(') {
            $failures.Add('apply sleeps inside the pipe handler, which runs on the editor thread, so the editor freezes and its dialog never opens')
        }

        # Regression: a staged reply that answers ok with no stage would look like a finished pipeline.
        # Constraint: the pending payload belongs to the step helper, so it is asserted on the file, not on RunApply.
        $stagedProgress = $pluginText -match '\\\"pending\\\":true' -and $pluginText -match 'ApplyRun'
        $observed['applyStages'] = if ($stagedProgress) { 'found' } else { 'missing' }
        if (-not $stagedProgress) {
            $failures.Add('apply no longer reports staged progress, so a caller cannot tell a pending run from a finished one')
        }

        # Regression: a pending payload that apply never answers with looks like progress in the source only.
        if ($applyBody.Success -and $applyBody.Value -notmatch 'ApplyPendingJson\(') {
            $failures.Add('apply never answers with a pending step, so a caller cannot tell a running apply from a finished one')
        }

        if ($applyBody.Value -notmatch 'no_geometry_change') {
            $failures.Add('apply reports success even when generation changed no geometry, so a no-op looks like a real run')
        } elseif ($applyBody.Value.IndexOf('payload = "{\"stage\":\"done\"') -lt $applyBody.Value.IndexOf('no_geometry_change')) {
            $failures.Add('apply builds its success payload before the no-geometry-change gate, so a no-op still answers ok')
        }

        if ($applyBody.Value -notmatch 'ReadTotals\(cell, after') {
            $failures.Add('apply does not re-read the cell after generation, so it cannot tell whether anything was built')
        }

        # Regression: apply classified a log slice taken before its check stage wrote anything, while mode=check
        # classified the check's own report, so one check could read clean in one call and unknown in the other.
        $captureAt = $applyBody.Value.IndexOf('navmeshcheck::BeginCapture')
        $checkPostAt = $applyBody.Value.IndexOf('PostApplyStep(2')
        $observed['applyCapture'] = if ($captureAt -ge 0 -and $checkPostAt -gt $captureAt) { 'found' } else { 'missing' }
        if ($captureAt -lt 0 -or $checkPostAt -lt 0 -or $checkPostAt -lt $captureAt) {
            $failures.Add('apply posts its check before opening the capture, so the report it hands on would miss the lines that check wrote')
        }

        if ($applyBody.Value -notmatch 'navmeshcheck::LastReportJson') {
            $failures.Add('apply does not carry the captured report, so its verdict still comes from a log re-read')
        }

        if ($applyBody.Value -notmatch 'navmeshcheck::LastVerdictFields') {
            $failures.Add('apply does not surface the verdict its own check produced, so the result rests on the server re-parsing the lines')
        }

        # Regression: a run that failed or timed out mid-stage left its generation dialog up and its check
        # capture open, so the editor stayed modal and the next nav.check collected a report nobody posted;
        # only restarting the editor cleared it.
        $observed['applyAbandons'] = if ($applyBody.Value -match 'AbandonApply\(frame\)') { 'found' } else { 'missing' }
        if ($applyBody.Value -notmatch 'AbandonApply\(frame\)') {
            $failures.Add('apply drops a failed run without dismissing the dialog it opened, so a stalled run leaves the editor modal')
        }

        $bareResets = [regex]::Matches($applyBody.Value, 'ResetApply\(\);').Count
        $observed['applyBareResets'] = $bareResets
        if ($bareResets -gt 1) {
            $failures.Add("apply drops a run with a bare reset in $bareResets places, so a dialog or capture it opened can survive the failure")
        }

        if ($pluginText -notmatch 'FirstWord\(args\) == "abort"') {
            $failures.Add("$Plugin has no nav.apply abort, so a stalled run can only be cleared by waiting out the stale window")
        }

        if ($checkText -and $checkText -notmatch 'void Abandon\(\)') {
            $failures.Add("$CheckSource cannot drop a capture, so an abandoned apply leaves the next check collecting a report nobody posted")
        }
    }

    $finalize = [regex]::Match($pluginText, 'tool == "nav\.finalize"[\s\S]*?unknown_tool')
    $observed['finalizeGate'] = if ($finalize.Success) { 'found' } else { 'missing' }
    if (-not $finalize.Success) {
        $failures.Add("$Plugin has no nav.finalize branch before the unknown_tool fallthrough")
    } else {
        if ($finalize.Value -notmatch 'confirmation_required') {
            $failures.Add('the nav.finalize branch does not refuse an unconfirmed call, so one call changes cell data')
        }
        # The gate must compare the caller's own answer: a guard that is always false leaves the refusal
        # in the file as dead code while the command runs anyway.
        if ($finalize.Value -notmatch 'Lower\(args\) != "confirm"') {
            $failures.Add('the nav.finalize branch no longer compares args to confirm, so the refusal can be bypassed')
        }
    }
}

if ($dispatcherText) {
    $routed = $dispatcherText -match 'tool\.rfind\("nav\."'
    $observed['dispatched'] = if ($routed) { 'found' } else { 'missing' }
    if (-not $routed) {
        $failures.Add("$Dispatcher never routes nav.* tools, so none of them can be reached")
    }

    if ($dispatcherText -notmatch 'tool\.rfind\("place\."') {
        $failures.Add("$Dispatcher never routes place.* tools, so placement cannot be reached")
    }

    # Regression: the server gave up on a stalled apply without dropping it, so the next run resumed the dead
    # sequence and the editor kept the dialog the failed run left behind.
    if ($toolText) {
        $observed['applyAbort'] = if ($toolText -match 'nav\.apply abort' -and $toolText -match 'case "abort"') { 'found' } else { 'missing' }
        if ($toolText -notmatch 'nav\.apply abort') {
            $failures.Add("$Tool never aborts a failed apply, so a stalled run stays open in the plugin")
        }

        if ($toolText -notmatch 'case "abort"') {
            $failures.Add("$Tool exposes no abort mode, so a caller cannot clear a stalled apply on demand")
        }

        # Regression: apply saved a cell whose result came only from its own parse of the lines, so a verdict
        # the plugin had already produced could disagree with the report and nothing noticed.
        if ($toolText -notmatch 'PluginVerdict\(') {
            $failures.Add("$Tool never reads the verdict the plugin reported, so apply trusts only its own parse")
        }

        if ($toolText -notmatch 'verdict_mismatch') {
            $failures.Add("$Tool has no verdict_mismatch outcome, so a disagreement between the plugin and the parse saves the cell anyway")
        }
    }
}

if ($uiText) {
    if ($uiText -notmatch '#include "AgentNavmesh\.h"') {
        $failures.Add("$Ui does not include AgentNavmesh.h")
    }
    if ($uiText -notmatch 'RunNavmeshTool\(tool, args') {
        $failures.Add("$Ui never calls RunNavmeshTool, so the routed nav.* tools answer unknown_tool")
    }
    if ($uiText -notmatch 'RunPlaceTool\(tool, args') {
        $failures.Add("$Ui never calls RunPlaceTool, so the routed place.* tools answer unknown_tool")
    }
}

if ($toolText) {
    foreach ($required in @(
        @('ck\.navmesh', 'the MCP tool must keep its name'),
        @('confirmation_required', 'the server must refuse an unconfirmed finalize'),
        @('nav\.check', 'mode=check must reach the plugin'),
        @('nav\.state', 'mode=state must reach the plugin'),
        @('nav\.run \{Caption\(kind\)\}', 'mode=generate must post the generator caption itself, so the server decides when the dialog is pressed'),
        @('nav\.apply', 'mode=apply must reach the plugin'),
        @('nav\.run', 'mode=run must reach the plugin'),
        @('triangles', 'the state reply must report triangle counts taken from the form arrays'),
        @('CkpeLogPath', 'the verdict must be read from the CKPE log this build writes'),
        @('report', 'the reply must carry what the pass wrote, so an empty pass is visible'),
        @('nav\.plan', 'the dry run must reach the plugin instead of being simulated'),
        @('NavmeshVerdictParser', 'the check must be turned into a verdict, not returned as prose'),
        @('not_clean', 'the not-clean branch must exist, so a failed check cannot save')
    )) {
        if ($toolText -notmatch $required[0]) {
            $failures.Add("$Tool no longer matches /$($required[0])/: $($required[1])")
        }
    }

    # The verdict replaces the raw lines, so a caller must never have to parse the log itself.
    if ($toolText -match 'mode=check returned|ReadAfter\(offset, rows\)\)\s*$') {
        $failures.Add("$Tool still answers the check with raw report lines")
    }
    if ($toolText -notmatch 'Verdict = verdict') {
        $failures.Add("$Tool computes a verdict but never returns it")
    }

    # Regression: the verdict was parsed from a log slice read at an offset taken before the check ran, so a
    # slow report answered unknown even though the check itself had written its findings.
    $checkBody = [regex]::Match($toolText, 'private NavmeshResult Check\(\)[\s\S]*?\n    \}')
    $observed['checkUsesReport'] = if ($checkBody.Success -and $checkBody.Value -match 'ReportLines\(payload\)') { 'found' } else { 'missing' }
    if (-not $checkBody.Success) {
        $failures.Add("$Tool has no check action, so nothing surfaces the verdict")
    } else {
        if ($checkBody.Value -notmatch 'ReportLines\(payload\)') {
            $failures.Add('the check parses a log slice instead of the report the check itself returned')
        }

        if ($checkBody.Value -notmatch 'check_incomplete') {
            $failures.Add('a check that never settles has no distinct answer, so it could pass as clean')
        }
    }

    # The save must sit behind the verdict, not merely next to it: a reordering would save a failed check.
    $applyCase = [regex]::Match($toolText, 'case "apply":[\s\S]*?case "run":')
    $observed['saveGate'] = if ($applyCase.Success) { 'found' } else { 'missing' }
    if (-not $applyCase.Success) {
        $failures.Add("$Tool has no apply branch, so nothing gates the save")
    } elseif ($applyCase.Value -notmatch 'nav\.save confirm') {
        $failures.Add("$Tool never posts nav.save from the apply pipeline")
    } elseif ($applyCase.Value.IndexOf('verdict.Code == "clean"') -lt 0) {
        $failures.Add("$Tool posts a save from apply without computing a verdict first")
    } elseif ($applyCase.Value.IndexOf('nav.save confirm') -lt $applyCase.Value.IndexOf('verdict.Code == "clean"')) {
        $failures.Add('the save is posted before the check verdict is computed, so a failed check could still save')
    }

    # Regression: apply and check must classify one set of lines, or the same check reads clean in one call and
    # unknown in the other.
    $observed['applyUsesReport'] = if ($applyCase.Success -and $applyCase.Value -match 'ReportLines\(payload\)') { 'found' } else { 'missing' }
    if ($applyCase.Success -and $applyCase.Value -notmatch 'ReportLines\(payload\)') {
        $failures.Add('apply parses a log slice instead of the report its check stage captured, so it can disagree with mode=check')
    }

    # Regression: a navmesh pass over a whole plugin was one apply per cell with the user loading and saving
    # each by hand. The sweep modes are what turn that into a tool answer, so losing one puts the work back.
    $sweep = @(
        @('case "audit":', 'mode=audit must exist, or nothing tells a user which cells still need navmesh work'),
        @('case "batch":', 'mode=batch must exist, or a multi-cell pass is one apply per cell with the user loading each'),
        @('case "cover":', 'mode=cover must exist, or the cover group is reachable only by knowing its captions'),
        @('ui\.cellload ', 'the sweep must open each cell through the editor list instead of assuming one is loaded'),
        @('Navmesh\("apply", true', 'the batch must run the same pipeline as mode=apply instead of restating the stages, or the save gate drifts'),
        @('Refused\("mode=batch', 'batch changes cell data, so it must refuse an unconfirmed call'),
        @('Refused\("mode=cover', 'cover changes cell data, so it must refuse an unconfirmed call'),
        @('CoverState\(out string coverError\)', 'cover answers nothing itself, so its counts must be read before and after the command'),
        @('WaitForModal\(GenerationDialogTitle\(\)', 'generation must wait for the dialog the editor opens after the caption, or its OK press lands on nothing'),
        @('GenerationDialogTitle\(\)', 'the generator dialogs differ per generation kind, so the title must be resolved rather than assumed'),
        @('sameReadings >= 2', 'a build empties the arrays before it writes them, so only a settled count proves the pass finished'),
        @('wait_seconds', 'the wait must be bounded, or one call can hold the editor forever'),
        @('case "strip":', 'mode=strip must exist, or emptying a cell for a build test needs hand work in the edit session'),
        @('nav\.strip confirm', 'the strip must go through the plugin verb, since no caption empties a cell outside the edit session'),
        @('IsAliasRow', 'the Cell View lists a dirty cell twice, so the sweep must merge rows by form id or audit the same cell as two'),
        @('CleanId', 'the editor marks a dirty cell with a trailing star, so one form must not report two names and two geometry states'),
        @('case "load":', 'an exterior cell must be loadable by name, or the whole sweep is interiors only'),
        @('UseWorldSpace\(', 'the Cell View lists one worldspace at a time, so an exterior load must select its worldspace before it opens a cell'),
        @('response_code\("no_worldspace"\)|no_worldspace"', 'a worldspace the Cell View does not list must fail by name, not leave whatever was selected loaded'),
        @('no_worldspace_combo', 'without the combo lookup a missing worldspace selector would silently read an interior cell'),
        @('interior = Member', 'audit must say whether the cell is an exterior, or exterior evidence cannot be told apart from interior'),
        @('busyReads', 'a busy read during a build is not a cell with no geometry, or a large exterior build is called incomplete while it is still running'),
        @('while \(clock\.ElapsedMilliseconds < wait \* 1000L && !built\)', 'the wait window is the only limit on a build, because the pipe goes quiet while the editor builds and a dead-pipe guard ended builds that were still running'),
        @('Stopwatch clock', 'the wait must be measured in wall time, because a busy read spends seconds the sleep count never sees'),
        @('batchKind == "mesh"', 'the batch must have a mesh kind that strips and regenerates a worldspace cell, or a city sweep is one call per cell by hand'),
        @('MeshBatch\(', 'the mesh batch needs its own pass, because the apply batch saves and this one must only mesh'),
        @('form_mismatch', 'a batch row must prove it became the loaded cell by form id, or it can mesh one cell and report another'),
        @('ui\.notify \{list\} dblclk', 'exterior cells share an EditorID, so a row must open by its own list index instead of by name'),
        @('FindCellViewList\(', 'the row index only means something inside the Cell View list, so that list must be resolved before opening a row'),
        @('while \(loadClock\.ElapsedMilliseconds < LoadWindowMs\)', 'a load is proven on a wall-clock window, because a fixed read count called a cell load_timeout that landed seconds later'),
        @('audit = new', 'the batch must carry its own per-cell audit read, or agreement needs a second invocation'),
        @('audit\.FormId\.Equals\(loaded\.FormId', 'the audit inside the batch must confirm the same form id, or it can attest a different cell'),
        @('loaded = audit\.FormId\.Equals', 'a batch row must say loaded by form id, not by the editor id it shares with its neighbours'),
        @('SavePlugin\(', 'a generated mesh must be written to a plugin, or the caller is left with work only the editor holds'),
        @('persisted', 'a cell must state whether its mesh reached a plugin, or a memory-only mesh reads as delivered'),
        @('save_failed', 'a cell whose save failed must be reported as unfinished instead of complete'),
        @('Select Target Plugin', 'the editor cannot write a loaded master in place, so the save path must answer its target-plugin dialog'),
        @('savedAtUtc', 'the result must say when the plugin was written, not only that a save was attempted'),
        @('save_as', 'the target plugin is chosen by the caller, so it is carried into the save instead of assumed'),
        @('RunUiaHelper\(', 'the target-plugin dialog must be answered by the tool, or the save needs a person at the keyboard'),
        @('uia-target-plugin\.ps1', 'UI Automation inside the editor cannot reach its own dialog, so the helper runs outside that process'),
        @('if \(!LoadCell\(row, out string loadCode, out string loadMessage\)\)', 'a cell load answers before the render window settles, so the sweep must prove the row loaded before reading its geometry'),
        @('load_timeout', 'a row that never becomes the loaded cell must say so, not report the previous cell geometry under its name'),
        @('int triangles = Number\(coverage, "triangles"\);', 'audit must count triangles from the same coverage block nav.state reports, or the two can disagree about one cell'),
        @('"Find Cover Edges"', 'cover=generate must post the cell command; the All Spaces caption runs over the whole load order and stalls the editor'),
        @('CoverStatus\(', 'a cover pass returns before the editor finishes, so the counts must be readable from a separate status call'),
        @('return new NavmeshResult\("ok", "posted"', 'a cover pass that outlasts its budget must say posted, or the caller cannot tell a finished pass from a running one'),
        @('triangles == 0', 'a build leaves an empty placeholder form, so needing work is a cell with no triangles, not one with an empty form')
    )
    $missingSweep = @($sweep | Where-Object { $toolText -notmatch $_[0] })
    $observed['sweepModes'] = if ($missingSweep.Count -eq 0) { 'found' } else { 'missing' }
    foreach ($required in $missingSweep) {
        $failures.Add("$Tool no longer matches /$($required[0])/: $($required[1])")
    }
}

if ($verdictText) {
    # Every pattern below is a message template this editor build carries, so dropping one loses a finding.
    foreach ($required in @(
        @('NavmeshVerdict', 'the verdict type the reply carries must exist'),
        @('Parse\(', 'the parser must expose one entry point'),
        @('"unknown"', 'silence must map to unknown, never to a clean pass'),
        @('degenerate', 'the degenerate-triangle template must stay classified'),
        @('Unconnected NavMesh', 'the unconnected-navmesh suffix warning must stay classified'),
        @('Potentially overlapping', 'the overlapping-navmesh template must stay classified'),
        @('invalid door portal', 'the invalid door portal template must stay classified'),
        @('altered but not checked out', 'the not-checked-out template must stay classified')
    )) {
        if ($verdictText -notmatch $required[0]) {
            $failures.Add("$VerdictSource no longer matches /$($required[0])/: $($required[1])")
        }
    }
}

# Regression: the reader lives in its own module, so a CMake file that lists only one of them builds a
# plugin whose nav.state or apply pipeline is missing entirely.
if ($buildText) {
    foreach ($source in 'AgentNavmesh.cpp', 'AgentNavmeshRead.cpp', 'AgentNavmeshCheck.cpp', 'AgentPlace.cpp') {
        if ($buildText -notmatch [regex]::Escape($source)) {
            $failures.Add("$Build does not compile $source, so the navmesh tools would answer unknown_tool or miss their reader")
        }
    }
}

if ($Live) {
    $live = Test-NavmeshLive -LiveCell $Cell -GenerateCell:$Generate -TimeoutSeconds $LiveTimeoutSeconds `
        -CheckVerdict:$Check -Expected $Expect -Dll (Resolve-McpDll $McpDll) -Game $GamePath -CheckTimeout $CheckTimeoutMs `
        -ApplyRun:$Apply -RetryRun:$ApplyRetry -ApplyKind $ApplyKind -StageBudget $StageBudgetMs `
        -ProbeBudget $ProbeBudgetMs -ApplyBudget $ApplyBudgetSeconds
    foreach ($key in $live.Evidence.Keys) { $observed["live.$key"] = $live.Evidence[$key] }
    foreach ($failure in $live.Failures) { $failures.Add($failure) }
} else {
    $observed['live'] = 'skipped'
}

$evidenceJson = [ordered]@{ navmesh = $observed; failures = @($failures); passed = ($failures.Count -eq 0) } | ConvertTo-Json -Depth 6
if ($Evidence) {
    $evidenceJson | Set-Content -LiteralPath $Evidence -Encoding utf8
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) { Write-Output "FAIL $failure" }
    exit 1
}

Write-Output 'PASS navmesh commands are menu-resolved, dispatched, built, and finalize needs confirmation'
Write-Output $evidenceJson
exit 0
