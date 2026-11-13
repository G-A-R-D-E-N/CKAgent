using System.Runtime.InteropServices;
using System.Text;

namespace Garden.CkAgent;

/// The main window must own a real HMENU: command ids are read from it.
internal static class Win32
{
    internal const uint WmCommand = 0x0111;
    internal const uint MfByPosition = 0x0400;
    internal const uint MiimId = 0x0002;
    internal const uint MiimSubMenu = 0x0004;

    internal delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    internal static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int count);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetMenu(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern int GetMenuItemCount(IntPtr menu);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern int GetMenuStringW(IntPtr menu, uint item, StringBuilder text, int count, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern bool GetMenuItemInfoW(IntPtr menu, uint item, bool byPosition, ref MenuItemInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessageW(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowEnabled(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MenuItemInfo
    {
        public uint Size;
        public uint Mask;
        public uint Type;
        public uint State;
        public uint Id;
        public IntPtr SubMenu;
        public IntPtr CheckedBitmap;
        public IntPtr UncheckedBitmap;
        public IntPtr ItemData;
        public IntPtr TypeData;
        public uint TextLength;
        public IntPtr ItemBitmap;
    }

    internal static string ClassName(IntPtr hWnd)
    {
        var text = new StringBuilder(256);
        GetClassNameW(hWnd, text, text.Capacity);
        return text.ToString();
    }

    internal static string WindowText(IntPtr hWnd)
    {
        var text = new StringBuilder(512);
        GetWindowTextW(hWnd, text, text.Capacity);
        return text.ToString();
    }

    internal static uint ProcessId(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out uint pid);
        return pid;
    }
}
