using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Garden.CkAgent;

public sealed record NavmeshResult(
    string Status,
    string Code,
    string Message,
    string? Payload,
    string? Log,
    IReadOnlyList<string>? Report)
{
    public NavmeshVerdict? Verdict { get; init; }

    public NavmeshPersistence? Persistence { get; init; }

    public NavmeshCheckEvidence? Check { get; init; }
}

// Constraint: the pass leaves its verdict in the CKPE log, so that is what is read back.
[McpServerToolType]
public sealed partial class NavmeshTools
{
    private const int LogSettleMs = 1200;
    private const int MaxLineLength = 500;

    private readonly IPluginCaller _plugin;
    private readonly EditorPaths _paths;
    private readonly SaveCoordinator _save;

    public NavmeshTools(IPluginCaller plugin, EditorPaths paths, SaveCoordinator save)
    {
        _plugin = plugin;
        _paths = paths;
        _save = save;
    }

    [McpServerTool(Name = "ck.navmesh"),
     Description("Create, apply and inspect the loaded cell's navmesh through the editor's own commands. " +
                 "mode=state reads the cell's navmesh forms: whether the cell carries navmesh, how many " +
                 "forms, each form's id, its navmesh id, the cell it belongs to and its vertex and triangle " +
                 "counts, plus a coverage block that sums those counts (geometry form_arrays) with the " +
                 "measured triangle area in square units (area, null when a form cannot be read in full). " +
                 "The counts come " +
                 "from the form's own vertex and triangle arrays and were confirmed against the cell's NAVM " +
                 "records. mode=apply is the whole job behind one tool call: it drives the plugin's staged " +
                 "pipeline, where each stage is its own non-blocking call and answers pending until it is " +
                 "done, and posts generation (kind=recast default, havok or advanced), presses the dialog " +
                 "that command opens and waits for it to close, then Finalize Cell NavMeshes, then " +
                 "Check NavMeshes, in that order, and returns each step; it fails with no_geometry_change " +
                 "when the cell's own vertex and triangle counts did not move, so a no-op never reads as " +
                 "a successful run. The check's own log lines are " +
                 "parsed into a structured per-cell verdict instead of being returned raw, and it saves " +
                 "the cell with the editor's own File/Save command only when that verdict is clean and no " +
                 "dialog was left behind. dry_run=true resolves the four commands and posts nothing, so " +
                 "neither a generation nor a save can happen. " +
                 "mode=generate runs only the generation step: it posts the generator's caption, waits for the " +
                 "dialog the editor opens afterwards, presses its OK, and then waits for the count to settle, " +
                 "because a build empties the cell's arrays before it writes them and a single reading proves " +
                 "nothing; wait_seconds bounds that wait (default 240, limit 900) and the answer reports the " +
                 "counts before and after with the time it took. mode=finalize runs only finalize; mode=check " +
                 "runs only the check and answers with that same per-cell verdict: each cell with its " +
                 "errors and warnings, each finding with a severity, the navmesh id its message names and " +
                 "a collapsed count; a check that writes nothing recognised is unknown, never clean. " +
                 "mode=abort drops a staged apply that stalled or failed and dismisses any generation " +
                 "dialog it left open, so mode=apply can be retried without restarting the editor. " +
                 "mode=run executes one named navmesh command from the live menu, " +
                 "resolved by caption, which is the command-level edit surface: precuts, cover clearing, " +
                 "mode=load, which takes cell=<EditorID> plus worldspace=<name> for an exterior cell, row=<Cell View index> " +
                 "or form_id=<FormID> when several cells share that EditorID, and proves the load " +
                 "before reading it, and mode=strip, which zeroes the loaded cell's navmesh arrays through the plugin so a build " +
                 "waypoints, Move Selection To Separate NavMesh, Remove Cell NavMeshes, Audit Navmesh " +
                 "Report, Export Navmesh as OBJ. mode=commands lists every navmesh entry with its path, id " +
                 "and enabled state, so nothing is addressed by a build-specific id. " +
                 "mode=audit sweeps the Cell View without changing any cell: it loads each cell it walks " +
                 "(filter narrows the list, after resumes it at the last cell of the previous answer, limit " +
                 "defaults to 5) and answers per cell whether the cell carries navmesh, its form count, " +
                 "vertices, triangles, cover edges and whether it needs work, which is a cell with no navmesh " +
                 "or with a form that was emptied or could not be read; the last cell walked is left loaded. " +
                 "mode=batch runs the whole pipeline on each listed cell, through the same stages as " +
                 "mode=apply, and saves only the cells whose check came back clean; it answers per cell and " +
                 "walks one cell per call by default, so a long sweep advances with after instead of holding " +
                 "one call open, and dry_run lists the cells it would run. mode=batch kind=mesh strips, regenerates and " +
                 "then saves plugin=<file> for every cell it meshed - the file is copied to Data back-ups first - so no " +
                 "mesh is left in memory: each cell answers what was written, to which file and when, and a cell whose " +
                 "save failed is reported as unfinished rather than delivered. mode=cover posts one command of " +
                 "the cover group - generate, clear, clear_generated, or generate_all for the whole load order - " +
                 "waits a bounded wait_seconds for the cover counts, answers posted when the editor is still " +
                 "computing, and cover=status reads those counts afterwards, and reports the cover counts before " +
                 "and after, because that group answers nothing itself. apply, generate, " +
                 "finalize, run, batch and cover all change cell data and are refused without confirm=true. " +
                 "Painting or " +
                 "moving individual triangles is not possible here: the CK's navmesh edit module is " +
                 "interactive and the headers expose no geometry. No NAVM payload is written by this tool. " +
                 "Requires CKPE plus CKAgent.dll in <game>\\CKPEPlugins\\.")]
    public NavmeshResult Navmesh(string mode = "commands", bool confirm = false, int max_rows = 40, string cell = "",
                                 string worldspace = "",
                                 string kind = "recast", string command = "", bool dry_run = false,
                                 string filter = "", string after = "", int limit = 0, string cover = "",
                                 int wait_seconds = 0, int row = -1, string form_id = "",
                                 bool save = true, string plugin = "", string save_as = "", int save_timeout_seconds = 180)
    {
        _worldspace = (worldspace ?? "").Trim();

        switch ((mode ?? "").Trim().ToLowerInvariant())
        {
            // Constraint: exterior cells live behind the Cell View's worldspace combo, so a load names it first.
            case "load":
            {
                string wantedCell = (cell ?? "").Trim();
                if (wantedCell.Length == 0)
                    return Refused("mode=load needs cell=<EditorID>; interior names need no worldspace, exterior cells " +
                                   "also take worldspace=<name>");

                // Constraint: exterior cells share EditorIDs, so a caller may name the Cell View row or form id.
                if (!LoadCell(new CellRow(wantedCell, (form_id ?? "").Trim(), "", row),
                              out string loadCode, out string loadMessage, worldspace))
                    return new NavmeshResult("error", loadCode, loadMessage, null, _paths.CkpeLogPath, null);

                if (!BlockedEditor.TryCall(_plugin, "nav.state", out string loaded, out string stateCode,
                                           out string stateMessage))
                    return new NavmeshResult("error", stateCode, stateMessage, null, _paths.CkpeLogPath, null);

                return new NavmeshResult("ok", "loaded",
                    $"{wantedCell} is loaded; mode=generate acts on it and mode=state reads it back",
                    loaded, _paths.CkpeLogPath, null);
            }

            case "state":
            {
                if (!BlockedEditor.TryCall(_plugin, "nav.state", out string payload, out string code,
                                           out string message))
                    return new NavmeshResult("error", code, message, null, null, null);

                return new NavmeshResult("ok", "ok", "nav.state", payload, _paths.CkpeLogPath, null);
            }

            // Constraint: no editor command empties a cell outside the edit session, so stripping is a plugin verb.
            case "strip":
            {
                if (!confirm)
                    return Refused("mode=strip zeroes the loaded cell's navmesh arrays; repeat with confirm=true " +
                                   "when that is intended, which is the setup for testing a build from nothing");

                if (!BlockedEditor.TryCall(_plugin, "nav.strip confirm", out string payload, out string code,
                                           out string message))
                    return new NavmeshResult("error", code, message, null, _paths.CkpeLogPath, null);

                return new NavmeshResult("ok", "stripped",
                    "the loaded cell's navmesh arrays were zeroed; read the counts back with mode=state",
                    payload, _paths.CkpeLogPath, null);
            }

            case "commands":
            {
                if (!BlockedEditor.TryCall(_plugin, "nav.commands", out string payload, out string code,
                                           out string message))
                    return new NavmeshResult("error", code, message, null, null, null);

                return new NavmeshResult("ok", "ok", "nav.commands", payload, _paths.CkpeLogPath, null);
            }

            case "check":
                return Check();

            case "finalize":
                if (!confirm)
                    return Refused("mode=finalize changes the loaded cell's navmesh data; repeat with " +
                                   "confirm=true when that is intended");

                return Post("nav.finalize confirm", max_rows);

            case "generate":
            {
                if (!confirm)
                    return Refused("mode=generate replaces the loaded cell's generated navmesh; repeat with " +
                                   "confirm=true when that is intended");

                if (!ValidKind(kind))
                    return InvalidKind();

                return Generate(kind.Trim().ToLowerInvariant(), wait_seconds);
            }

            case "apply":
            {
                if (!ValidKind(kind))
                    return InvalidKind();

                string selected = kind.Trim().ToLowerInvariant();

                if (dry_run)
                {
                    if (!BlockedEditor.TryCall(_plugin, $"nav.plan {selected}", out string plan,
                                               out string planCode, out string planMessage))
                        return new NavmeshResult("error", planCode, planMessage, null, null, null);

                    return new NavmeshResult("ok", "dry_run",
                        "dry run: the planned steps would be posted in order and File/Save would run only if " +
                        "the check reported no failure; nothing was posted and the cell was not saved",
                        plan, _paths.CkpeLogPath, null);
                }

                if (!confirm)
                    return Refused("mode=apply regenerates, finalises, checks and then saves the loaded cell; " +
                                   "repeat with confirm=true when that is intended, or dry_run=true to see the plan");

                // Constraint: a cell can be named here, so one cell is one call rather than a load and then an apply.
                string wantedCell = (cell ?? "").Trim();
                if (wantedCell.Length > 0 &&
                    !LoadCell(new CellRow(wantedCell, (form_id ?? "").Trim(), "", row), out string loadCode,
                              out string loadMessage, worldspace))
                    return new NavmeshResult("error", loadCode, loadMessage, null, _paths.CkpeLogPath, null);

                int rows = max_rows <= 0 ? 40 : Math.Min(max_rows, 500);
                long offset = LogLength();

                // Constraint: the plugin may not sleep on the editor thread, so a pending stage is advanced here.
                const int TickPauseMs = 2000;
                const int TickLimit = 120;
                string payload = "";
                string code = "";
                string message = "";
                bool pruned = false;
                string pruneJson = "null";

                for (int tick = 0; tick < TickLimit; tick++)
                {
                    if (!BlockedEditor.TryCall(_plugin, $"nav.apply {selected} confirm", out payload,
                                               out code, out message))
                    {
                        // Constraint: a run that failed mid-sequence must not stay open, or its dialog blocks the editor.
                        bool dropped = AbortApply();
                        return new NavmeshResult("error", code,
                            message + (dropped ? " (the run was dropped, so mode=apply can be retried)" : ""),
                            payload.Length == 0 ? null : payload, _paths.CkpeLogPath, ReadAfter(offset, rows));
                    }

                    if (!Pending(payload))
                        break;

                    // Constraint: the generation emptied the form it replaced, so that placeholder is dropped before the
                    // check and the read-back look at the cell - otherwise every apply leaves one more form behind.
                    if (!pruned && Stage(payload) == "finalize")
                    {
                        pruned = true;
                        pruneJson = PruneSupersededForms();
                    }

                    if (tick == TickLimit - 1)
                    {
                        // Constraint: a stalled run must be droppable here, or the next apply waits out the stale window.
                        bool dropped = AbortApply();
                        return new NavmeshResult("error", "apply_incomplete",
                            $"the apply sequence stopped at stage {Stage(payload)} and never finished" +
                            (dropped
                                ? "; the run was dropped and mode=apply starts again from the generation"
                                : "; the run could not be dropped, so call mode=abort before retrying"),
                            payload, _paths.CkpeLogPath, ReadAfter(offset, rows));
                    }

                    Thread.Sleep(TickPauseMs);
                }

                // Constraint: the plugin reports its own verdict, so apply does not rest on a log re-parse alone.
                string? reported = PluginVerdict(payload);
                IReadOnlyList<string> reportLines = ReportLines(payload) ?? ReadAll(offset);
                NavmeshVerdict verdict = NavmeshVerdictParser.Parse(reportLines);
                string? blockedBy = BlockedEditor.BlockingTitle(_plugin);
                bool parsedClean = verdict.Code == "clean";

                // Constraint: a check that found nothing writes nothing, so a window holding only this tool's own pipe
                // echo is resolved against the plugin's verdict and the geometry digests the state call reports.
                CellNumbers? numbers = ReadLoadedNumbers();
                NavmeshExpected? checkGeometry = Geometry(numbers);
                NavmeshCheckEvidence evidence = NavmeshCheckGate.Resolve(reported, verdict, reportLines, parsedClean,
                    checkGeometry, blockedBy);
                bool mismatch = evidence.Code == "mismatch";
                bool clean = evidence.Clean;

                string saveOutcomeJson = "null";
                string persistenceJson = "null";
                NavmeshPersistence? persistence = null;
                string outcome;
                string resultCode;

                if (mismatch)
                {
                    outcome = $"{evidence.Reason}; the cell was not saved";
                    resultCode = "verdict_mismatch";
                }
                else if (!clean)
                {
                    outcome = $"{evidence.Reason}; the cell was not saved";
                    resultCode = "not_clean";
                }
                else
                {
                    (persistence, saveOutcomeJson) = SaveAndVerify("nav.save confirm", save_timeout_seconds);
                    persistenceJson = JsonSerializer.Serialize(persistence, EnvelopeJson);

                    if (persistence.Persisted)
                    {
                        outcome = $"{evidence.Reason}, so the cell was saved and read back: {persistence.Message}";
                        resultCode = "saved";
                    }
                    else
                    {
                        outcome = $"{evidence.Reason}, but the cell is not persisted ({persistence.Code}): " +
                                  $"{persistence.Message}";
                        resultCode = persistence.Code;
                    }
                }

                // Constraint: a prune the plugin could not perform is named here, so a stale install never reads as a
                // clean run with one extra form behind it.
                string pruneNotice = NavmeshApplyPrune.Notice(pruneJson);
                if (pruneNotice.Length > 0) outcome += $"; {pruneNotice}";

                // Constraint: the record is written before the answer, so a delivered cell always has its evidence on disk.
                string? evidencePath = WriteEvidence("apply", numbers, evidence, persistence, pruneJson, outcome);

                string envelope = "{\"pipeline\":" + payload + ",\"verdict\":" +
                                  JsonSerializer.Serialize(verdict, EnvelopeJson) +
                                  ",\"check\":" + JsonSerializer.Serialize(new
                                  {
                                      code = evidence.Code,
                                      clean = evidence.Clean,
                                      silent = evidence.Silent,
                                      corroborated = evidence.Corroborated,
                                      reason = evidence.Reason,
                                      geometryRecords = checkGeometry?.Records.Count ?? 0
                                  }) +
                                  ",\"pruned\":" + pruneJson +
                                  ",\"evidence\":" + (evidencePath is null ? "null" : JsonSerializer.Serialize(evidencePath)) +
                                  ",\"pluginVerdict\":" + (reported is null ? "null" : JsonSerializer.Serialize(reported)) +
                                  ",\"save\":" + saveOutcomeJson +
                                  ",\"persistence\":" + persistenceJson +
                                  ",\"blockedBy\":" + (string.IsNullOrEmpty(blockedBy) ? "null" : JsonSerializer.Serialize(blockedBy)) + "}";
                return new NavmeshResult("ok", resultCode,
                    evidencePath is null ? outcome : outcome + $"; evidence: {evidencePath}",
                    envelope, _paths.CkpeLogPath, null)
                {
                    Verdict = verdict,
                    Persistence = persistence,
                    Check = evidence
                };
            }

            case "abort":
            {
                // Constraint: recovery must never need confirm, because the run it drops is already failing.
                return Post("nav.apply abort", max_rows);
            }

            case "run":
            {
                if (!confirm)
                    return Refused("mode=run executes a navmesh command that may change cell data; repeat " +
                                   "with confirm=true when that is intended");

                string caption = (command ?? "").Trim();
                if (caption.Length == 0)
                    return new NavmeshResult("error", "invalid_argument",
                        "mode=run needs command set to a navmesh caption, for example 'Audit Navmesh Report'; " +
                        "mode=commands lists the captions this build offers", null, null, null);

                return Post($"nav.run {caption} confirm", max_rows);
            }

            case "audit":
                return Audit(filter, after, limit);

            case "batch":
            {
                string batchKind = (kind ?? "").Trim().ToLowerInvariant();

                // Constraint: the mesh batch saves what it generates, so it is reached before the apply kinds.
                if (batchKind == "mesh")
                {
                    if (!confirm && !dry_run)
                        return Refused("mode=batch kind=mesh strips and regenerates each cell's navmesh; repeat " +
                                       "with confirm=true when that is intended, or dry_run=true to list the cells");

                    return MeshBatch(filter, after, limit, wait_seconds, dry_run, save, plugin, save_as, save_timeout_seconds);
                }

                if (!ValidKind(kind))
                    return InvalidKind();

                if (dry_run)
                    return Batch(batchKind, filter, after, limit, max_rows, true);

                if (!confirm)
                    return Refused("mode=batch regenerates, finalises, checks and saves each listed cell; " +
                                   "repeat with confirm=true when that is intended, or dry_run=true to see the cells");

                return Batch(batchKind, filter, after, limit, max_rows, false);
            }

            case "cover":
            {
                string action = (cover ?? "").Trim().ToLowerInvariant();

                // Constraint: reading the counts is not a change, so it needs no confirm token.
                if (action == "status")
                    return CoverStatus(max_rows, save, save_timeout_seconds);

                if (!confirm)
                    return Refused("mode=cover changes the loaded cell's cover data; repeat with confirm=true " +
                                   "when that is intended");

                return Cover(action, max_rows, wait_seconds, save, save_timeout_seconds);
            }

            default:
                return new NavmeshResult("error", "invalid_argument",
                    "mode must be state, commands, check, finalize, generate, apply, abort, run, audit, " +
                    "batch or cover",
                    null, null, null);
        }
    }

    private const int DefaultAuditCells = 5;
    private const int DefaultBatchCells = 1;
    private const int MaxSweepCells = 20;

    // Constraint: a sweep loads each cell, so the list is resumed by name instead of a cursor held in the server.
    private NavmeshResult Audit(string filter, string after, int limit)
    {
        // Constraint: the Cell View lists one worldspace at a time, so the selection must happen before the list read.
        if (!UseWorldSpace(_worldspace, out string spaceCode, out string spaceMessage))
            return new NavmeshResult("error", spaceCode, spaceMessage, null, null, null);

        List<CellRow>? rows = SelectCells(filter, after, limit <= 0 ? DefaultAuditCells : limit,
            out string code, out string message);
        if (rows is null)
            return new NavmeshResult("error", code, message, null, null, null);

        if (rows.Count == 0)
            return new NavmeshResult("error", "no_cells", NoCellsMessage(filter, after), null, null, null);

        var cells = new List<object>();
        int needWork = 0;
        string last = "";

        foreach (CellRow row in rows)
        {
            cells.Add(AuditCell(row, out bool needsWork));
            if (needsWork) needWork++;
            last = row.CleanId;
        }

        string envelope = JsonSerializer.Serialize(new
        {
            filter = Trimmed(filter),
            after = Trimmed(after),
            nextAfter = last,
            audited = cells.Count,
            needsWork = needWork,
            cells,
        }, EnvelopeJson);

        return new NavmeshResult("ok", "ok",
            $"{cells.Count} cell(s) audited, {needWork} need navmesh work; {last} is left loaded",
            envelope, _paths.CkpeLogPath, null);
    }

    // Constraint: the batch is the same pipeline as mode=apply, so it calls it rather than restating the stages.
    private NavmeshResult Batch(string kind, string filter, string after, int limit, int maxRows, bool dryRun)
    {
        List<CellRow>? rows = SelectCells(filter, after, limit <= 0 ? DefaultBatchCells : limit,
            out string code, out string message);
        if (rows is null)
            return new NavmeshResult("error", code, message, null, null, null);

        if (rows.Count == 0)
            return new NavmeshResult("error", "no_cells", NoCellsMessage(filter, after), null, null, null);

        var names = new List<string>();
        foreach (CellRow row in rows) names.Add(row.EditorId);
        string last = names[names.Count - 1];

        if (dryRun)
        {
            string plan = JsonSerializer.Serialize(
                new { kind, after = Trimmed(after), cells = names, nextAfter = last }, EnvelopeJson);

            return new NavmeshResult("ok", "dry_run",
                $"a batch of {names.Count} cell(s) would run {kind} on each and save only the cells whose check " +
                "came back clean; nothing was posted and nothing was saved",
                plan, _paths.CkpeLogPath, null);
        }

        var outcomes = new List<object>();
        int saved = 0;
        string done = "";
        string? firstFailure = null;

        foreach (CellRow row in rows)
        {
            if (!LoadCell(row, out string loadCode, out string loadMessage))
            {
                firstFailure ??= $"{row.EditorId}: {loadMessage}";
                outcomes.Add(new
                {
                    cell = row.EditorId, code = loadCode, saved = false, verdict = (string?)null,
                    message = loadMessage,
                });
                done = row.EditorId;
                continue;
            }

            NavmeshResult applied = Navmesh("apply", true, maxRows, kind);
            bool didSave = string.Equals(applied.Code, "saved", StringComparison.Ordinal);
            if (didSave) saved++;
            else firstFailure ??= $"{row.EditorId}: {applied.Message}";

            outcomes.Add(new
            {
                cell = row.EditorId,
                code = applied.Code,
                saved = didSave,
                verdict = applied.Verdict?.Code,
                message = applied.Message,
            });
            done = row.EditorId;
        }

        bool allSaved = saved == outcomes.Count;
        string envelope = JsonSerializer.Serialize(new
        {
            kind,
            after = Trimmed(after),
            nextAfter = done.Length > 0 ? done : null,
            batched = outcomes.Count,
            saved,
            cells = outcomes,
        }, EnvelopeJson);

        return new NavmeshResult("ok", allSaved ? "saved" : "incomplete",
            allSaved
                ? $"{outcomes.Count} cell(s) ran {kind} and were saved"
                : $"{outcomes.Count} cell(s) ran {kind}, {saved} were saved; first that did not finish: {firstFailure}",
            envelope, _paths.CkpeLogPath, null);
    }


    private object AuditCell(CellRow row, out bool needsWork)
    {
        needsWork = false;

        if (!LoadCell(row, out string loadCode, out string loadMessage))
            return new
            {
                cell = row.CleanId, formId = row.FormId, loaded = false, error = loadCode,
                message = loadMessage,
            };

        if (!BlockedEditor.TryCall(_plugin, "nav.state", out string state, out string stateCode,
                                   out string stateMessage))
            return new
            {
                cell = row.CleanId, formId = row.FormId, loaded = true, error = stateCode,
                message = stateMessage,
            };

        try
        {
            using JsonDocument document = JsonDocument.Parse(state);
            JsonElement root = document.RootElement;
            JsonElement cell = Member(root, "cell");
            JsonElement navmesh = Member(root, "navmesh");
            JsonElement coverage = Member(navmesh, "coverage");

            string loadedId = StringMember(cell, "editorId");
            string loadedFormId = StringMember(cell, "formId");
            bool present = Member(navmesh, "present").ValueKind == JsonValueKind.True;
            int forms = Number(navmesh, "navmeshForms");
            int emptyForms = Number(coverage, "emptyForms");
            int unreadableForms = Number(coverage, "unreadableForms");
            int triangles = Number(coverage, "triangles");

            // Constraint: a build leaves an empty placeholder form behind, so only a cell with no triangles at all needs work.
            needsWork = !present || triangles == 0 || unreadableForms > 0;

            return new
            {
                cell = loadedId.Length > 0 ? loadedId : row.CleanId,
                formId = loadedFormId.Length > 0 ? loadedFormId : row.FormId,
                loaded = true,
                interior = Member(cell, "interior").ValueKind == JsonValueKind.True,
                references = Number(cell, "references"),
                navmesh = present,
                forms,
                vertices = Number(coverage, "vertices"),
                triangles,
                coverAny = Number(Member(coverage, "edgeCover"), "any"),
                emptyForms,
                unreadableForms,
                needsWork,
                readForms = Number(coverage, "readForms"),
            };
        }
        catch (JsonException)
        {
            return new
            {
                cell = row.EditorId, formId = row.FormId, loaded = true,
                error = "bad_reply", message = "nav.state answered without the JSON this tool reads",
            };
        }
    }

    // Constraint: a silent check may only pass when the geometry the save would compare is readable, so the same
    // per-record digests the read-back uses are read from the editor before the gate decides.
    // Constraint: the cell's own table is read once, so the check evidence, the prune and the evidence file agree.
    private CellNumbers? ReadLoadedNumbers()
    {
        if (!BlockedEditor.TryCall(_plugin, "nav.state", out string state, out _, out _)) return null;

        try
        {
            return ReadNumbers(state);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static NavmeshExpected? Geometry(CellNumbers? numbers)
    {
        if (numbers is null || numbers.Records.Count == 0) return null;

        uint cellId = NavmeshPersist.TryParseFormId(numbers.FormId, out uint parsed) ? parsed : 0;
        return new NavmeshExpected(cellId, numbers.Records);
    }

    private NavmeshExpected? ReadLoadedGeometry() => Geometry(ReadLoadedNumbers());

    // Constraint: a run that answers the user must leave a record of what it did, so the proof is not the chat itself.
    private string? WriteEvidence(string mode, CellNumbers? numbers, NavmeshCheckEvidence? check,
                                  NavmeshPersistence? persistence, string? prunedJson, string message)
    {
        uint cellId = numbers is not null && NavmeshPersist.TryParseFormId(numbers.FormId, out uint parsed)
            ? parsed
            : 0;

        return WriteEvidence(mode, numbers?.EditorId ?? "", cellId, check, persistence, prunedJson, message);
    }

    // Constraint: every path that writes a cell leaves the same record, so a sweep and a single apply are provable alike.
    private string? WriteEvidence(string mode, string cellName, uint cellFormId, NavmeshCheckEvidence? check,
                                  NavmeshPersistence? persistence, string? prunedJson, string message)
    {
        try
        {
            return NavmeshEvidence.Write(_paths, mode, cellName, cellFormId, check, persistence, prunedJson, message);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    // Constraint: every mode that computes navmesh or cover geometry persists through this one step, so no mode
    // can report a save it never made and no mode can leave the file for the user to write by hand.
    private (NavmeshPersistence Persistence, string SaveJson) SaveAndVerify(string saveCommand, int timeoutSeconds)
    {
        // Constraint: the editor writes whichever file it has open, so Data is snapshotted first and the file
        // that changes is the one read back.
        IReadOnlyDictionary<string, (long Length, DateTime Modified)> before = NavmeshPersist.Snapshot(_paths.DataDirectory);

        if (!BlockedEditor.TryCall(_plugin, saveCommand, out string posted, out string code, out string message))
        {
            return (NavmeshPersistence.Failed("save_failed",
                $"the editor's File/Save could not be posted: {message}"), "null");
        }

        string? written = NavmeshPersist.WaitForChange(before, _paths.DataDirectory,
            Math.Clamp(timeoutSeconds, 5, 120) * 1000);

        NavmeshPersistence persistence = written is null
            ? NavmeshPersistence.Failed("save_not_written",
                "File/Save was posted, but no plugin in Data changed, so nothing on disk holds this mesh")
            : VerifyLoadedCell(written);

        return (persistence, posted);
    }

    // Constraint: the cover commands report nothing themselves, so their effect is read from the counts.
    // Constraint: a cover pass computes on the editor thread, so its counts are read on a later call rather than inline.
    private NavmeshResult CoverStatus(int maxRows, bool save, int timeoutSeconds)
    {
        string? counts = CoverState(out string error);
        if (counts is null)
            return new NavmeshResult("error", "state_unreadable",
                "the loaded cell's cover could not be read: " + error, null, null, null);

        string summary = "triangles with cover: " + CoverAnyText(counts);
        if (!save)
            return new NavmeshResult("ok", "status", summary + "; save=false, so nothing was written",
                "{\"cover\":" + counts + "}", _paths.CkpeLogPath, null);

        // Constraint: this call is how a run that outlasted its budget is finished, so it persists the pass it reads.
        (NavmeshPersistence persistence, string saveJson) = SaveAndVerify("nav.save confirm", timeoutSeconds);

        // Constraint: a cover pass that reaches disk leaves the same record an apply does, or its proof is the answer.
        string? evidencePath = WriteEvidence("cover", ReadLoadedNumbers(), null, persistence, null, summary);
        string trail = evidencePath is null ? "" : $"; evidence: {evidencePath}";

        string envelope = "{\"cover\":" + counts + ",\"save\":" + saveJson +
                          ",\"persistence\":" + JsonSerializer.Serialize(persistence, EnvelopeJson) +
                          ",\"evidence\":" + (evidencePath is null ? "null" : JsonSerializer.Serialize(evidencePath)) + "}";

        if (!persistence.Persisted)
            return new NavmeshResult("error", persistence.Code,
                summary + $", but the cell is not persisted ({persistence.Code}): {persistence.Message}" + trail,
                envelope, _paths.CkpeLogPath, null);

        return new NavmeshResult("ok", "saved", summary + ", saved and read back: " + persistence.Message + trail,
            envelope, _paths.CkpeLogPath, null);
    }

    private NavmeshResult Cover(string action, int maxRows, int waitSeconds, bool save, int timeoutSeconds)
    {
        string caption = action switch
        {
            // Constraint: the cell command is the default, because the All Spaces one runs over the whole load order.
            "generate" => "Find Cover Edges",
            "generate_all" => "Generate Cover For All Spaces",
            "clear" => "Clear Cover",
            "clear_generated" => "Clear Generated Cover",
            _ => "",
        };

        if (caption.Length == 0)
            return new NavmeshResult("error", "invalid_argument",
                "cover must be generate, generate_all, clear, clear_generated or status; mode=commands lists " +
                "the captions this build offers",
                null, null, null);

        string? before = CoverState(out string coverError);
        if (before is null)
            return new NavmeshResult("error", "state_unreadable",
                "the loaded cell's cover could not be read before the command: " + coverError, null, null, null);

        long offset = LogLength();
        if (!BlockedEditor.TryCall(_plugin, $"nav.run {caption} confirm", out string posted, out string code,
                                   out string message))
            return new NavmeshResult("error", code, message, null, _paths.CkpeLogPath, null);

        // Constraint: the post returns at once and the editor then computes, so the counts are waited for, never assumed.
        int budget = waitSeconds <= 0 ? 20 : Math.Min(waitSeconds, 60);
        int elapsed = 0;
        string? after = null;
        string lastError = "";

        while (elapsed < budget * 1000)
        {
            Thread.Sleep(2000);
            elapsed += 2000;

            after = CoverState(out lastError);
            if (after is not null) break;
        }

        int rows = maxRows <= 0 ? 40 : Math.Min(maxRows, 500);
        bool busy = after is null;

        string envelope = "{\"action\":" + JsonSerializer.Serialize(action) +
                          ",\"caption\":" + JsonSerializer.Serialize(caption) +
                          ",\"before\":" + before +
                          ",\"after\":" + (after ?? "null") +
                          ",\"waitedMs\":" + elapsed +
                          ",\"posted\":" + posted + "}";

        if (busy)
            return new NavmeshResult("ok", "posted",
                $"{caption} posted and the editor is still computing it ({elapsed / 1000}s); triangles with cover " +
                $"before: {CoverAnyText(before)} - call mode=cover cover=status once it is idle, which reads the " +
                "counts and saves the cell",
                envelope, _paths.CkpeLogPath, ReadAfter(offset, rows));

        string finished = $"{caption} finished in {elapsed / 1000}s; triangles with cover: {CoverAnyText(before)} " +
                          $"before, {CoverAnyText(after)} after";

        if (!save)
            return new NavmeshResult("ok", "ok", finished + "; save=false, so the cover is still only in memory",
                envelope, _paths.CkpeLogPath, ReadAfter(offset, rows));

        // Constraint: cover lives in the cell's arrays until the file is written, so a finished pass goes through the
        // same save and read-back step as apply and cannot report done without a matching file.
        (NavmeshPersistence persistence, string saveJson) = SaveAndVerify("nav.save confirm", timeoutSeconds);
        envelope = envelope[..^1] + ",\"save\":" + saveJson +
                   ",\"persistence\":" + JsonSerializer.Serialize(persistence, EnvelopeJson) + "}";

        (string status, string verdictCode, string verdictMessage) = CoverVerdict(persistence, finished);

        // Constraint: a cover pass that reaches disk leaves the same record an apply does, or its proof is the answer.
        string? evidencePath = WriteEvidence("cover", ReadLoadedNumbers(), null, persistence, null, verdictMessage);

        if (evidencePath is not null)
        {
            verdictMessage += $"; evidence: {evidencePath}";
            envelope = envelope[..^1] + ",\"evidence\":" + JsonSerializer.Serialize(evidencePath) + "}";
        }

        return new NavmeshResult(status, verdictCode, verdictMessage, envelope, _paths.CkpeLogPath, ReadAfter(offset, rows));
    }

    /// Invariant: a finished cover pass reports done only when the read-back it carries is persisted, so the
    /// reply's code and the file on disk cannot disagree. It is separate from the editor so a test can drive it.
    public static (string Status, string Code, string Message) CoverVerdict(NavmeshPersistence persistence, string finished)
    {
        if (!persistence.Persisted)
        {
            return ("error", persistence.Code,
                finished + $", but the cell is not persisted ({persistence.Code}): {persistence.Message}");
        }

        return ("ok", "saved", finished + ", saved and read back: " + persistence.Message);
    }

    // Constraint: each cover command maps to one live caption and answers nothing, so its effect is read back.
    private string? CoverState(out string error)
    {
        error = "";

        if (!BlockedEditor.TryCall(_plugin, "nav.state", out string state, out string code, out string message))
        {
            error = message.Length > 0 ? message : code;
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(state);
            JsonElement cover = Member(Member(Member(document.RootElement, "navmesh"), "coverage"), "edgeCover");
            if (cover.ValueKind != JsonValueKind.Object)
            {
                error = "the loaded cell reports no cover counts";
                return null;
            }

            return cover.GetRawText();
        }
        catch (JsonException)
        {
            error = "nav.state answered without the JSON this tool reads";
            return null;
        }
    }

    private static int CoverAny(string? cover) => cover is null ? -1 : Number(ParseOrUndefined(cover), "any");

    private static string CoverAnyText(string? cover) => cover is null ? "unreadable" : CoverAny(cover).ToString();

    private static JsonElement ParseOrUndefined(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return default;
        }
    }

    // Constraint: the sweep must read the Cell View the caller sees, so the list comes from ui.cells itself.
    private List<CellRow>? SelectCells(string filter, string after, int limit, out string code, out string message)
    {
        code = "ok";
        message = "";

        string command = string.IsNullOrWhiteSpace(filter) ? "ui.cells" : $"ui.cells {filter.Trim()}";
        if (!BlockedEditor.TryCall(_plugin, command, out string payload, out code, out message))
            return null;

        List<CellRow>? rows = ParseCells(payload);
        if (rows is null)
        {
            code = "bad_reply";
            message = "the Cell View reply carried no rows to walk";
            return null;
        }

        int cap = Math.Min(limit <= 0 ? DefaultAuditCells : limit, MaxSweepCells);
        string start = (after ?? "").Trim();
        bool walking = start.Length == 0;
        var selected = new List<CellRow>();

        foreach (CellRow row in rows)
        {
            if (!walking)
            {
                if (row.CleanId.Equals(start, StringComparison.OrdinalIgnoreCase) ||
                    row.EditorId.Equals(start, StringComparison.OrdinalIgnoreCase)) walking = true;
                continue;
            }

            selected.Add(row);
            if (selected.Count >= cap) break;
        }

        return selected;
    }

    // Constraint: an exterior cell on a loaded editor can take half a minute to appear, so the proof waits by time.
    private const int LoadWindowMs = 60000;
    private const int LoadPollMs = 2000;
    private const int WorldSpaceSettleMs = 1500;

    // Constraint: a sweep that names a worldspace must select it once, not before every row it walks.
    private string _worldspace = "";

    private static bool IsAliasRow(string editorId) => editorId.EndsWith(" *", StringComparison.Ordinal);

    private static List<CellRow>? ParseCells(string payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement rows = Member(document.RootElement, "rows");
            if (rows.ValueKind != JsonValueKind.Array) return null;

            var list = new List<CellRow>();
            var byFormId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (JsonElement row in rows.EnumerateArray())
            {
                string editorId = StringMember(row, "editorId");
                if (editorId.Length == 0) continue;

                string formId = StringMember(row, "formId");
                CellRow cell = new(editorId, formId, StringMember(row, "name"), Number(row, "row"));

                // Constraint: the Cell View lists one cell twice when it carries navmesh, so the alias row is merged.
                if (formId.Length > 0 && byFormId.TryGetValue(formId, out int index))
                {
                    if (IsAliasRow(list[index].EditorId) && !IsAliasRow(editorId)) list[index] = cell;
                    continue;
                }

                if (formId.Length > 0) byFormId[formId] = list.Count;
                list.Add(cell);
            }

            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement Member(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value)
            ? value
            : default;

    private static string StringMember(JsonElement element, string name)
    {
        JsonElement value = Member(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    private static int Number(JsonElement element, string name)
    {
        JsonElement value = Member(element, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : 0;
    }

    private static string? Trimmed(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string NoCellsMessage(string filter, string after)
    {
        string trimmed = (after ?? "").Trim();
        if (trimmed.Length > 0)
            return $"the Cell View lists no cell after '{trimmed}'; pass the last cell of the previous answer as after";

        return string.IsNullOrWhiteSpace(filter)
            ? "the Cell View listed no cell to walk; load a master file first"
            : $"no cell in the Cell View matches '{filter.Trim()}'";
    }

    private sealed record CellRow(string EditorId, string FormId, string Name, int Row = -1)
    {
        // Constraint: the Cell View marks a dirty cell with a trailing star, so identity is the name without it.
        public string CleanId => EditorId.TrimEnd().EndsWith("*", StringComparison.Ordinal)
            ? EditorId.TrimEnd()[..^1].TrimEnd()
            : EditorId;
    }

    private static readonly JsonSerializerOptions EnvelopeJson = new(JsonSerializerDefaults.Web);

    // Constraint: the check's structured verdict replaces its raw lines, so no caller parses prose.
    private NavmeshResult Check()
    {
        long offset = LogLength();

        // Constraint: the plugin collects the report over successive calls, because no call may sleep on its thread.
        const int TickPauseMs = 300;
        const int TickLimit = 60;
        string payload = "";
        string code = "";
        string message = "";

        for (int tick = 0; tick < TickLimit; tick++)
        {
            if (!BlockedEditor.TryCall(_plugin, "nav.check", out payload, out code, out message))
                return new NavmeshResult("error", code, message, null, _paths.CkpeLogPath, ReadAll(offset));

            if (!Pending(payload))
                break;

            if (tick == TickLimit - 1)
                return new NavmeshResult("error", "check_incomplete",
                    "the check never settled, so no report was captured", payload, _paths.CkpeLogPath, ReadAll(offset));

            Thread.Sleep(TickPauseMs);
        }

        IReadOnlyList<string> lines = ReportLines(payload) ?? ReadAll(offset);

        // Constraint: a check that found nothing writes nothing, so check resolves the report with the same gate apply
        // saves on rather than answering unknown for the silence apply accepts.
        NavmeshCheckOutcome outcome = NavmeshCheckGate.ResolveReport(payload, lines, ReadLoadedGeometry(),
            BlockedEditor.BlockingTitle(_plugin));

        return new NavmeshResult("ok", outcome.Evidence.Code, outcome.Evidence.Reason, payload, _paths.CkpeLogPath, null)
        {
            Verdict = outcome.Verdict,
            Check = outcome.Evidence
        };
    }

    // Constraint: a generation puts its mesh in a new form and empties the one it replaced, so the run drops that
    // placeholder while the check still has to classify the cell, instead of leaving it beside the mesh.
    private string PruneSupersededForms()
    {
        if (!BlockedEditor.TryCall(_plugin, "nav.state", out string state, out _, out _)) return "null";

        IReadOnlyList<NavmeshFormCount> drop = NavmeshApplyPrune.Superseded(NavmeshApplyPrune.ReadForms(state));
        if (drop.Count == 0) return "{\"ids\":[],\"posted\":false}";

        string ids = "";
        string listed = "";

        foreach (NavmeshFormCount form in drop)
        {
            if (ids.Length > 0) ids += ",";
            if (listed.Length > 0) listed += ",";

            ids += form.FormId.ToString("X8");
            listed += "\"" + form.FormId.ToString("X8") + "\"";
        }

        bool posted = BlockedEditor.TryCall(_plugin, $"nav.prune {ids} confirm", out string reply, out _,
            out string failure);

        return "{\"ids\":[" + listed + "],\"posted\":" + (posted ? "true" : "false") +
               ",\"reply\":" + JsonSerializer.Serialize(posted ? reply : failure) + "}";
    }

    // Constraint: the plugin classifies its own check, so apply reports that verdict rather than only its own parse.
    private static string? PluginVerdict(string payload) => NavmeshCheckGate.PluginVerdict(payload);

    // Constraint: the reply carries the lines the check itself wrote, so the verdict never races a log re-read.
    private static IReadOnlyList<string>? ReportLines(string payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            if (!document.RootElement.TryGetProperty("lines", out JsonElement lines))
            {
                if (!document.RootElement.TryGetProperty("report", out JsonElement report)) return null;
                if (report.ValueKind != JsonValueKind.Object) return null;
                if (!report.TryGetProperty("lines", out lines)) return null;
            }

            if (lines.ValueKind != JsonValueKind.Array) return null;

            var list = new List<string>();
            foreach (JsonElement line in lines.EnumerateArray())
                if (line.ValueKind == JsonValueKind.String) list.Add(line.GetString() ?? "");

            return list;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Constraint: only the three generation strategies this build names may be forwarded.
    // Constraint: a stage reply is not a finished pipeline, so it must never be reported as an applied one.
    private static bool Pending(string payload) =>
        payload.Contains("\"pending\":true", StringComparison.Ordinal);

    private static string Stage(string payload)
    {
        const string key = "\"stage\":\"";
        int at = payload.IndexOf(key, StringComparison.Ordinal);
        if (at < 0) return "unknown";

        int start = at + key.Length;
        int end = payload.IndexOf('"', start);
        return end > start ? payload.Substring(start, end - start) : "unknown";
    }

    private static bool ValidKind(string kind)
        => (kind ?? "").Trim().ToLowerInvariant() is "recast" or "havok" or "advanced";

    private static NavmeshResult InvalidKind()
        => new("error", "invalid_argument", "kind must be recast, havok or advanced", null, null, null);

    private static NavmeshResult Refused(string message)
        => new("error", "confirmation_required", message, null, null, null);

    // Constraint: the log offset is taken before the post, so the report is only what this command wrote.
    // Constraint: apply is staged in the plugin, so a failure here leaves a run open that only this can close.
    private bool AbortApply() =>
        BlockedEditor.TryCall(_plugin, "nav.apply abort", out _, out _, out _);

    private NavmeshResult Post(string command, int maxRows)
    {
        int rows = maxRows <= 0 ? 40 : Math.Min(maxRows, 500);
        long offset = LogLength();

        if (!BlockedEditor.TryCall(_plugin, command, out string payload, out string code, out string message))
            return new NavmeshResult("error", code, message, null, _paths.CkpeLogPath, null);

        return new NavmeshResult("ok", "ok", command, payload, _paths.CkpeLogPath, ReadAfter(offset, rows));
    }

    private long LogLength()
    {
        try
        {
            var log = new FileInfo(_paths.CkpeLogPath);
            return log.Exists ? log.Length : 0;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private IReadOnlyList<string> ReadAll(long offset) => ReadAfter(offset, int.MaxValue);

    private IReadOnlyList<string> ReadAfter(long offset, int maxRows)
    {
        Thread.Sleep(LogSettleMs);

        try
        {
            using var stream = new FileStream(_paths.CkpeLogPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (offset > stream.Length) offset = 0;
            stream.Seek(offset, SeekOrigin.Begin);

            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                if (line.Length == 0) continue;
                // Constraint: the plugin logs our own request and reply, and that echo is not a verdict.
                if (line.StartsWith("CALL ", StringComparison.Ordinal) ||
                    line.StartsWith("RPLY ", StringComparison.Ordinal)) continue;
                lines.Add(line.Length > MaxLineLength ? line[..MaxLineLength] : line);
            }

            return lines.Count <= maxRows ? lines : lines.GetRange(lines.Count - maxRows, maxRows);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }
}
