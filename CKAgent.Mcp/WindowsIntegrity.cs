using System.Runtime.InteropServices;

namespace Garden.CkAgent;

// Constraint: a refused pipe must name this process's own integrity level, because that decides the fix.
internal static class WindowsIntegrity
{
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public int Attributes;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length,
        out int needed);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthority(IntPtr sid, uint index);

    [DllImport("advapi32.dll")]
    private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// Invariant: the level comes from the current token, and null means Windows would not report it.
    public static string? LevelName()
    {
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
                return null;

            GetTokenInformation(token, TokenIntegrityLevel, IntPtr.Zero, 0, out int needed);
            if (needed <= 0)
                return null;

            buffer = Marshal.AllocHGlobal(needed);
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, needed, out _))
                return null;

            SidAndAttributes label = Marshal.PtrToStructure<SidAndAttributes>(buffer);
            if (label.Sid == IntPtr.Zero)
                return null;

            // Invariant: the level is the last sub-authority of the label SID, indexed by its own count.
            int count = Marshal.ReadByte(GetSidSubAuthorityCount(label.Sid));
            if (count <= 0)
                return null;

            int rid = Marshal.ReadInt32(GetSidSubAuthority(label.Sid, (uint)(count - 1)));
            return rid switch
            {
                < 0x1000 => "untrusted",
                < 0x2000 => "low",
                < 0x3000 => "medium",
                < 0x4000 => "high",
                _ => "system",
            };
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }
}
