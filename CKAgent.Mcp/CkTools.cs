using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;

namespace Garden.CkAgent;

public sealed record EditorStatusResult(
    string Status,
    string Code,
    string Message,
    bool Running,
    int? Pid,
    string? MainWindowTitle,
    long? MainWindowHandle,
    string CreationKitPath,
    string GameDirectory,
    string? EditorVersion,
    int ManifestCommands,
    string? ManifestBuild,
    string? ManifestCapturedUtc,
    bool PluginPipeAvailable,
    string StateDirectory);

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
                 "command manifest age, and whether the in-process CKAgent plugin is listening.")]
    public EditorStatusResult Status()
    {
        var document = _commands.Load();
        var process = EditorDiscovery.FindEditor(_paths.CreationKitPath);

        if (process == null)
        {
            return new EditorStatusResult("ok", "editor_not_running",
                "no CreationKit.exe for this install is running", false, null, null, null,
                _paths.CreationKitPath, _paths.GameDirectory, _paths.EditorVersion,
                document?.Commands.Count ?? 0, document?.Build, document?.CapturedUtc,
                false, _paths.StateDirectory);
        }

        IntPtr window = EditorDiscovery.FindMainWindow((uint)process.Id);
        bool pipe = EditorDiscovery.IsPluginPipeAvailable();

        string code = window != IntPtr.Zero ? "ok" : "window_not_ready";
        string message = window != IntPtr.Zero
            ? "editor is running and its command menu is reachable"
            : "editor is running but has not created its main window yet";

        return new EditorStatusResult("ok", code, message, true, process.Id,
            window != IntPtr.Zero ? Win32.WindowText(window) : null,
            window != IntPtr.Zero ? window.ToInt64() : null,
            _paths.CreationKitPath, _paths.GameDirectory, _paths.EditorVersion,
            document?.Commands.Count ?? 0, document?.Build, document?.CapturedUtc,
            pipe, _paths.StateDirectory);
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
public sealed record CameraResult(
    string Status,
    string Code,
    string Message,
    string? Payload);

[McpServerToolType]
public sealed class CameraTools
{
    private readonly PluginClient _plugin;

    public CameraTools(PluginClient plugin) => _plugin = plugin;

    [McpServerTool(Name = "ck.frame"),
     Description("Load a cell and put the camera in front of an object, so a reference can be inspected " +
                 "without navigating by hand. cell is an EditorID or display name (skipped when omitted, so " +
                 "an already loaded cell is used); reference matches a reference EditorID, its base object " +
                 "EditorID, or an 8-digit form id; distance defaults to 256 and yaw picks which side to " +
                 "stand on. Returns the matched object, its position, the camera pose and whether the object " +
                 "sits ahead along the view row. Needs a loaded cell and CKPE with CKAgent.dll installed.")]
    public CameraResult Frame(string reference, string? cell = null, double? distance = null, double? yaw = null)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return new CameraResult("error", "invalid_argument",
                "reference is required: a reference EditorID, a base object EditorID, or a form id", null);
        }

        if (!string.IsNullOrWhiteSpace(cell))
        {
            // Constraint: an already loaded cell would satisfy the wait for another one, so the handle must change.
            string? before = CurrentCellHandle();

            if (!BlockedEditor.TryCall(_plugin, $"ui.cellload {cell.Trim()}", out string opened, out string openCode, out string openMessage))
                return new CameraResult("error", openCode, openMessage, opened);

            bool loaded = false;
            for (int attempt = 0; attempt < 60 && !loaded; attempt++)
            {
                string? now = CurrentCellHandle();
                loaded = now != null && now != before;
                if (!loaded) Thread.Sleep(250);
            }

            if (!loaded)
            {
                return new CameraResult("error", "cell_not_loaded",
                    $"the editor did not report a new cell for {cell} within 15 s", null);
            }
        }

        // A yaw without a distance must still frame at the default standoff rather than at zero.
        if (yaw != null && distance == null) distance = 256;

        string command = $"frame {reference.Trim()}";
        if (distance != null) command += " " + PluginClient.Format(distance.Value);
        if (yaw != null) command += " " + PluginClient.Format(yaw.Value);

        if (!BlockedEditor.TryCall(_plugin, command, out string payload, out string code, out string message))
            return new CameraResult("error", code, message, null);

        return new CameraResult("ok", "ok", command, payload);
    }

    [McpServerTool(Name = "ck.aim"),
     Description("Rotate the camera until a named reference in the loaded cell is straight ahead, keeping " +
                 "the camera where it stands so the object is seen from the view already chosen. reference " +
                 "matches a reference EditorID, its base object EditorID, or an 8-digit form id; optional " +
                 "distance backs the camera away to that standoff first. Reports the yaw it wrote, the " +
                 "achieved aim and the vertical offset: pitch cannot be set, so an object above or below " +
                 "the camera comes back with aligned=false and a non-zero vertical. Needs a loaded cell " +
                 "and CKPE with CKAgent.dll installed.")]
    public CameraResult Aim(string reference, double? distance = null)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return new CameraResult("error", "invalid_argument",
                "reference is required: a reference EditorID, a base object EditorID, or a form id", null);
        }

        string command = $"aim {reference.Trim()}";
        if (distance != null) command += " " + PluginClient.Format(distance.Value);

        if (!BlockedEditor.TryCall(_plugin, command, out string payload, out string code, out string message))
            return new CameraResult("error", code, message, null);

        return new CameraResult("ok", "ok", command, payload);
    }

    private string? CurrentCellHandle()
    {
        if (!_plugin.TryCall("cell", out string payload, out _, out _)) return null;

        const string marker = "\"cell\":\"";
        int at = payload.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return null;
        at += marker.Length;
        int end = payload.IndexOf('"', at);
        return end < 0 ? null : payload.Substring(at, end - at);
    }

    [McpServerTool(Name = "ck.camera"),
     Description("Drive the render window camera through the CKAgent plugin. mode=Get returns the " +
                 "scene camera's position, rotation matrix and scale; mode=Set places it at x/y/z with " +
                 "optional yaw/pitch (radians, +pitch tilts up); mode=Move steps by x/y/z in the camera's " +
                 "own axes, so +y is its right and not world +Y; mode=Lookat forwards x/y/z to the " +
                 "editor's LookAt, which changes the orientation but was measured not to point at that " +
                 "point, so prefer mode=Set for a known view. Every mode returns the resulting position " +
                 "and matrix. Needs a loaded cell and reports camera_transform_unverified when the scene " +
                 "camera has no usable transform yet. " +
                 "Requires CKPE plus CKAgent.dll in <game>\\CKPEPlugins\\.")]
    public CameraResult Camera(string mode = "get", double? x = null, double? y = null, double? z = null,
                               double? yaw = null, double? pitch = null)
    {
        string command;

        switch (mode.Trim().ToLowerInvariant())
        {
            case "get":
                command = "camera get";
                break;

            case "set":
                if (x == null || y == null || z == null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "mode=Set needs x, y and z", null);
                }

                if ((yaw == null) != (pitch == null))
                {
                    return new CameraResult("error", "invalid_argument",
                        "yaw and pitch must be given together", null);
                }

                command = yaw != null
                    ? $"camera set {PluginClient.Format(x.Value)} {PluginClient.Format(y.Value)} {PluginClient.Format(z.Value)} " +
                      $"{PluginClient.Format(yaw.Value)} {PluginClient.Format(pitch.Value)}"
                    : $"camera set {PluginClient.Format(x.Value)} {PluginClient.Format(y.Value)} {PluginClient.Format(z.Value)}";
                break;

            case "move":
            {
                if (x == null || y == null || z == null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "mode=Move needs x, y and z: a step along the camera's own axes", null);
                }

                if (yaw != null || pitch != null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "yaw and pitch belong to mode=Set; mode=Move only takes a local step", null);
                }

                command = $"camera move {PluginClient.Format(x.Value)} {PluginClient.Format(y.Value)} {PluginClient.Format(z.Value)}";
                break;
            }

            case "lookat":
            {
                if (x == null || y == null || z == null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "mode=Lookat needs x, y and z: the world point to aim at", null);
                }

                if (yaw != null || pitch != null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "yaw and pitch belong to mode=Set; mode=Lookat derives the rotation from the target", null);
                }

                command = $"camera lookat {PluginClient.Format(x.Value)} {PluginClient.Format(y.Value)} {PluginClient.Format(z.Value)}";
                break;
            }

            default:
                return new CameraResult("error", "invalid_argument", "mode must be get or set", null);
        }

        if (!BlockedEditor.TryCall(_plugin, command, out string payload, out string code, out string message))
            return new CameraResult("error", code, message, null);

        return new CameraResult("ok", "ok", command, payload);
    }
}

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
                 "optionally filtered by EditorID or display name. Needs a loaded master file and the " +
                 "CKAgent plugin installed.")]
    public CellResult Cells(string? filter = null)
    {
        string command = string.IsNullOrWhiteSpace(filter) ? "ui.cells" : $"ui.cells {filter!.Trim()}";
        if (!BlockedEditor.TryCall(_plugin, command, out string payload, out string code, out string message))
            return new CellResult("error", code, message, null);

        return new CellResult("ok", "ok", command, payload);
    }

    [McpServerTool(Name = "ck.cell_load"),
     Description("Open a cell in the render window by EditorID or display name, exactly as double-clicking " +
                 "it in the Cell View does, and report the item that was opened. Refuses a name that cannot " +
                 "travel in one protocol line.")]
    public CellResult CellLoad(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new CellResult("error", "invalid_argument", "name is required", null);

        string trimmed = name.Trim();
        if (trimmed.Length > 200 || trimmed.Contains('\n') || trimmed.Contains('\r') || trimmed.Contains('\t'))
            return new CellResult("error", "invalid_argument",
                "name must be a single line of at most 200 characters", null);

        if (!BlockedEditor.TryCall(_plugin, $"ui.cellload {trimmed}", out string payload, out string code, out string message))
            return new CellResult("error", code, message, null);

        return new CellResult("ok", "ok", $"ui.cellload {trimmed}", payload);
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
        "ui.command", "ui.menu", "ui.click", "ui.modals", "ui.cells", "ui.cellload", "ui.dataload",
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
