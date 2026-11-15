using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

public static class UiaWindows
{
    delegate bool EnumProc(IntPtr h, IntPtr p);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc proc, IntPtr param);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowTextW(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);

    public static bool Foreground(long handle)
    {
        return SetForegroundWindow(new IntPtr(handle));
    }

    // Every visible top-level window of the process whose title contains the wanted text.
    public static List<long> Find(uint wanted, string title)
    {
        var found = new List<long>();
        EnumWindows((h, p) =>
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            if (pid == wanted && IsWindowVisible(h))
            {
                var text = new StringBuilder(512);
                GetWindowTextW(h, text, 511);
                if (text.ToString().IndexOf(title, StringComparison.OrdinalIgnoreCase) >= 0)
                    found.Add(h.ToInt64());
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
