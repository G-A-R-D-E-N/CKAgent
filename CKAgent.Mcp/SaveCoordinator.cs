using System.Diagnostics;

namespace Garden.CkAgent;

public sealed record SaveOutcome(
    string Status,
    string Code,
    string Message,
    string? Plugin,
    string? PluginPath,
    string? BackupPath,
    long? BeforeBytes,
    long? AfterBytes,
    long ElapsedMs,
    uint? CommandId);

/// Invariant: only a save the editor confirmed to disk counts as delivered, so the decision lives here.
public readonly record struct SaveVerdict(bool Persisted, string Code, string? SavedAtUtc);


/// A save must back the plugin up first and then confirm the file actually changed.
public sealed class SaveCoordinator
{
    private readonly EditorPaths _paths;
    private readonly CommandStore _commands;

    public SaveCoordinator(EditorPaths paths, CommandStore commands)
    {
        _paths = paths;
        _commands = commands;
    }

    // Constraint: a loaded master cannot be written in place, so the editor asks for a target plugin; that dialog
    // is answered here by the caller's chooser, and the file the editor then writes is the one watched.
    public SaveOutcome Save(string plugin, IntPtr mainWindow, int timeoutSeconds, string? saveAs = null,
                            Func<IntPtr, bool>? chooseTarget = null)
    {
        var clock = Stopwatch.StartNew();
        string pluginPath = Path.Combine(_paths.DataDirectory, plugin);
        string written = string.IsNullOrWhiteSpace(saveAs) ? plugin : saveAs!.Trim();
        string watchPath = Path.Combine(_paths.DataDirectory, written);

        if (!File.Exists(pluginPath))
        {
            return Fail("plugin_not_found", $"{plugin} is not in {_paths.DataDirectory}", plugin, pluginPath, clock);
        }

        if (!_commands.TryResolve("File/Save", out CommandEntry command, out string resolveError))
        {
            return Fail("command_unavailable", resolveError, plugin, pluginPath, clock);
        }

        string backup;
        try
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string folder = Path.Combine(_paths.BackupsDirectory, stamp);
            Directory.CreateDirectory(folder);
            backup = Path.Combine(folder, Path.GetFileName(pluginPath));
            File.Copy(pluginPath, backup, overwrite: false);
        }
        catch (Exception exception)
        {
            return Fail("backup_failed", $"refusing to save without a backup: {exception.Message}",
                plugin, pluginPath, clock);
        }

        long beforeBytes = File.Exists(watchPath) ? new FileInfo(watchPath).Length : -1;
        DateTime beforeWrite = File.Exists(watchPath) ? File.GetLastWriteTimeUtc(watchPath) : DateTime.MinValue;

        if (!Win32.IsWindow(mainWindow) || !Win32.PostMessageW(mainWindow, Win32.WmCommand,
                (IntPtr)command.Id, IntPtr.Zero))
        {
            return Fail("post_failed", $"could not post '{command.Path}' to the editor window",
                plugin, pluginPath, clock, backup);
        }

        var deadline = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 600));
        bool answered = false;
        while (clock.Elapsed < deadline)
        {
            Thread.Sleep(200);

            if (!Win32.IsWindow(mainWindow))
            {
                return new SaveOutcome("error", "editor_gone", "the editor closed while saving",
                    plugin, pluginPath, backup, beforeBytes, null, clock.ElapsedMilliseconds, command.Id);
            }

            if (!answered && chooseTarget != null)
            {
                IntPtr dialog = FindDialog(Win32.ProcessId(mainWindow), "Select Target Plugin");
                if (dialog != IntPtr.Zero) answered = chooseTarget(dialog);
            }

            if (!File.Exists(watchPath)) continue;
            var info = new FileInfo(watchPath);
            if (info.Length != beforeBytes || info.LastWriteTimeUtc != beforeWrite)
            {
                return new SaveOutcome("ok", "saved",
                    $"'{command.Path}' ran and {written} changed on disk" +
                    (beforeBytes < 0 ? " (created)" : ""),
                    written, watchPath, backup, beforeBytes, info.Length, clock.ElapsedMilliseconds, command.Id);
            }
        }

        // A modal dialog or a different active file must be reported, not treated as failure.
        return new SaveOutcome("warning", "save_not_observed",
            $"posted '{command.Path}' but {plugin} did not change; the editor may have a modal dialog open " +
            "or a different file may be active",
            plugin, pluginPath, backup, beforeBytes, beforeBytes, clock.ElapsedMilliseconds, command.Id);
    }

    private static IntPtr FindDialog(uint processId, string title)
    {
        IntPtr found = IntPtr.Zero;
        Win32.EnumWindows((hWnd, _) =>
        {
            if (Win32.ProcessId(hWnd) != processId) return true;
            if (!Win32.WindowText(hWnd).Contains(title, StringComparison.OrdinalIgnoreCase)) return true;
            if (!Win32.IsWindowEnabled(hWnd)) return true;
            found = hWnd;
            return false;
        }, IntPtr.Zero);

        return found;
    }

    // Constraint: a backup that was taken is named in the outcome even when the save stopped before posting,
    // because the caller has to know where the untouched copy is.
    private static SaveOutcome Fail(string code, string message, string plugin, string pluginPath, Stopwatch clock,
                              string? backup = null) =>
        new("error", code, message, plugin, pluginPath, backup, null, null, clock.ElapsedMilliseconds, null);
}
