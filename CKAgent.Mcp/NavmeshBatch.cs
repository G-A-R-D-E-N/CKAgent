using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Text.Json;

namespace Garden.CkAgent;

// Constraint: a batch must answer per cell, so one bad row is named instead of stopping the pass.
public sealed partial class NavmeshTools
{
    private const int AuditSettleMs = 1000;

    private sealed record CellNumbers(
        string EditorId, string FormId, bool Interior, int References, int Forms, int ReadForms, int EmptyForms,
        int Vertices, int Triangles);

    // Constraint: saving a master opens a target dialog, so the name is typed into it and Save pressed, not left to a user.
    private bool AnswerTargetDialog(IntPtr dialog, string saveAs)
    {
        string handle = "0x" + dialog.ToInt64().ToString("X");

        // Constraint: UI Automation inside the editor cannot reach its own dialog, so the tool's own helper runs
        // outside that process, where UI Automation answers, and selects the target plugin in the dialog's list.
        if (RunUiaHelper(saveAs, out string helperOutput, out string helperError)) return true;

        // The in-process path and the keystroke path stay as fallbacks and report why they failed.
        lastDialogError = helperOutput.Length == 0 ? helperError : helperOutput;
        if (BlockedEditor.TryCall(_plugin, $"uia.save {handle} {saveAs}", out _, out _, out _)) return true;
        if (!BlockedEditor.TryCall(_plugin, $"ui.type {handle} {saveAs}", out _, out _, out _)) return false;
        return BlockedEditor.TryCall(_plugin, $"ui.click {handle} Save", out _, out _, out _);
    }

    private static string? FindFirstClass(string tree, string wanted)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(tree);

            string? Walk(JsonElement node)
            {
                if (StringMember(node, "class").Equals(wanted, StringComparison.Ordinal)) return StringMember(node, "hwnd");
                JsonElement children = Member(node, "children");
                if (children.ValueKind != JsonValueKind.Array) return null;
                foreach (JsonElement child in children.EnumerateArray())
                {
                    string? found = Walk(child);
                    if (found is not null) return found;
                }

                return null;
            }

            return Walk(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Constraint: the helper is the caller's own tooling, so answering the dialog costs no keystroke from a person.
    private bool RunUiaHelper(string saveAs, out string output, out string error)
    {
        output = "";
        error = "";

        string script = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools",
            "uia-target-plugin.ps1");
        if (!File.Exists(script)) script = Path.Combine(Directory.GetCurrentDirectory(), "tools", "uia-target-plugin.ps1");
        if (!File.Exists(script))
        {
            error = "uia helper not found beside the tool";
            return false;
        }

        try
        {
            var info = new ProcessStartInfo("powershell")
            {
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -File \"{saveAs}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using Process helper = Process.Start(info)!;
            output = helper.StandardOutput.ReadToEnd().Trim();
            error = helper.StandardError.ReadToEnd().Trim();
            helper.WaitForExit(60000);
            return helper.HasExited && helper.ExitCode == 0;
        }
        catch (Exception exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private string lastDialogError = "";

    // Constraint: the caller names the file to write, because the editor's Save writes whatever file it has open.
    private SaveOutcome SavePlugin(string plugin, string saveAs, int timeoutSeconds)
    {
        Process? editor = EditorDiscovery.FindEditor(_paths.CreationKitPath);
        if (editor is null)
            return new SaveOutcome("error", "editor_not_running", "no editor is running for this install",
                plugin, null, null, null, null, 0, null);

        IntPtr window = EditorDiscovery.FindMainWindow((uint)editor.Id);
        if (window == IntPtr.Zero)
            return new SaveOutcome("error", "window_not_ready", "the editor's main window is not up yet",
                plugin, null, null, null, null, 0, null);

        string target = string.IsNullOrWhiteSpace(saveAs) ? plugin : saveAs.Trim();
        return _save.Save(plugin, window, Math.Clamp(timeoutSeconds, 10, 600),
            string.IsNullOrWhiteSpace(saveAs) ? null : saveAs,
            dialog => AnswerTargetDialog(dialog, target));
    }

    private static CellNumbers ReadNumbers(string state)
    {
        using JsonDocument document = JsonDocument.Parse(state);
        JsonElement root = document.RootElement;
        JsonElement cell = Member(root, "cell");
        JsonElement navmesh = Member(root, "navmesh");
        JsonElement coverage = Member(navmesh, "coverage");

        return new CellNumbers(
            StringMember(cell, "editorId"),
            StringMember(cell, "formId"),
            Member(cell, "interior").ValueKind == JsonValueKind.True,
            Number(cell, "references"),
            Number(navmesh, "navmeshForms"),
            Number(coverage, "readForms"),
            Number(coverage, "emptyForms"),
            Number(coverage, "vertices"),
            Number(coverage, "triangles"));
    }

    // Constraint: a generated mesh that is not written to a plugin is not delivered, so the batch saves by default.
    private NavmeshResult MeshBatch(string filter, string after, int limit, int waitSeconds, bool dryRun,
                                   bool save, string plugin, string saveAs, int saveTimeoutSeconds)
    {
        string target = (plugin ?? "").Trim();
        if (save && target.Length == 0)
            return Refused("kind=mesh saves every cell it generates, so it needs plugin=<file in Data>, for example " +
                           "plugin=Fallout4.esm; pass save=false to leave the meshes in memory only");

        // Constraint: the rows come from whichever worldspace the Cell View shows, so it is selected first.
        if (!UseWorldSpace(_worldspace, out string spaceCode, out string spaceMessage))
            return new NavmeshResult("error", spaceCode, spaceMessage, null, null, null);

        List<CellRow>? rows = SelectCells(filter, after, limit <= 0 ? DefaultBatchCells : limit,
            out string code, out string message);
        if (rows is null)
            return new NavmeshResult("error", code, message, null, null, null);

        if (rows.Count == 0)
            return new NavmeshResult("error", "no_cells", NoCellsMessage(filter, after), null, null, null);

        var names = new List<object>();
        foreach (CellRow row in rows) names.Add(new { cell = row.CleanId, formId = row.FormId });

        if (dryRun)
        {
            string plan = JsonSerializer.Serialize(
                new { kind = "mesh", after = Trimmed(after), cells = names, nextAfter = rows[rows.Count - 1].CleanId },
                EnvelopeJson);

            return new NavmeshResult("ok", "dry_run",
                $"a mesh batch of {rows.Count} cell(s) would strip and regenerate each cell's navmesh in one pass, " +
                $"then save {target} once per cell and back it up first; nothing was posted", plan, _paths.CkpeLogPath, null);
        }

        var outcomes = new List<object>();
        int meshed = 0;
        string? firstFailure = null;

        foreach (CellRow row in rows)
        {
            if (!LoadCell(row, out string loadCode, out string loadMessage))
            {
                firstFailure ??= $"{row.CleanId}: {loadMessage}";
                outcomes.Add(new
                {
                    cell = row.CleanId, formId = row.FormId, loaded = false, code = loadCode,
                    message = loadMessage, agree = false,
                });
                continue;
            }

            if (!BlockedEditor.TryCall(_plugin, "nav.state", out string before, out string beforeCode,
                                       out string beforeMessage))
            {
                firstFailure ??= $"{row.CleanId}: {beforeMessage}";
                outcomes.Add(new
                {
                    cell = row.CleanId, formId = row.FormId, loaded = true, code = beforeCode,
                    message = beforeMessage, agree = false,
                });
                continue;
            }

            CellNumbers loaded;
            try
            {
                loaded = ReadNumbers(before);
            }
            catch (JsonException)
            {
                outcomes.Add(new
                {
                    cell = row.CleanId, formId = row.FormId, loaded = true, code = "bad_reply",
                    message = "nav.state answered without the geometry this tool reads", agree = false,
                });
                firstFailure ??= $"{row.CleanId}: nav.state answered without the geometry this tool reads";
                continue;
            }

            // Constraint: the carried form id is the proof this row became the loaded cell, not the row text.
            if (row.FormId.Length > 0 && loaded.FormId.Length > 0 &&
                !row.FormId.Equals(loaded.FormId, StringComparison.OrdinalIgnoreCase))
            {
                string mismatch = $"row {row.FormId} became loaded cell {loaded.FormId}";
                firstFailure ??= $"{row.CleanId}: {mismatch}";
                outcomes.Add(new
                {
                    cell = row.CleanId, formId = row.FormId, loaded = false, code = "form_mismatch",
                    message = mismatch, agree = false,
                });
                continue;
            }

            if (!BlockedEditor.TryCall(_plugin, "nav.strip confirm", out string strip, out string stripCode,
                                       out string stripMessage))
            {
                firstFailure ??= $"{row.CleanId}: {stripMessage}";
                outcomes.Add(new
                {
                    cell = row.CleanId, formId = loaded.FormId, loaded = true, code = stripCode,
                    message = stripMessage, agree = false,
                });
                continue;
            }

            NavmeshResult generated = Generate("recast", waitSeconds);
            if (!string.Equals(generated.Code, "generated", StringComparison.Ordinal))
            {
                firstFailure ??= $"{row.CleanId}: {generated.Message}";
                outcomes.Add(new
                {
                    cell = row.CleanId, formId = loaded.FormId, loaded = true, code = generated.Code,
                    message = generated.Message, strip, generate = generated.Payload, agree = false,
                });
                continue;
            }

            if (!BlockedEditor.TryCall(_plugin, "nav.state", out string afterState, out _, out _))
            {
                outcomes.Add(new
                {
                    cell = row.CleanId, formId = loaded.FormId, loaded = true, code = "state_unreadable",
                    message = "the cell could not be read back after the build", strip,
                    generate = generated.Payload, agree = false,
                });
                continue;
            }

            CellNumbers state = ReadNumbers(afterState);

            // Constraint: the batch carries its own audit read, so one call proves payload, state and audit agree.
            Thread.Sleep(AuditSettleMs);
            if (!BlockedEditor.TryCall(_plugin, "nav.state", out string auditState, out _, out _))
            {
                outcomes.Add(new
                {
                    cell = row.CleanId, formId = loaded.FormId, loaded = true, code = "audit_unreadable",
                    message = "the cell could not be read back a second time, so payload and audit were not compared",
                    strip, generate = generated.Payload, agree = false,
                });
                continue;
            }

            CellNumbers audit = ReadNumbers(auditState);
            JsonElement afterNumbers = Member(
                JsonDocument.Parse(generated.Payload ?? "{}").RootElement, "after");
            int afterForms = Number(afterNumbers, "forms");
            int afterVertices = Number(afterNumbers, "vertices");
            int afterTriangles = Number(afterNumbers, "triangles");

            bool agree = afterForms == state.Forms && afterVertices == state.Vertices &&
                         afterTriangles == state.Triangles &&
                         audit.Forms == state.Forms && audit.Vertices == state.Vertices &&
                         audit.Triangles == state.Triangles &&
                         audit.FormId.Equals(loaded.FormId, StringComparison.OrdinalIgnoreCase) &&
                         state.Vertices > 0 && state.Triangles > 0;
            string outcomeCode = agree ? "meshed" : "disagree";
            object? saved = null;
            bool persisted = false;

            if (agree && save)
            {
                SaveOutcome written = SavePlugin(target, saveAs, saveTimeoutSeconds);
                persisted = written.Status == "ok";
                saved = new
                {
                    status = written.Status,
                    code = written.Code,
                    message = written.Message,
                    plugin = written.Plugin,
                    pluginPath = written.PluginPath,
                    backupPath = written.BackupPath,
                    beforeBytes = written.BeforeBytes,
                    afterBytes = written.AfterBytes,
                    savedAtUtc = persisted ? DateTime.UtcNow.ToString("O") : null,
                    elapsedMs = written.ElapsedMs,
                };

                if (!persisted)
                {
                    // Constraint: a mesh still in memory is reported as unfinished, never as a delivered cell.
                    outcomeCode = "save_failed";
                    firstFailure ??= $"{row.CleanId}: {written.Code}: {written.Message}";
                }
                else
                {
                    outcomeCode = "saved";
                }
            }

            if (agree && (!save || persisted)) meshed++;
            else if (!agree) firstFailure ??= $"{row.CleanId}: generate and state disagree";

            outcomes.Add(new
            {
                cell = row.CleanId,
                formId = loaded.FormId,
                interior = loaded.Interior,
                references = loaded.References,
                loaded = true,
                code = outcomeCode,
                persisted,
                save = saved,
                strip,
                before = new { forms = loaded.Forms, vertices = loaded.Vertices, triangles = loaded.Triangles },
                generate = generated.Payload,
                state = new { forms = state.Forms, vertices = state.Vertices, triangles = state.Triangles },
                audit = new
                {
                    cell = audit.EditorId,
                    formId = audit.FormId,
                    interior = audit.Interior,
                    references = audit.References,
                    forms = audit.Forms,
                    vertices = audit.Vertices,
                    triangles = audit.Triangles,
                    loaded = audit.FormId.Equals(loaded.FormId, StringComparison.OrdinalIgnoreCase),
                },
                agree,
            });
        }

        string last = rows[rows.Count - 1].CleanId;
        string envelope = JsonSerializer.Serialize(new
        {
            kind = "mesh",
            after = Trimmed(after),
            nextAfter = last,
            batched = outcomes.Count,
            meshed,
            claimedPlugin = save ? target : null,
            cells = outcomes,
        }, EnvelopeJson);

        bool complete = meshed == outcomes.Count;
        string headline = complete
            ? $"{outcomes.Count} exterior/interior cell(s) stripped and regenerated, " +
              (save ? $"saved to {target} " : "left in memory (") +
              (save ? "and each agreeing with nav.state" : "save=false") + (save ? "" : ")")
            : $"{outcomes.Count} cell(s) attempted, {meshed} " + (save ? "saved" : "agree") +
              $"; first that did not: {firstFailure}";

        return new NavmeshResult("ok", complete ? (save ? "saved" : "meshed") : "incomplete",
            headline, envelope, _paths.CkpeLogPath, null);
    }
}
