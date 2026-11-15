# Constraint: the live modes drive a running editor, so they live outside the static suite's line budget.
function Invoke-CkAgent([string]$Command, [int]$ConnectMs = 5000) {
    $stream = New-Object System.IO.Pipes.NamedPipeClientStream('.', $Pipe, [System.IO.Pipes.PipeDirection]::InOut)
    try { $stream.Connect($ConnectMs) } catch { return $null }

    $reader = New-Object System.IO.StreamReader($stream)
    $writer = New-Object System.IO.StreamWriter($stream)
    $writer.AutoFlush = $true
    $writer.NewLine = "`n"

    try { [void]$reader.ReadLine() } catch { $stream.Dispose(); return $null }
    $writer.WriteLine("CALL 1 $Command")

    try { $reply = $reader.ReadLine() } catch { $stream.Dispose(); return $null }
    $stream.Dispose()

    if ($reply -match '^RPLY \d+ OK (.*)$') { return $Matches[1] }
    return "ERR $reply"
}


function Read-NavState([string]$payload) {
    if (-not $payload -or $payload.StartsWith('ERR')) { return $null }
    try { return $payload | ConvertFrom-Json } catch { return $null }
}


function Get-Field($object, [string]$name) {
    if ($null -eq $object) { return $null }
    $property = $object.PSObject.Properties[$name]
    if ($property) { return $property.Value }
    return $null
}


# Constraint: the verdict only exists above the pipe, so the check is driven through the real MCP server.
function Invoke-McpTool([string]$ToolName, $Arguments, [string]$Dll, [string]$Game, [int]$TimeoutMs) {
    if (-not $Dll -or -not (Test-Path -LiteralPath $Dll)) { return $null }
    if (-not (Get-Command node -ErrorAction SilentlyContinue)) { return $null }

    $argsFile = [System.IO.Path]::GetTempFileName()
    try {
        ($Arguments | ConvertTo-Json -Depth 5 -Compress) | Set-Content -LiteralPath $argsFile -Encoding utf8
        $probe = Join-Path $PSScriptRoot 'mcp-call.mjs'
        $text = & node $probe $Dll $ToolName $argsFile $Game $TimeoutMs 2>$null
        if (-not $text) { return $null }
        return ($text | ConvertFrom-Json)
    }
    finally {
        Remove-Item -LiteralPath $argsFile -ErrorAction SilentlyContinue
    }
}


function Resolve-McpDll([string]$configured) {
    if ($configured) { return $configured }

    $root = Join-Path $PSScriptRoot '..\CKAgent.Mcp\bin\Release'
    if (-not (Test-Path -LiteralPath $root)) { return '' }

    $found = Get-ChildItem -Path $root -Recurse -Filter 'CKAgent.Mcp.dll' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $found) { return '' }
    return $found.FullName
}


# Regression: an emptied navmesh array head is not an unreadable one, so an 'empty' form must answer zeros
# and only an 'unreadable' form may answer null.
function Test-NavForms($state, $liveFailures, $evidence) {
    $forms = @($state.navmesh.forms)
    $evidence['forms'] = $forms.Count
    $evidence['navmeshForms'] = [int]$state.navmesh.navmeshForms

    $empty = 0
    $read = 0
    $unreadable = 0
    $vertices = 0
    $triangles = 0

    foreach ($form in $forms) {
        $geometry = [string]$form.geometry

        if (@('read', 'empty', 'unreadable') -notcontains $geometry) {
            $liveFailures.Add("form $($form.formId) reports geometry '$geometry', which is none of read, empty or unreadable")
            continue
        }

        if ($geometry -eq 'unreadable') {
            $unreadable++
            if ($null -ne $form.vertices) {
                $liveFailures.Add("form $($form.formId) is unreadable yet reports $($form.vertices) vertices")
            }
            continue
        }

        if ($null -eq $form.vertices -or $null -eq $form.triangles -or $null -eq $form.area -or $null -eq $form.areaTriangles) {
            $liveFailures.Add("form $($form.formId) reports geometry '$geometry' with null counts; an emptied array head must read as zero, not unknown")
            continue
        }

        if ($geometry -eq 'empty') {
            $empty++
            if ([int]$form.vertices -ne 0 -or [int]$form.triangles -ne 0 -or [double]$form.area -ne 0 -or [int]$form.areaTriangles -ne 0) {
                $liveFailures.Add("form $($form.formId) is empty yet reports $($form.vertices) vertices, $($form.triangles) triangles and $($form.area) area")
            }
            continue
        }

        $read++
        $vertices += [int]$form.vertices
        $triangles += [int]$form.triangles

        if ([int]$form.vertices -le 0 -or [int]$form.triangles -le 0) {
            $liveFailures.Add("form $($form.formId) is marked read yet reports $($form.vertices) vertices and $($form.triangles) triangles")
        }
    }

    $evidence['readForms'] = $read
    $evidence['emptyForms'] = $empty
    $evidence['unreadableForms'] = $unreadable

    # Regression: cover was reported only per cell, so a cell with several meshes could not be compared mesh by
    # mesh and a form's cover either missing or counted twice hid inside the cell total.
    $edge01 = 0
    $edge12 = 0
    $edge20 = 0
    $any = 0
    $all = 0
    $openEdges = 0
    $heightSet = 0
    $covered = 0

    foreach ($form in $forms) {
        $geometry = [string]$form.geometry

        if ($geometry -ne 'read') {
            if ($null -ne $form.cover) {
                $liveFailures.Add("form $($form.formId) reports cover while its geometry is '$geometry', so those counts belong to no triangles")
            }
            continue
        }

        if ($null -eq $form.cover) {
            $liveFailures.Add("form $($form.formId) is readable but carries no cover of its own, so this cell's meshes cannot be compared one by one")
            continue
        }

        $covered++
        $edge01 += [int]$form.cover.edge01
        $edge12 += [int]$form.cover.edge12
        $edge20 += [int]$form.cover.edge20
        $any += [int]$form.cover.any
        $all += [int]$form.cover.all
        $openEdges += [int]$form.cover.openEdges
        $heightSet += [int]$form.cover.heightSet
    }

    $evidence['coverForms'] = $covered

    $cellCover = $state.navmesh.coverage.edgeCover
    if ($null -ne $cellCover) {
        $sums = [ordered]@{
            edge01 = $edge01; edge12 = $edge12; edge20 = $edge20
            any = $any; all = $all; openEdges = $openEdges; heightSet = $heightSet
        }

        foreach ($key in $sums.Keys) {
            if ([int](Get-Field $cellCover $key) -ne $sums[$key]) {
                $liveFailures.Add("the cell reports $key = $([int](Get-Field $cellCover $key)) while its forms sum to $($sums[$key]), so a form's cover is missing or counted twice")
            }
        }
    }

    $coverage = $state.navmesh.coverage
    if ([int]$coverage.readForms -ne $read -or [int]$coverage.emptyForms -ne $empty -or [int]$coverage.unreadableForms -ne $unreadable) {
        $liveFailures.Add("coverage counts $($coverage.readForms) read / $($coverage.emptyForms) empty / $($coverage.unreadableForms) unreadable, but the forms say $read / $empty / $unreadable")
    }

    if ([int]$coverage.vertices -ne $vertices -or [int]$coverage.triangles -ne $triangles) {
        $liveFailures.Add("coverage sums $($coverage.vertices) vertices and $($coverage.triangles) triangles, but the readable forms hold $vertices and $triangles")
    }

    if ($null -eq $coverage.area -and $unreadable -eq 0) {
        $liveFailures.Add('coverage has no area even though every form is readable, so an empty form voided the total')
    }
}


# Regression: the check's verdict was once parsed from whatever the log happened to hold, so a real run could
# answer 'unknown' with findings on screen. This asserts the verdict names the findings the cell is known for.
function Test-NavmeshVerdict($result, [string[]]$Expected, $liveFailures, $evidence) {
    $code = [string](Get-Field $result 'code')
    $evidence['verdictCode'] = $code
    $evidence['verdictSummary'] = [string](Get-Field $result 'message')

    $verdict = Get-Field $result 'verdict'
    if ($null -eq $verdict) {
        $liveFailures.Add("the check answered '$code' without a verdict: $($evidence['verdictSummary'])")
        return
    }

    $verdictCode = [string](Get-Field $verdict 'code')
    $evidence['verdict'] = $verdictCode
    $evidence['lines'] = [int](Get-Field $verdict 'lines')

    if (@('clean', 'warnings', 'errors') -notcontains $verdictCode) {
        $liveFailures.Add("the check reported '$verdictCode' instead of clean, warnings or errors: $([string](Get-Field $verdict 'summary'))")
        return
    }

    # Regression: the plugin and the server both classify the check, so the same lines could read errors in one
    # and clean in the other without either side noticing.
    $pluginAnswer = Read-NavState ([string](Get-Field $result 'payload'))
    $pluginSaid = [string](Get-Field $pluginAnswer 'verdict')
    $evidence['pluginVerdict'] = $pluginSaid
    if ($pluginSaid -and $pluginSaid -ne $verdictCode) {
        $liveFailures.Add("the plugin's own check reported '$pluginSaid' while the server parsed '$verdictCode' from the same lines")
    }

    $findings = @()
    foreach ($cellReport in @(Get-Field $verdict 'cells')) {
        if ($null -eq $cellReport) { continue }
        foreach ($finding in @(Get-Field $cellReport 'findings')) {
            if ($null -eq $finding) { continue }
            $findings += $finding
        }
    }
    foreach ($finding in @(Get-Field $verdict 'unattributed')) {
        if ($null -eq $finding) { continue }
        $findings += $finding
    }

    $evidence['findings'] = $findings.Count
    $errors = @($findings | Where-Object { (Get-Field $_ 'severity') -eq 'error' }).Count
    $warnings = @($findings | Where-Object { (Get-Field $_ 'severity') -eq 'warning' }).Count

    if ($errors -ne [int](Get-Field $verdict 'errors') -or $warnings -ne [int](Get-Field $verdict 'warnings')) {
        $liveFailures.Add("the verdict totals $([int](Get-Field $verdict 'errors')) errors and $([int](Get-Field $verdict 'warnings')) warnings, but it names $errors and $warnings")
    }

    if ($verdictCode -ne 'clean' -and $findings.Count -eq 0) {
        $liveFailures.Add("the check reported '$verdictCode' but named no finding, so the verdict says nothing the caller can act on")
    }

    foreach ($finding in $findings) {
        if ([string]::IsNullOrWhiteSpace([string](Get-Field $finding 'message'))) {
            $liveFailures.Add('a finding was named without a message')
        }

        if ([string]::IsNullOrWhiteSpace([string](Get-Field $finding 'line'))) {
            $liveFailures.Add("the finding '$([string](Get-Field $finding 'message'))' carries no source line, so it cannot be tied to the report")
        }
    }

    foreach ($pattern in $Expected) {
        $hit = $findings | Where-Object {
            ([string](Get-Field $_ 'message') -match $pattern) -or ([string](Get-Field $_ 'line') -match $pattern)
        } | Select-Object -First 1

        if (-not $hit) {
            $named = ($findings | ForEach-Object { [string](Get-Field $_ 'message') }) -join '; '
            if (-not $named) { $named = 'nothing' }
            $liveFailures.Add("the verdict does not name '$pattern'; it named: $named")
        }
    }
}


# Regression: a run abandoned mid-stage used to stay open in the plugin, so the next nav.apply resumed the
# dead sequence and the editor kept the modal its failed stage left behind; recovery meant waiting out the
# stale window or restarting the editor.
function Test-NavmeshApplyAbort([string]$LiveCell, [string]$Kind, $liveFailures, $evidence) {
    $first = Read-NavState (Invoke-CkAgent "nav.apply $Kind confirm")
    if (-not $first) {
        $liveFailures.Add('the apply did not start, so a mid-sequence abort could not be tested: the cell must be writable and its plugin active')
        return
    }

    $firstStage = [string](Get-Field $first 'stage')
    $firstDone = [bool](Get-Field $first 'done')
    $evidence['abortFirstStage'] = $firstStage
    $evidence['abortFirstDone'] = $firstDone

    $abort = Read-NavState (Invoke-CkAgent 'nav.apply abort')
    if (-not $abort) {
        $liveFailures.Add('nav.apply abort answered nothing, so a failed run still needs a restart')
        return
    }

    $dropped = [bool](Get-Field $abort 'dropped')
    $evidence['abortDropped'] = $dropped
    $evidence['abortDismissedBy'] = [string](Get-Field $abort 'dismissedBy')

    if (-not $firstDone -and -not $dropped) {
        $liveFailures.Add("nav.apply abort reported nothing to drop although the run answered '$firstStage'")
    }

    # Constraint: the retry must start a new run, so it cannot answer with the abandoned stage.
    $second = Read-NavState (Invoke-CkAgent "nav.apply $Kind confirm")
    if (-not $second) {
        $liveFailures.Add('the apply could not be started again after the abort, so a failed run still needs a restart')
        return
    }

    $stage = [string](Get-Field $second 'stage')
    $evidence['abortRetryStage'] = $stage
    if ($stage -ne 'generate' -and $stage -ne 'done') {
        $liveFailures.Add("the retry answered '$stage' instead of starting at generate, so the abandoned run was resumed")
    }

    # Constraint: the test must not leave a run behind for the next mode to continue.
    [void](Invoke-CkAgent 'nav.apply abort')
}


function Test-NavmeshApplyStage([string]$LiveCell, [string]$Kind, [int]$StageMs, [int]$ProbeMs, [int]$BudgetSeconds, $liveFailures, $evidence) {
    $state = Read-NavState (Invoke-CkAgent 'nav.state')
    if (-not $state) {
        $liveFailures.Add('nav.state answered nothing before the apply started')
        return
    }

    $loaded = [string](Get-Field (Get-Field $state 'cell') 'editorId')
    $evidence['applyCell'] = $loaded

    if ($LiveCell -and $loaded -and $loaded -ne $LiveCell) {
        $liveFailures.Add("the editor is on '$loaded' rather than '$LiveCell', so the apply would rewrite the wrong cell")
        return
    }

    $pendingSeen = 0
    $probes = 0
    $fastProbes = 0
    $probeTimes = @()
    $probedStages = @{}
    $slowestStage = 0
    $slowestProbe = 0
    $stages = @()
    $finished = $false
    $failed = $false
    $deadline = (Get-Date).AddSeconds($BudgetSeconds)

    while ((Get-Date) -lt $deadline) {
        $watch = [System.Diagnostics.Stopwatch]::StartNew()
        $reply = Invoke-CkAgent "nav.apply $Kind confirm"
        $watch.Stop()
        $elapsed = [int]$watch.Elapsed.TotalMilliseconds

        if (-not $reply -or $reply.StartsWith('ERR')) {
            $liveFailures.Add("nav.apply $Kind confirm failed: $reply -- the cell must be writable and its plugin active")
            $failed = $true
            break
        }

        $answer = Read-NavState $reply
        if (-not $answer) {
            $liveFailures.Add("nav.apply answered unreadable JSON: $reply")
            $failed = $true
            break
        }

        $stage = [string](Get-Field $answer 'stage')
        if ($elapsed -gt $slowestStage) { $slowestStage = $elapsed }
        if ($elapsed -gt $StageMs) {
            $liveFailures.Add("the '$stage' stage answered after $elapsed ms against a $StageMs ms budget, so the editor thread was held while it ran")
        }

        $stages += [ordered]@{ stage = $stage; ms = $elapsed }

        if (Get-Field $answer 'done') {
            $evidence['applyVerdict'] = [string](Get-Field (Get-Field $answer 'report') 'verdict')
            $reported = [string](Get-Field $answer 'verdict')
            $evidence['applyPluginVerdict'] = $reported
            $evidence['applySteps'] = ($stages | ForEach-Object { "$($_.stage)=$($_.ms)ms" }) -join ' '

            if (@('clean', 'warnings', 'errors', 'unknown') -notcontains $reported) {
                $liveFailures.Add("the finished apply carries no verdict of its own ('$reported'), so the result still rests on the server parsing the lines")
            }

            $finished = $true
            break
        }

        if (Get-Field $answer 'pending') {
            $pendingSeen++

            # Constraint: probing every poll would double the work of a long settle, so each stage is probed once.
            if ($probedStages.ContainsKey($stage)) {
                Start-Sleep -Milliseconds 500
                continue
            }
            $probedStages[$stage] = $true

            # Constraint: a pending stage must leave the editor free, so probe it with an unrelated call.
            $probe = [System.Diagnostics.Stopwatch]::StartNew()
            $probeReply = Invoke-CkAgent 'nav.state'
            $probe.Stop()
            $probeMs = [int]$probe.Elapsed.TotalMilliseconds
            $probes++
            $probeTimes += "$stage=$probeMs"
            if ($probeMs -gt $slowestProbe) { $slowestProbe = $probeMs }
            # Constraint: the editor may legitimately be computing during a stage, so a slow probe is only
            # evidence if no pending stage ever answered one quickly.
            if ($probeReply -and $probeMs -le $ProbeMs) { $fastProbes++ }
        }

        Start-Sleep -Milliseconds 500
    }

    if (-not $evidence['applySteps']) {
        $evidence['applySteps'] = ($stages | ForEach-Object { "$($_.stage)=$($_.ms)ms" }) -join ' '
    }

    $evidence['applyPending'] = $pendingSeen
    $evidence['applyProbes'] = $probes
    $evidence['applyFastProbes'] = $fastProbes
    $evidence['applyProbeMs'] = $probeTimes -join ' '
    $evidence['applySlowestStageMs'] = $slowestStage
    $evidence['applySlowestProbeMs'] = $slowestProbe

    if (-not $finished -and -not $failed) {
        $liveFailures.Add("the apply did not reach done within $BudgetSeconds s; its stages were '$($stages | ForEach-Object { $_.stage })'")
        return
    }

    if ($failed) { return }

    if ($pendingSeen -eq 0) {
        $liveFailures.Add('the apply reached done in one call, so nothing showed the editor staying responsive while a stage was pending')
    }

    if ($probes -eq 0) {
        $liveFailures.Add('no stage was sampled while it was pending, so the responsiveness of the editor thread is unproven')
    }

    if ($fastProbes -eq 0) {
        $liveFailures.Add("no pending stage answered nav.state within $ProbeMs ms, so the editor served no other call while the apply was in flight; probes were '$($probeTimes -join ' ')'")
    }
}


# Regression: a live generate left the mesh it created in the cell, so the next run read forms it had not
# asked for and putting the cell back meant closing it without saving by hand.
function Restore-NavmeshCell([string]$LiveCell, [int]$OriginalForms, [int]$TimeoutSeconds, [bool]$Mutated, $liveFailures, $evidence) {
    $state = Read-NavState (Invoke-CkAgent 'nav.state')
    if (-not $state) {
        $liveFailures.Add('nav.state answered nothing after the run, so the cell could not be checked for left-over navmesh')
        return
    }

    $now = [int]$state.navmesh.navmeshForms
    $evidence['restoreForms'] = $now

    if (-not $Mutated) {
        $evidence['restore'] = 'not_needed'
        return
    }

    # Constraint: removing navmesh from a cell that had some would delete the original, so only an empty cell is restored.
    if ($OriginalForms -gt 0) {
        $evidence['restore'] = 'not_possible'
        return
    }

    if ($now -eq 0) {
        $evidence['restore'] = 'already_empty'
        return
    }

    # Constraint: the caption comes from the live menu, so this build's wording is not assumed.
    $caption = 'Remove Cell NavMeshes'
    $commands = Read-NavState (Invoke-CkAgent 'nav.commands')
    foreach ($command in @(Get-Field $commands 'commands')) {
        $text = [string](Get-Field $command 'caption')
        if ($text -match 'remove.*cell.*navmesh') { $caption = $text; break }
    }

    $posted = Invoke-CkAgent "nav.run $caption confirm"
    $evidence['restoreCaption'] = $caption
    $evidence['restorePost'] = $posted

    if (-not $posted -or $posted.StartsWith('ERR')) {
        $evidence['restore'] = 'failed'
        $liveFailures.Add("the generated mesh could not be removed with '$caption', so the cell keeps it: $posted")
        return
    }

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 1
        $state = Read-NavState (Invoke-CkAgent 'nav.state')
        if ($state -and [int]$state.navmesh.navmeshForms -eq 0) {
            $evidence['restore'] = 'removed'
            return
        }
    }

    $evidence['restore'] = 'failed'
    $liveFailures.Add("'$caption' left navmesh forms in $LiveCell after $TimeoutSeconds s, so the run left a generated mesh behind")
}

function Test-NavmeshLive {
    param([string]$LiveCell, [switch]$GenerateCell, [int]$TimeoutSeconds,
        [switch]$CheckVerdict, [string[]]$Expected, [string]$Dll, [string]$Game, [int]$CheckTimeout,
        [switch]$ApplyRun, [switch]$RetryRun, [string]$ApplyKind, [int]$StageBudget, [int]$ProbeBudget, [int]$ApplyBudget)

    $liveFailures = New-Object System.Collections.Generic.List[string]
    $evidence = [ordered]@{ cell = $LiveCell }

    $loaded = Invoke-CkAgent "ui.cellload $LiveCell"
    if (-not $loaded) {
        $liveFailures.Add("no editor is answering on the '$Pipe' pipe, so the cell could not be loaded")
        return [pscustomobject]@{ Failures = $liveFailures; Evidence = $evidence }
    }

    $evidence['load'] = $loaded
    Start-Sleep -Seconds 5

    $before = Read-NavState (Invoke-CkAgent 'nav.state')
    if (-not $before) {
        $liveFailures.Add('nav.state answered nothing readable after the cell was loaded')
        return [pscustomobject]@{ Failures = $liveFailures; Evidence = $evidence }
    }

    # Constraint: the mutating modes are what create navmesh, so only a read-only run needs one to exist first.
    $originalForms = [int]$before.navmesh.navmeshForms
    $mutates = [bool]($GenerateCell -or $ApplyRun -or $RetryRun)
    $evidence['originalForms'] = $originalForms

    if (-not $mutates -and $originalForms -eq 0) {
        $liveFailures.Add("$LiveCell carries no navmesh forms, so there is nothing to read; pass -Cell with a cell that has navmesh")
        return [pscustomobject]@{ Failures = $liveFailures; Evidence = $evidence }
    }

    $evidence['beforeForms'] = $originalForms
    if ($originalForms -gt 0) {
        Test-NavForms $before $liveFailures ([ordered]@{})
    }

    if ($RetryRun) {
        Test-NavmeshApplyAbort $LiveCell $ApplyKind $liveFailures $evidence
    }

    if ($ApplyRun) {
        Test-NavmeshApplyStage $LiveCell $ApplyKind $StageBudget $ProbeBudget $ApplyBudget $liveFailures $evidence
    }

    if ($GenerateCell) {
        $posted = Invoke-CkAgent 'nav.generate recast confirm'
        $evidence['generate'] = $posted

        if (-not $posted -or $posted.StartsWith('ERR')) {
            $liveFailures.Add("nav.generate failed: $posted")
            return [pscustomobject]@{ Failures = $liveFailures; Evidence = $evidence }
        }

        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            Start-Sleep -Seconds 2
            $accept = Invoke-CkAgent 'nav.accept'
            if ($accept -and $accept -notmatch 'no_dialog') { $evidence['accept'] = $accept; break }
        }

        $after = $null
        $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
        while ((Get-Date) -lt $deadline) {
            Start-Sleep -Seconds 3
            $state = Read-NavState (Invoke-CkAgent 'nav.state')
            if ($state -and [int]$state.navmesh.navmeshForms -gt [int]$before.navmesh.navmeshForms) { $after = $state; break }
        }

        if (-not $after) {
            $liveFailures.Add("generation added no navmesh form within $TimeoutSeconds s, so no generated cell was read; the cell must be writable, which needs an active plugin")
            Restore-NavmeshCell $LiveCell $originalForms $TimeoutSeconds $mutates $liveFailures $evidence
            return [pscustomobject]@{ Failures = $liveFailures; Evidence = $evidence }
        }

        $before = $after
    }

    if ($originalForms -gt 0 -or -not $mutates) {
        Test-NavForms $before $liveFailures $evidence
    }

    if ($CheckVerdict) {
        if (-not $Game) {
            $liveFailures.Add('the verdict needs -GamePath (or CKAGENT_GAME) so the MCP server can find the editor data')
        }
        elseif (-not $Dll) {
            $liveFailures.Add('the MCP server was not found under CKAgent.Mcp\bin\Release; build it or pass -McpDll')
        }
        else {
            $evidence['mcp'] = $Dll
            $result = Invoke-McpTool 'ck.navmesh' @{ mode = 'check' } $Dll $Game $CheckTimeout

            if (-not $result) {
                $liveFailures.Add('the MCP server answered nothing for ck.navmesh mode=check')
            }
            else {
                Test-NavmeshVerdict $result $Expected $liveFailures $evidence
            }
        }
    }

    Restore-NavmeshCell $LiveCell $originalForms $TimeoutSeconds $mutates $liveFailures $evidence

    return [pscustomobject]@{ Failures = $liveFailures; Evidence = $evidence }
}
