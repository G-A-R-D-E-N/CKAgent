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

    public SaveOutcome Save(string plugin, IntPtr mainWindow, int timeoutSeconds)
    {
        var clock = Stopwatch.StartNew();
        string pluginPath = Path.Combine(_paths.DataDirectory, plugin);

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

        long beforeBytes = new FileInfo(pluginPath).Length;
        DateTime beforeWrite = File.GetLastWriteTimeUtc(pluginPath);

        if (!Win32.IsWindow(mainWindow) || !Win32.PostMessageW(mainWindow, Win32.WmCommand,
                (IntPtr)command.Id, IntPtr.Zero))
        {
            return Fail("post_failed", $"could not post '{command.Path}' to the editor window",
                plugin, pluginPath, clock);
        }

        var deadline = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 600));
        while (clock.Elapsed < deadline)
        {
            Thread.Sleep(200);

            if (!Win32.IsWindow(mainWindow))
            {
                return new SaveOutcome("error", "editor_gone", "the editor closed while saving",
                    plugin, pluginPath, backup, beforeBytes, null, clock.ElapsedMilliseconds, command.Id);
            }

            var info = new FileInfo(pluginPath);
            if (!info.Exists) break;
            if (info.Length != beforeBytes || info.LastWriteTimeUtc != beforeWrite)
            {
                return new SaveOutcome("ok", "saved", $"'{command.Path}' ran and {plugin} changed on disk",
                    plugin, pluginPath, backup, beforeBytes, info.Length, clock.ElapsedMilliseconds, command.Id);
            }
        }

        // A modal dialog or a different active file must be reported, not treated as failure.
        return new SaveOutcome("warning", "save_not_observed",
            $"posted '{command.Path}' but {plugin} did not change; the editor may have a modal dialog open " +
            "or a different file may be active",
            plugin, pluginPath, backup, beforeBytes, beforeBytes, clock.ElapsedMilliseconds, command.Id);
    }

    private static SaveOutcome Fail(string code, string message, string plugin, string pluginPath, Stopwatch clock) =>
        new("error", code, message, plugin, pluginPath, null, null, null, clock.ElapsedMilliseconds, null);
}
