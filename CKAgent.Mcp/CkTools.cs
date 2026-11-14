using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    string StateDirectory,
    string? BlockedBy,
    IReadOnlyList<string>? ModalButtons,
    string? DismissBy);

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
                 "what will happen.")]
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
                false, _paths.StateDirectory, null, null, null);
        }

        IntPtr window = EditorDiscovery.FindMainWindow((uint)process.Id);
        bool pipe = EditorDiscovery.IsPluginPipeAvailable();

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

        string code = window != IntPtr.Zero ? "ok" : "window_not_ready";
        string message = window != IntPtr.Zero
            ? "editor is running and its command menu is reachable"
            : "editor is running but has not created its main window yet";

        return new EditorStatusResult("ok", code, message, true, process.Id,
            window != IntPtr.Zero ? Win32.WindowText(window) : null,
            window != IntPtr.Zero ? window.ToInt64() : null,
            _paths.CreationKitPath, _paths.GameDirectory, _paths.EditorVersion,
            document?.Commands.Count ?? 0, document?.Build, document?.CapturedUtc,
            pipe, _paths.StateDirectory, blockedBy, modalButtons, dismissBy);
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
                 "EditorID, or an 8-digit form id; distance is chosen from the reference's own bounds (2.4 " +
                 "bounding-sphere radii back, 30 degrees above the box centre) and may be overridden, and yaw " +
                 "picks which side to stand on. Returns the matched object, its position, the bounds it was " +
                 "framed from and whether they came from the reference, the camera pose and whether the " +
                 "object sits ahead along the view row, plus the cell the render window actually holds, so a " +
                 "load that landed elsewhere is visible in the same reply. Needs a loaded cell and CKPE with " +
                 "CKAgent.dll installed.")]
    public CameraResult Frame(string reference, string? cell = null, double? distance = null, double? yaw = null)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return new CameraResult("error", "invalid_argument",
                "reference is required: a reference EditorID, a base object EditorID, or a form id", null);
        }

        string? requestedCell = null;

        if (!string.IsNullOrWhiteSpace(cell))
        {
            requestedCell = cell.Trim();
            CellWatch.OpenCell? before = CellWatch.Read(_plugin);

            // Constraint: an already loaded cell would satisfy the wait for another one, so the handle must change.
            if (!BlockedEditor.TryCall(_plugin, $"ui.cellload {requestedCell}", out string opened, out string openCode, out string openMessage))
                return new CameraResult("error", openCode, openMessage, opened);

            string? matched = CellWatch.Matched(opened);
            CellWatch.OpenCell? loaded = CellWatch.WaitForChange(_plugin, before, requestedCell, matched);

            if (loaded == null)
            {
                return new CameraResult("error", "cell_not_loaded",
                    $"the Cell View accepted {requestedCell}, but the render window still holds " +
                    $"{CellWatch.Describe(before)} after 15 s",
                    CellWatch.WithOpened(opened, requestedCell, before));
            }

        }

        // Constraint: a yaw needs the distance word in front of it, and 0 is how it asks for the bounds fit.
        if (yaw != null && distance == null) distance = 0;

        string command = $"frame {reference.Trim()}";
        if (distance != null) command += " " + PluginClient.Format(distance.Value);
        if (yaw != null) command += " " + PluginClient.Format(yaw.Value);

        if (!BlockedEditor.TryCall(_plugin, command, out string payload, out string code, out string message))
            return new CameraResult("error", code, message, null);

        CellWatch.OpenCell? framed = CellWatch.Read(_plugin);
        string openedIn = framed?.EditorId ?? framed?.FormId ?? "an unreadable cell";

        return new CameraResult("ok", "ok", $"{command} in {openedIn}",
            CellWatch.WithOpened(payload, requestedCell, framed));
    }

    [McpServerTool(Name = "ck.aim"),
     Description("Rotate the camera until a named reference in the loaded cell is straight ahead, keeping " +
                 "the camera where it stands so the object is seen from the view already chosen. reference " +
                 "matches a reference EditorID, its base object EditorID, or an 8-digit form id, and may be a " +
                 "comma-joined list aimed in order from the same camera position, which scans a small area " +
                 "without the caller computing poses; optional " +
                 "distance backs the camera away to that standoff first and is refused with a list, because it " +
                 "moves the camera; the tilt is computed from where " +
                 "that leaves the camera. A target straight above or below it keeps the current heading and " +
                 "tilts to the pole. Reports the yaw and pitch it wrote and the achieved aim, measured " +
                 "against the scene camera's own view row rather than the matrix just written, so an object " +
                 "above or below the camera is centred rather than reported as a miss. Needs a loaded cell " +
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

    [McpServerTool(Name = "ck.camera"),
     Description("Drive the render window camera through the CKAgent plugin. mode=Get returns the " +
                 "scene camera's position, rotation matrix and scale; mode=Set places it at x/y/z with " +
                 "optional yaw/pitch (radians, +pitch tilts up); mode=Move steps by x/y/z in the camera's " +
                 "own axes, so +y is its right and not world +Y; mode=Lookat aims at the world point " +
                 "x/y/z, calling the editor's LookAt and then writing the pose it computes when that " +
                 "does not verify, and reports which path aimed it as via plus the achieved aim; " +
                 "mode=Orbit circles a target at the given angle (radians, angle 0 looks along +X), optionally " +
                 "tilted, where the target is either the world point x/y/z or a named reference whose bounds " +
                 "centre is circled, and the distance defaults to the same bounds fit ck.frame uses; it sets " +
                 "the editor's own pivot to that point so its own " +
                 "orbit and zoom continue from there, and reports the reference it used, the pivot it read back plus the radius " +
                 "and aim it achieved. mode=Rotate turns the camera by the step x/y/z in radians through " +
                 "the editor's own RotateBy, and reports the yaw and pitch it achieved with their deltas, so " +
                 "an orbit step can be compared against the editor's rotation directly; a step that leaves " +
                 "the view unchanged fails as rotate_noop. mode=Lens reads the field of view from the scene camera's own " +
                 "frustum and reports it in degrees along with the near and far planes, the zoom measured " +
                 "against the render window's own lens, the raw frustum and the view mode; passing fov " +
                 "sets a vertical field of view in degrees, and passing zoom scales the field of view by a " +
                 "factor, where above 1 zooms in; both report what was achieved rather than what was asked " +
                 "for, and the editor's own Zoom call is not used because it leaves the frustum unchanged. " +
                 "Every mode returns the resulting position " +
                 "and matrix. Needs a loaded cell and reports camera_transform_unverified when the scene " +
                 "camera has no usable transform yet. " +
                 "Requires CKPE plus CKAgent.dll in <game>\\CKPEPlugins\\.")]
    public CameraResult Camera(string mode = "get", double? x = null, double? y = null, double? z = null,
                               double? yaw = null, double? pitch = null, double? distance = null,
                               double? fov = null, double? zoom = null, string? reference = null)
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

            case "rotate":
            {
                if (x == null || y == null || z == null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "mode=Rotate needs x, y and z: the step in radians around each of the camera's own axes", null);
                }

                if (yaw != null || pitch != null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "yaw and pitch belong to mode=Set; mode=Rotate takes the step in x, y and z", null);
                }

                command = $"camera rotate {PluginClient.Format(x.Value)} {PluginClient.Format(y.Value)} {PluginClient.Format(z.Value)}";
                break;
            }

            case "lens":
            {
                if (fov != null && zoom != null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "fov and zoom change the same lens; pass one", null);
                }

                command = "camera lens";
                if (fov != null) command += " fov " + PluginClient.Format(fov.Value);
                else if (zoom != null) command += " zoom " + PluginClient.Format(zoom.Value);
                break;
            }

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

            case "orbit":
            {
                if (yaw == null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "mode=Orbit needs yaw: the angle around the target, in radians", null);
                }

                if (reference != null)
                {
                    if (x != null || y != null || z != null)
                    {
                        return new CameraResult("error", "invalid_argument",
                            "mode=Orbit takes a reference or x/y/z, not both", null);
                    }

                    if (distance != null && !(distance > 0))
                    {
                        return new CameraResult("error", "invalid_argument",
                            "mode=Orbit needs a positive distance when it is given", null);
                    }

                    // Constraint: the plugin needs the radius word before the angle, and 0 asks for the bounds fit.
                    string radius = distance != null ? PluginClient.Format(distance.Value) : "0";
                    command = $"orbit {reference.Trim()} {radius} {PluginClient.Format(yaw.Value)}" +
                              (pitch != null ? $" {PluginClient.Format(pitch.Value)}" : string.Empty);
                    break;
                }

                if (x == null || y == null || z == null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "mode=Orbit needs a reference, or x, y and z: the point to circle", null);
                }

                if (distance == null || !(distance > 0))
                {
                    return new CameraResult("error", "invalid_argument",
                        "mode=Orbit needs a positive distance: the radius to keep", null);
                }

                command = $"orbit {PluginClient.Format(x.Value)} {PluginClient.Format(y.Value)} {PluginClient.Format(z.Value)} " +
                          $"{PluginClient.Format(distance.Value)} {PluginClient.Format(yaw.Value)}" +
                          (pitch != null ? $" {PluginClient.Format(pitch.Value)}" : string.Empty);
                break;
            }

            default:
                return new CameraResult("error", "invalid_argument",
                    "mode must be get, set, move, lookat, orbit, rotate or lens", null);
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
                 "it in the Cell View does, and report the cell the render window actually holds after the " +
                 "load, so a wrong-cell load is caught in the same reply: the opened EditorID and form id " +
                 "accompany the matched row, and cell_not_loaded names the cell that stayed when the try did " +
                 "nothing. Refuses a name that cannot travel in one protocol line.")]
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

    internal static OpenCell? Read(PluginClient plugin)
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
    internal static OpenCell? WaitForChange(PluginClient plugin, OpenCell? before, string requested, string? matched,
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
