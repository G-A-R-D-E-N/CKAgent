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

    internal static bool IsPluginPipeAvailable()
    {
        try
        {
            using var client = new System.IO.Pipes.NamedPipeClientStream(
                ".", "ckagent", System.IO.Pipes.PipeDirection.InOut,
                System.IO.Pipes.PipeOptions.Asynchronous);
            client.Connect(100);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
