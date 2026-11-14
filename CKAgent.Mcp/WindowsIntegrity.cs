using System.Runtime.InteropServices;
using System.Text;

namespace Garden.CkAgent;

// Constraint: a refused pipe must name this process's own integrity level, because that decides the fix.
internal static class WindowsIntegrity
{
    private const uint TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;
    private const int TokenElevation = 20;
    private const int TokenPrivileges = 3;
    private const uint SePrivilegeEnabled = 0x00000002;

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public IntPtr Sid;
        public int Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LuidAndAttributes
    {
        public Luid Id;
        public uint Attributes;
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

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool LookupPrivilegeName(string? system, ref Luid luid, StringBuilder name, ref int length);

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

    /// Invariant: elevation is the token's own flag, so it is asked for rather than inferred from the level.
    internal static bool IsElevated()
    {
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
                return false;

            buffer = Marshal.AllocHGlobal(sizeof(int));
            if (!GetTokenInformation(token, TokenElevation, buffer, sizeof(int), out _))
                return false;

            return Marshal.ReadInt32(buffer) != 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    /// Constraint: only enabled privileges are reported, because a held-but-disabled one cannot open anything.
    internal static IReadOnlyList<string> EnabledPrivileges()
    {
        IntPtr token = IntPtr.Zero;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token))
                return Array.Empty<string>();

            GetTokenInformation(token, TokenPrivileges, IntPtr.Zero, 0, out int needed);
            if (needed <= 0)
                return Array.Empty<string>();

            buffer = Marshal.AllocHGlobal(needed);
            if (!GetTokenInformation(token, TokenPrivileges, buffer, needed, out _))
                return Array.Empty<string>();

            // Invariant: the buffer is a count followed by fixed-size LUID-and-attribute entries.
            int count = Marshal.ReadInt32(buffer);
            int entry = Marshal.SizeOf<LuidAndAttributes>();
            var names = new List<string>();

            for (int i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<LuidAndAttributes>(
                    IntPtr.Add(buffer, sizeof(int) + (i * entry)));

                if ((item.Attributes & SePrivilegeEnabled) == 0)
                    continue;

                string? name = PrivilegeName(item.Id);
                if (name != null)
                    names.Add(name);
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (token != IntPtr.Zero) CloseHandle(token);
        }
    }

    private static string? PrivilegeName(Luid luid)
    {
        int size = 128;
        var name = new StringBuilder(size);
        return LookupPrivilegeName(null, ref luid, name, ref size) ? name.ToString() : null;
    }
}
