using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace Garden.CkAgent;

public sealed record CommandListResult(
    string Status,
    string Code,
    string Message,
    int Count,
    string? Build,
    string? CapturedUtc,
    IReadOnlyList<CommandEntry> Commands);

public sealed record InvokeCommandResult(
    string Status,
    string Code,
    string Message,
    string? Command,
    uint? Id,
    bool Posted);

public sealed record JobLogResult(
    string Status,
    string Code,
    string Message,
    string? JobId,
    string? Path,
    int TailLines,
    string? Log);

[McpServerToolType]
public sealed class CkTools
{
    private readonly EditorPaths _paths;
    private readonly CommandStore _commands;
    private readonly SaveCoordinator _save;
    private readonly JobRunner _jobs;
    private readonly PluginClient _plugin;

    public CkTools(EditorPaths paths, CommandStore commands, SaveCoordinator save, JobRunner jobs,
                   PluginClient plugin)
    {
        _paths = paths;
        _commands = commands;
        _save = save;
        _jobs = jobs;
        _plugin = plugin;
    }

    [McpServerTool(Name = "ck.status"),
     Description("Report the installed Creation Kit and the running editor: build, process, main window, " +
                 "command manifest age, and whether the in-process CKAgent plugin is listening. When a dialog " +
                 "is blocking the editor it reports the dialog title, the captions of its buttons, and the " +
                 "caption the automatic dismissal will press, so a caller can see both what is available and " +
                 "what will happen. A window that exists but is hidden is reported as editor_invisible, not as a " +
                 "missing editor, and stateDirectoryNote names a state folder used after the default was refused. " +
                 "The reply also states this client's own integrity level, elevation and enabled privileges, and " +
                 "which evidence found the editor — its process, its frame window, or neither — because a host at " +
                 "low integrity is handed no editor process at all. pluginPipe names the pipe the plugin answered " +
                 "on, and lowIntegrityPipeAllowed says whether this host may try the low-labelled one at all.")]
    public EditorStatusResult Status()
    {
        var document = _commands.Load();
        var process = EditorDiscovery.FindEditor(_paths.CreationKitPath);

        // Constraint: a host handed no editor process still has the frame window, which proves the editor is up.
        IntPtr frameOnly = EditorDiscovery.FindFrameWindow(out uint framePid);
        int frameOnlyPid = (int)framePid;
        var view = ClientView.Describe(process != null, frameOnly != IntPtr.Zero);
        string? probedPipe = EditorDiscovery.ProbePipe(_paths.LowIntegrityPipeAllowed);

        if (process == null && frameOnly == IntPtr.Zero)
        {
            return new EditorStatusResult("ok", "editor_not_running",
                "no CreationKit.exe for this install is running, and no frame window was found",
                false, null, null, null,
                _paths.CreationKitPath, _paths.GameDirectory, _paths.EditorVersion,
                document?.Commands.Count ?? 0, document?.Build, document?.CapturedUtc,
                probedPipe != null, _paths.StateDirectory, null, null, null, _paths.StateDirectoryNote,
                view.Integrity, view.Elevated, view.Privileges, view.SeesProcess, view.SeesWindow, view.Sight,
                probedPipe, _paths.LowIntegrityPipeAllowed);
        }

        IntPtr window = process != null
            ? EditorDiscovery.FindMainWindow((uint)process.Id)
            : frameOnly;
        int? pid = process?.Id ?? frameOnlyPid;
        bool pipe = probedPipe != null;

        string? blockedBy = null;
        IReadOnlyList<string>? modalButtons = null;
        string? dismissBy = null;
        if (pipe && _plugin.TryCall("status", out string pluginStatus, out _, out _))
        {
            try
            {
                using JsonDocument statusDocument = JsonDocument.Parse(pluginStatus);
                JsonElement root = statusDocument.RootElement;
                if (root.TryGetProperty("blockedBy", out JsonElement blocked) && blocked.ValueKind == JsonValueKind.String)
                {
                    blockedBy = blocked.GetString();
                }

                if (root.TryGetProperty("modalButtons", out JsonElement buttons) && buttons.ValueKind == JsonValueKind.Array)
                {
                    var captions = new List<string>();
                    foreach (JsonElement caption in buttons.EnumerateArray())
                    {
                        captions.Add(caption.GetString() ?? string.Empty);
                    }

                    modalButtons = captions;
                }

                if (root.TryGetProperty("dismissBy", out JsonElement dismiss) && dismiss.ValueKind == JsonValueKind.String)
                {
                    dismissBy = dismiss.GetString();
                }
            }
            catch (JsonException)
            {
                blockedBy = null;
                modalButtons = null;
                dismissBy = null;
            }
        }

        bool visible = window != IntPtr.Zero && Win32.IsWindowVisible(window);
        (string code, string message) =
            EditorDiscovery.DescribeStatus(process != null, window != IntPtr.Zero, visible);

        return new EditorStatusResult("ok", code, message, true, pid,
            window != IntPtr.Zero ? Win32.WindowText(window) : null,
            window != IntPtr.Zero ? window.ToInt64() : null,
            _paths.CreationKitPath, _paths.GameDirectory, _paths.EditorVersion,
            document?.Commands.Count ?? 0, document?.Build, document?.CapturedUtc,
            pipe, _paths.StateDirectory, blockedBy, modalButtons, dismissBy, _paths.StateDirectoryNote,
            view.Integrity, view.Elevated, view.Privileges, view.SeesProcess, view.SeesWindow, view.Sight,
            _plugin.ReachedPipe ?? probedPipe, _paths.LowIntegrityPipeAllowed);
    }

    [McpServerTool(Name = "ck.commands"),
     Description("Read the editor's menu tree into the generated command manifest (build-specific ids), " +
                 "or return the cached manifest. Set refresh to true after a Creation Kit update.")]
    public CommandListResult Commands(bool refresh = false)
    {
        if (!refresh)
        {
            var cached = _commands.Load();
            if (cached == null)
            {
                return new CommandListResult("error", "no_manifest",
                    "no command manifest yet: call ck.commands with refresh=true while the editor is running",
                    0, null, null, Array.Empty<CommandEntry>());
            }

            return new CommandListResult("ok", "cached", $"cached manifest from build {cached.Build}",
                cached.Commands.Count, cached.Build, cached.CapturedUtc, cached.Commands);
        }

        var process = EditorDiscovery.FindEditor(_paths.CreationKitPath);
        if (process == null)
            return new CommandListResult("error", "editor_not_running",
                "the editor must be running to capture menu command ids", 0, null, null, Array.Empty<CommandEntry>());

        IntPtr window = EditorDiscovery.FindMainWindow((uint)process.Id);
        if (window == IntPtr.Zero)
            return new CommandListResult("error", "window_not_ready",
                "the editor's main window is not up yet", 0, null, null, Array.Empty<CommandEntry>());

        IntPtr menu = Win32.GetMenu(window);
        if (menu == IntPtr.Zero)
            return new CommandListResult("error", "no_menu",
                "the main window has no menu", 0, null, null, Array.Empty<CommandEntry>());

        var document = _commands.Capture(menu, _paths.EditorVersion ?? "unknown");

        return new CommandListResult("ok", "captured",
            $"captured {document.Commands.Count} commands from build {document.Build}",
            document.Commands.Count, document.Build, document.CapturedUtc, document.Commands);
    }

    [McpServerTool(Name = "ck.invoke_command"),
     Description("Run one editor menu command by name (\"File/Save\", \"World/PreCombine Geometry For World\", " +
                 "or a numeric id). Resolves against the generated manifest. Use dryRun to only resolve.")]
    public InvokeCommandResult InvokeCommand(string command, bool dry_run = false)
    {
        if (!_commands.TryResolve(command, out CommandEntry entry, out string resolveError))
            return new InvokeCommandResult("error", "unresolved", resolveError, command, null, false);

        if (dry_run)
            return new InvokeCommandResult("ok", "resolved",
                $"'{entry.Path}' is id {entry.Id}; nothing was posted", entry.Path, entry.Id, false);

        var process = EditorDiscovery.FindEditor(_paths.CreationKitPath);
        if (process == null)
            return new InvokeCommandResult("error", "editor_not_running",
                "no editor is running for this install", entry.Path, entry.Id, false);

        IntPtr window = EditorDiscovery.FindMainWindow((uint)process.Id);
        if (window == IntPtr.Zero)
            return new InvokeCommandResult("error", "window_not_ready",
                "the editor's main window is not up yet", entry.Path, entry.Id, false);

        if (!Win32.IsWindowEnabled(window))
        {
            // A modal editor must be refused: the command would queue behind the dialog.
            return new InvokeCommandResult("warning", "editor_modal",
                "the editor has a modal dialog open; close it before invoking commands",
                entry.Path, entry.Id, false);
        }

        if (!Win32.PostMessageW(window, Win32.WmCommand, (IntPtr)entry.Id, IntPtr.Zero))
            return new InvokeCommandResult("error", "post_failed",
                "PostMessage to the editor window failed", entry.Path, entry.Id, false);

        return new InvokeCommandResult("ok", "posted",
            $"posted '{entry.Path}' (id {entry.Id})", entry.Path, entry.Id, true);
    }

    [McpServerTool(Name = "ck.save"),
     Description("Back up one plugin out of Data, then run the editor's Save command and confirm the file " +
                 "changed. This is the only tool that writes a plugin, and it always copies first.")]
    public SaveOutcome Save(string plugin, int timeout_seconds = 120)
    {
        var process = EditorDiscovery.FindEditor(_paths.CreationKitPath);
        if (process == null)
        {
            return new SaveOutcome("error", "editor_not_running",
                "no editor is running for this install", plugin, null, null, null, null, 0, null);
        }

        IntPtr window = EditorDiscovery.FindMainWindow((uint)process.Id);
        if (window == IntPtr.Zero)
        {
            return new SaveOutcome("error", "window_not_ready",
                "the editor's main window is not up yet", plugin, null, null, null, null, 0, null);
        }

        if (!_commands.TryResolve("File/Save", out _, out _))
        {
            // Capture once on first run; the ids must come from the live menu, never a constant.
            IntPtr menu = Win32.GetMenu(window);
            if (menu != IntPtr.Zero)
                _commands.Capture(menu, _paths.EditorVersion ?? "unknown");
        }

        return _save.Save(plugin, window, timeout_seconds);
    }

    [McpServerTool(Name = "ck.generate_precombine"),
     Description("Start a headless precombine generation job (CreationKit.exe -GeneratePreCombined). " +
                 "Returns a job id immediately; poll with ck.job_status and tail with ck.job_log.")]
    public JobStart GeneratePrecombine(string plugin, string mode = "clean", string scope = "all") =>
        _jobs.Start(JobKind.Precombine, plugin, mode, scope);

    [McpServerTool(Name = "ck.generate_previs"),
     Description("Start a headless previs generation job (CreationKit.exe -GeneratePreVisData). " +
                 "Run precombine first; returns a job id immediately.")]
    public JobStart GeneratePrevis(string plugin, string mode = "clean", string scope = "all") =>
        _jobs.Start(JobKind.Previs, plugin, mode, scope);

    [McpServerTool(Name = "ck.job_status"),
     Description("Report one batch job, or the running job when no id is given, including its artifacts " +
                 "and the editor warnings captured for it.")]
    public JobStatusResult JobStatus(string? job = null)
    {
        var record = _jobs.Get(job);
        if (record == null)
        {
            return new JobStatusResult("error", string.IsNullOrWhiteSpace(job) ? "no_running_job" : "job_not_found",
                string.IsNullOrWhiteSpace(job) ? "no job is running" : $"no job '{job}'", null,
                _jobs.All().Select(j => j.Id).ToList());
        }

        if (string.IsNullOrWhiteSpace(job) && record.State == "running")
            return new JobStatusResult("ok", "running", "job is running", record, null);

        return new JobStatusResult("ok", record.State, record.Message ?? record.State, record, null);
    }

    [McpServerTool(Name = "ck.job_log"),
     Description("Tail a batch job's console log.")]
    public JobLogResult JobLog(string? job = null, int tail_lines = 80)
    {
        var record = _jobs.Get(job);
        if (record == null)
        {
            return new JobLogResult("error", "job_not_found",
                string.IsNullOrWhiteSpace(job) ? "no job is running" : $"no job '{job}'",
                null, null, tail_lines, null);
        }

        string tail = JobRunner.ReadTail(record.LogPath, tail_lines);
        return new JobLogResult("ok", "ok", $"last {tail_lines} lines", record.Id, record.LogPath, tail_lines, tail);
    }
}

// Camera reads must go through the plugin: only it can read the live window object.
public sealed record JobStatusResult(
    string Status,
    string Code,
    string Message,
    JobRecord? Job,
    IReadOnlyList<string>? KnownJobs);

public sealed record ResolveResult(
    string Status,
    string Code,
    string Message,
    string? Payload);

public sealed record CellResult(
    string Status,
    string Code,
    string Message,
    string? Payload);

// Cell selection must go through the plugin: the Cell View is a live control inside the editor process.
[McpServerToolType]
public sealed class CellTools
{
    private readonly PluginClient _plugin;

    public CellTools(PluginClient plugin) => _plugin = plugin;

    [McpServerTool(Name = "ck.cells"),
     Description("List the cells in the editor's Cell View as EditorID, FormID, name and row index, " +
                 "optionally filtered by EditorID or display name. The Cell View shows one collection at " +
                 "a time — Interiors, Recent Cells, or one worldspace — so an exterior worldspace is " +
                 "listed only after its entry is selected: read the pane's first ComboBox with " +
                 "method=ui.tree and set it with method=ui.select before listing or loading by name. " +
                 "Needs a loaded master file and the CKAgent plugin installed.")]
    public CellResult Cells(string? filter = null)
    {
        string command = string.IsNullOrWhiteSpace(filter) ? "ui.cells" : $"ui.cells {filter!.Trim()}";
        if (!BlockedEditor.TryCall(_plugin, command, out string payload, out string code, out string message))
            return new CellResult("error", code, message, null);

        return new CellResult("ok", "ok", command, payload);
    }

    [McpServerTool(Name = "ck.cell_load"),
     Description("Open a cell in the render window by EditorID or display name, exactly as double-clicking " +
                 "it in the Cell View does, and report the cell the render window actually holds after the " +
                 "load, so a wrong-cell load is caught in the same reply: the opened EditorID and form id " +
                 "accompany the matched row, and cell_not_loaded names the cell that stayed when the try did " +
                 "nothing. Resolves against the collection the Cell View's combo has selected, so a cell in " +
                 "an exterior worldspace needs that entry selected first (ck.cells explains how) and fails " +
                 "as no_cell without it; exterior cells share the name Wilderness, so those are opened by " +
                 "row with method=ui.notify after reading the list with method=ui.items. Refuses a name that " +
                 "cannot travel in one protocol line.")]
    public CellResult CellLoad(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new CellResult("error", "invalid_argument", "name is required", null);

        string trimmed = name.Trim();
        if (trimmed.Length > 200 || trimmed.Contains('\n') || trimmed.Contains('\r') || trimmed.Contains('\t'))
            return new CellResult("error", "invalid_argument",
                "name must be a single line of at most 200 characters", null);

        CellWatch.OpenCell? before = CellWatch.Read(_plugin);

        if (!BlockedEditor.TryCall(_plugin, $"ui.cellload {trimmed}", out string payload, out string code, out string message))
            return new CellResult("error", code, message, payload);

        string? matched = CellWatch.Matched(payload);
        CellWatch.OpenCell? opened = CellWatch.WaitForChange(_plugin, before, trimmed, matched);

        if (opened == null)
        {
            return new CellResult("error", "cell_not_loaded",
                $"the Cell View accepted {trimmed}, but the render window still holds " +
                $"{CellWatch.Describe(before)} after 15 s",
                CellWatch.WithOpened(payload, trimmed, before));
        }

        string openedCell = CellWatch.Describe(opened);

        // Invariant: a display-name row opens a cell whose EditorID differs, so the name is reported, not compared.
        return new CellResult("ok", "ok", $"ui.cellload {trimmed} -> opened {openedCell}",
            CellWatch.WithOpened(payload, trimmed, opened));
    }

    [McpServerTool(Name = "ck.cell"),
     Description("Report the cell the render window currently holds, so a load is confirmed rather than " +
                 "assumed. Returns the cell's EditorID and form id alongside the render window and cell " +
                 "pointers, plus readable=false when the load has not settled enough to read them.")]
    public CellResult Current()
    {
        if (!BlockedEditor.TryCall(_plugin, "cell", out string payload, out string code, out string message))
            return new CellResult("error", code, message, null);

        return new CellResult("ok", "ok", "cell", payload);
    }
}

public sealed record UiResult(
    string Status,
    string Code,
    string Message,
    string? Payload);

// Constraint: the editor's controls are only reachable in-process, so this must stay a passthrough.
[McpServerToolType]
public sealed class UiTools
{
    private static readonly string[] Methods =
    {
        "ui.frame", "ui.windows", "ui.tree", "ui.items", "ui.select", "ui.notify",
        "ui.command", "ui.menu", "ui.click", "ui.modals", "ui.cells", "ui.cellload", "ui.cellclose", "ui.dataload",
    };

    private readonly PluginClient _plugin;

    public UiTools(PluginClient plugin) => _plugin = plugin;

    [McpServerTool(Name = "ck.ui"),
     Description("Drive the editor's own controls without the mouse: ui.items reads rows or tree nodes, " +
                 "ui.select selects one, ui.notify sends NM_DBLCLK (ui.notify <hwnd> dblclk [index]), " +
                 "ui.command runs a menu command by path (ui.command File/Data) or by id, ui.click presses " +
                 "a dialog button by caption, ui.modals lists dialogs and flags the hidden one blocking the " +
                 "editor (ui.modals reveal <hwnd> shows it), ui.windows and ui.tree inspect, ui.menu dumps it. " +
                 "A call refused while a hidden dialog blocks the editor is retried after that dialog is " +
                 "revealed, and the failure names the dialog if the block remains. " +
                 "Args is the tool's own argument tail, e.g. method=ui.notify, args=0x1234 dblclk 0.")]
    public UiResult Ui(string method, string? args = null)
    {
        string chosen = (method ?? "").Trim();
        if (Array.IndexOf(Methods, chosen) < 0)
            return new UiResult("error", "invalid_argument",
                "method must be one of: " + string.Join(", ", Methods), null);

        string tail = (args ?? "").Trim();
        if (tail.Contains('\n') || tail.Contains('\r'))
            return new UiResult("error", "invalid_argument", "args must be a single line", null);

        string command = tail.Length == 0 ? chosen : chosen + " " + tail;
        if (!BlockedEditor.TryCall(_plugin, command, out string payload, out string code, out string message))
            return new UiResult("error", code, message, null);

        return new UiResult("ok", "ok", command, payload);
    }

    [McpServerTool(Name = "ck.dismiss"),
     Description("Clear the dialog blocking the editor on request, for when a caller sees the block before " +
                 "anything has failed: it takes the dialog ui.modals flags as blocking, or the hwnd given, " +
                 "presses the same caption the automatic dismissal uses, and reports that caption and whether " +
                 "the block is gone. Nothing blocking is reported as nothing_blocked, not as an error.")]
    public UiResult Dismiss(string? hwnd = null)
    {
        string target = (hwnd ?? string.Empty).Trim();
        string title = string.Empty;
        bool blocking = target.Length == 0;

        if (!_plugin.TryCall("ui.modals", out string modals, out string modalsCode, out string modalsMessage))
            return new UiResult("error", modalsCode, modalsMessage, null);

        // Constraint: the modal list names the target and its title, so an explicit hwnd is reported the same way.
        try
        {
            using JsonDocument document = JsonDocument.Parse(modals);
            foreach (JsonElement modal in document.RootElement.EnumerateArray())
            {
                string handle = modal.TryGetProperty("hwnd", out JsonElement hwndText) ? hwndText.GetString() ?? string.Empty : string.Empty;
                bool isBlocking = modal.TryGetProperty("blocking", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
                bool wanted = blocking ? isBlocking : string.Equals(handle, target, StringComparison.OrdinalIgnoreCase);
                if (!wanted)
                    continue;

                target = handle;
                title = modal.TryGetProperty("title", out JsonElement named) ? named.GetString() ?? string.Empty : string.Empty;
                break;
            }
        }
        catch (JsonException)
        {
            return new UiResult("error", "bad_payload", "ui.modals did not return JSON", null);
        }

        if (target.Length == 0)
            return new UiResult("ok", "nothing_blocked", "no dialog is blocking the editor", null);


        if (!_plugin.TryCall($"ui.dismiss {target}", out string dismissed, out string code, out string message))
            return new UiResult("error", code, message, null);

        // Constraint: the press is only reported as a dismissal once the dialog is gone, not when the click was posted.
        bool cleared = false;
        string blockedBy = string.Empty;
        for (int attempt = 0; attempt < 12; ++attempt)
        {
            if (!_plugin.TryCall("status", out string status, out _, out _))
                break;

            blockedBy = StatusString(status, "blockedBy");
            if (blockedBy.Length == 0 && ModalGone(target))
            {
                cleared = true;
                break;
            }

            Thread.Sleep(120);
        }

        JsonObject report = JsonNode.Parse(dismissed) as JsonObject ?? new JsonObject();
        report["title"] = title;
        report["cleared"] = cleared;
        report["blockedBy"] = blockedBy;
        string pressed = report["by"] is JsonNode chosen ? chosen.ToString() : string.Empty;

        return cleared
            ? new UiResult("ok", "dismissed", $"dismissed '{title}' with {pressed}", report.ToJsonString())
            : new UiResult("ok", "still_blocked", $"pressed {pressed} but the editor is still blocked by '{blockedBy}'",
                report.ToJsonString());
    }

    /// Constraint: a dialog on screen names no block, so the handle leaving the modal list is the only proof it went.
    private bool ModalGone(string hwnd)
    {
        if (!_plugin.TryCall("ui.modals", out string modals, out _, out _))
            return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(modals);
            foreach (JsonElement modal in document.RootElement.EnumerateArray())
            {
                if (modal.TryGetProperty("hwnd", out JsonElement handle) && handle.ValueKind == JsonValueKind.String &&
                    string.Equals(handle.GetString(), hwnd, StringComparison.OrdinalIgnoreCase))
                    return false;
            }
        }
        catch (JsonException)
        {
            return false;
        }

        return true;
    }

    private static string StatusString(string json, string name)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String)
                return value.GetString() ?? string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }

        return string.Empty;
    }
}

// Anchors can go stale after a cell load, so the refresh must be askable, not only automatic.
[McpServerToolType]
public sealed class ResolveTools
{
    private readonly PluginClient _plugin;

    public ResolveTools(PluginClient plugin) => _plugin = plugin;

    [McpServerTool(Name = "ck.resolve"),
     Description("Ask the in-process plugin to re-read the render window and its camera once, for when " +
                 "the anchors go stale after a cell load. Returns the window and camera addresses, or the " +
                 "reason they did not validate. Requires CKPE plus CKAgent.dll in <game>\\CKPEPlugins\\.")]
    public ResolveResult Resolve()
    {
        if (!BlockedEditor.TryCall(_plugin, "resolve", out string payload, out string code, out string message))
            return new ResolveResult("error", code, message, null);

        return new ResolveResult("ok", "ok", "resolve", payload);
    }
}

// Constraint: only the render window's own cell settles a load, because the Cell View only names its click.
internal static class CellWatch
{
    internal sealed record OpenCell(string? Handle, string? EditorId, string? FormId, bool Readable);

    internal static OpenCell? Read(IPluginCaller plugin)
    {
        if (!BlockedEditor.TryCall(plugin, "cell", out string payload, out _, out _)) return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;

            bool readable = root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("readable", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;

            return new OpenCell(Text(root, "cell"), Text(root, "editorId"), Text(root, "formId"), readable);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Invariant: the wait ends when the requested cell is held or another one replaced it, never on time alone.
    internal static OpenCell? WaitForChange(IPluginCaller plugin, OpenCell? before, string requested, string? matched,
                                            int attempts = 60, int pauseMs = 250)
    {
        for (int attempt = 0; attempt < attempts; attempt++)
        {
            OpenCell? now = Read(plugin);
            if (now != null)
            {
                bool replaced = now.Handle != null && now.Handle != before?.Handle;
                if (replaced || Matches(now, requested, matched)) return now;
            }

            Thread.Sleep(pauseMs);
        }

        return null;
    }

    // Invariant: a name that already matches the loaded cell must not be loaded again, so the wait is never entered.
    internal static bool IsOpen(OpenCell? cell, string requested)
    {
        return cell?.EditorId is string id && string.Equals(id.Trim(), requested.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    internal static bool Matches(OpenCell cell, string requested, string? matched)
    {
        return cell.EditorId is string id &&
            (string.Equals(id, requested, StringComparison.OrdinalIgnoreCase) ||
             (matched != null && string.Equals(id, matched, StringComparison.OrdinalIgnoreCase)));
    }

    internal static string? Matched(string payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            return Text(document.RootElement, "cell");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string Describe(OpenCell? cell)
    {
        if (cell == null) return "no readable cell";
        if (cell.EditorId != null) return cell.EditorId;
        if (cell.FormId != null) return cell.FormId;
        return "an unreadable cell";
    }

    internal static string WithOpened(string payload, string? requested, OpenCell? opened)
    {
        JsonObject node = new();
        if (!string.IsNullOrWhiteSpace(payload) && JsonNode.Parse(payload) is JsonObject parsed) node = parsed;

        if (requested != null) node["requested"] = requested;
        if (opened != null)
        {
            node["opened"] = new JsonObject
            {
                ["editorId"] = opened.EditorId,
                ["formId"] = opened.FormId,
                ["cell"] = opened.Handle,
                ["readable"] = opened.Readable,
            };
        }

        return node.ToJsonString();
    }

    private static string? Text(JsonElement root, string name)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out JsonElement value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;

        string? text = value.GetString();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}
