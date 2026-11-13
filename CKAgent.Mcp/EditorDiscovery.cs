using System.Diagnostics;

namespace Garden.CkAgent;

internal static class EditorDiscovery
{
    internal const string MainWindowClass = "Creation Kit";

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

        Win32.EnumWindows((hWnd, _) =>
        {
            if (Win32.ProcessId(hWnd) != processId) return true;
            if (!string.Equals(Win32.ClassName(hWnd), MainWindowClass, StringComparison.Ordinal)) return true;

            found = hWnd;
            return false;
        }, IntPtr.Zero);

        return found;
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
