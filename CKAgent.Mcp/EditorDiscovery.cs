using System.Diagnostics;

namespace Garden.CkAgent;

internal static class EditorDiscovery
{
    // Invariant: the frame's class carries the build ("Creation Kit Fallout 4 [v1.11.246.0]"), so the class is not addressable.
    internal const string MainWindowClassPrefix = "Creation Kit";

    // It must be this install's executable; ids posted to another install edit the wrong data.
    internal static Process? FindEditor(string creationKitPath)
    {
        Process? fallback = null;

        foreach (var process in Process.GetProcessesByName("CreationKit"))
        {
            string? modulePath = null;
            try { modulePath = process.MainModule?.FileName; }
            catch (Exception) { /* a 32-bit or protected process: cannot inspect it */ }

            if (modulePath != null &&
                string.Equals(Path.GetFullPath(modulePath), Path.GetFullPath(creationKitPath),
                              StringComparison.OrdinalIgnoreCase))
            {
                return process;
            }

            fallback ??= process;
        }

        // A single unreadable editor must still count as ours.
        return Process.GetProcessesByName("CreationKit").Length == 1 ? fallback : null;
    }

    internal static IntPtr FindMainWindow(uint processId)
    {
        IntPtr found = IntPtr.Zero;
        IntPtr fallback = IntPtr.Zero;

        // Invariant: the frame is found by owning a menu bar, the way the plugin does it, and not by class.
        Win32.EnumWindows((hWnd, _) =>
        {
            if (Win32.ProcessId(hWnd) != processId) return true;
            if (Win32.GetMenu(hWnd) == IntPtr.Zero) return true;

            if (Win32.IsWindowVisible(hWnd) && Win32.IsWindowEnabled(hWnd))
            {
                found = hWnd;
                return false;
            }

            if (fallback == IntPtr.Zero && Win32.ClassName(hWnd).StartsWith(MainWindowClassPrefix, StringComparison.Ordinal))
                fallback = hWnd;

            return true;
        }, IntPtr.Zero);

        return found != IntPtr.Zero ? found : fallback;
    }

    // Invariant: a running editor this host cannot see is not a missing editor, so the reply must say which it is.
    internal static (string Code, string Message) DescribeStatus(bool processFound, bool hasWindow, bool windowVisible)
    {
        if (!processFound)
        {
            return hasWindow
                ? ("editor_invisible",
                   "CreationKit is running, but this host cannot open its process or enumerate it, so it was found " +
                   "by its frame window instead: the editor sits at another integrity level or session, and window " +
                   "messages are blocked between levels, so the plugin pipe is the way in")
                : ("editor_not_running",
                   "no CreationKit.exe for this install is running, and no frame window was found");
        }

        if (!hasWindow)
        {
            return ("window_not_ready", "editor is running but has not created its main window yet");
        }

        if (!windowVisible)
        {
            return ("editor_invisible",
                "editor is running and its main window exists, but the window is not visible, so menu and " +
                "control calls have nothing to reach until it is shown; the plugin pipe answers regardless");
        }

        return ("ok", "editor is running and its command menu is reachable");
    }

    // Constraint: a low-integrity host is handed no editor process, so the frame window is the only evidence there is.
    internal static IntPtr FindFrameWindow(out uint processId)
    {
        IntPtr found = IntPtr.Zero;
        uint owner = 0;

        // Invariant: the frame is the window that owns a menu and carries the build in its class, as the plugin finds it.
        Win32.EnumWindows((hWnd, _) =>
        {
            if (!Win32.ClassName(hWnd).StartsWith(MainWindowClassPrefix, StringComparison.Ordinal)) return true;
            if (Win32.GetMenu(hWnd) == IntPtr.Zero) return true;

            found = hWnd;
            owner = Win32.ProcessId(hWnd);
            return false;
        }, IntPtr.Zero);

        processId = owner;
        return found;
    }

    // Constraint: the probe must walk the same candidates as a call, or a status can name a pipe no call reaches.
    internal static string? ProbePipe(bool allowLow)
    {
        foreach (string name in PluginPipes.Candidates(allowLow))
        {
            try
            {
                using var client = new System.IO.Pipes.NamedPipeClientStream(
                    ".", name, System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.PipeOptions.Asynchronous);
                client.Connect(100);
                return name;
            }
            catch (Exception)
            {
            }
        }

        return null;
    }
}
