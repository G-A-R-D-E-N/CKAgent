using System.IO;
using System.IO.Compression;
using System.Text;

namespace Garden.CkAgent;

/// One NAVM record as it sits in a saved plugin, with the geometry counts the Creation Kit wrote into it.
public sealed record NavmeshRecord(
    uint CellFormId,
    uint NavmFormId,
    bool Compressed,
    int Vertices,
    int Triangles,
    string? Error);

/// What one saved plugin holds, read without the editor: every NAVM record, keyed by the cell that owns it.
public sealed record NavmeshFileReport(
    string Path,
    long Bytes,
    int Records,
    IReadOnlyList<NavmeshRecord> Navmeshes,
    string? Error);

/// The counts the editor just produced for the loaded cell, which the saved file has to match.
public sealed record NavmeshExpected(uint CellFormId, int Forms, int Vertices, int Triangles);

/// What the saved plugin turned out to carry for that cell.
public sealed record NavmeshReadback(int Forms, int Vertices, int Triangles, IReadOnlyList<uint> NavmFormIds);

/// Invariant: a mesh is delivered only when the plugin file on disk holds the same geometry the editor just built.
public sealed record NavmeshPersistence(
    bool Persisted,
    string Code,
    string Message,
    string? PluginPath,
    long? Bytes,
    NavmeshExpected? Expected,
    NavmeshReadback? ReadBack)
{
    public static NavmeshPersistence Failed(string code, string message, string? path = null, long? bytes = null) =>
        new(false, code, message, path, bytes, null, null);
}

/// Regression: the save chain used to stop at "the file's bytes changed", which a plugin written with a stale or
/// empty cell also satisfies. The read-back here is the step that makes a save mean the navmesh reached disk.
public static class NavmeshPersist
{
    private const string NavmSignature = "NAVM";
    private const string CellSignature = "CELL";
    private const string GroupSignature = "GRUP";
    private const string GeometrySignature = "NVNM";
    private const uint CompressedFlag = 0x00040000;
    private const uint PathingCellCrc = 0xA5E9A03C;
    private const int RecordHeaderSize = 24;
    private const int GroupHeaderSize = 24;
    private const int GroupTypeOffset = 12;
    private const int MaxVertices = 200000;

    private static readonly string[] PluginExtensions = { ".esp", ".esm", ".esl" };

    /// Constraint: a save is not observable without a before-image, so the tool snapshots Data first and watches it.
    public static IReadOnlyDictionary<string, (long Length, DateTime Modified)> Snapshot(string dataDirectory)
    {
        var seen = new Dictionary<string, (long Length, DateTime Modified)>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(dataDirectory)) return seen;

        foreach (string path in Directory.EnumerateFiles(dataDirectory))
        {
            if (!IsPlugin(path)) continue;

            var info = new FileInfo(path);
            seen[path] = (info.Length, info.LastWriteTimeUtc);
        }

        return seen;
    }

    /// The file the editor wrote is whichever plugin changed after the save was posted; nothing else can tell us.
    public static string? WaitForChange(IReadOnlyDictionary<string, (long Length, DateTime Modified)> before,
                                        string dataDirectory, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + Math.Max(timeoutMs, 0);

        while (true)
        {
            if (Directory.Exists(dataDirectory))
            {
                foreach (string path in Directory.EnumerateFiles(dataDirectory))
                {
                    if (!IsPlugin(path)) continue;

                    var info = new FileInfo(path);
                    var now = (Length: info.Length, Modified: info.LastWriteTimeUtc);

                    if (!before.TryGetValue(path, out var was)) return path;
                    if (was.Length != now.Length || was.Modified != now.Modified) return path;
                }
            }

            if (Environment.TickCount64 >= deadline) return null;
            Thread.Sleep(250);
        }
    }

    /// Constraint: the verdict is computed from the file, not from the editor, so it holds after the editor exits.
    public static NavmeshPersistence Verify(string pluginPath, NavmeshExpected expected)
    {
        if (!File.Exists(pluginPath))
            return NavmeshPersistence.Failed("readback_unavailable", $"{pluginPath} is not on disk, so nothing was written",
                pluginPath);

        long bytes;
        try
        {
            bytes = new FileInfo(pluginPath).Length;
        }
        catch (IOException exception)
        {
            return NavmeshPersistence.Failed("readback_unavailable", exception.Message, pluginPath);
        }

        NavmeshFileReport report;
        try
        {
            report = Read(pluginPath);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or EndOfStreamException)
        {
            return NavmeshPersistence.Failed("readback_unreadable", exception.Message, pluginPath, bytes);
        }

        if (report.Error is not null)
            return NavmeshPersistence.Failed("readback_unreadable", report.Error, pluginPath, bytes);

        List<NavmeshRecord> owned = report.Navmeshes.Where(mesh => mesh.CellFormId == expected.CellFormId).ToList();
        if (owned.Count == 0)
        {
            return new NavmeshPersistence(false, "cell_missing",
                $"{Path.GetFileName(pluginPath)} was written with {report.Records} NAVM record(s), none of cell " +
                $"0x{expected.CellFormId:X8}",
                pluginPath, bytes, expected, null);
        }

        string? unreadable = owned.FirstOrDefault(mesh => mesh.Error is not null)?.Error;
        if (unreadable is not null)
        {
            return new NavmeshPersistence(false, "readback_unreadable",
                $"cell 0x{expected.CellFormId:X8} is in {Path.GetFileName(pluginPath)}, but {unreadable}",
                pluginPath, bytes, expected, null);
        }

        var readBack = new NavmeshReadback(owned.Count,
            owned.Sum(mesh => mesh.Vertices),
            owned.Sum(mesh => mesh.Triangles),
            owned.Select(mesh => mesh.NavmFormId).ToList());

        bool same = readBack.Forms == expected.Forms &&
                    readBack.Vertices == expected.Vertices &&
                    readBack.Triangles == expected.Triangles;

        if (!same)
        {
            return new NavmeshPersistence(false, "readback_mismatch",
                $"cell 0x{expected.CellFormId:X8} reads back {readBack.Forms} form(s), {readBack.Vertices} vertices and " +
                $"{readBack.Triangles} triangles from {Path.GetFileName(pluginPath)}, but the editor built " +
                $"{expected.Forms} form(s), {expected.Vertices} vertices and {expected.Triangles} triangles",
                pluginPath, bytes, expected, readBack);
        }

        return new NavmeshPersistence(true, "persisted",
            $"cell 0x{expected.CellFormId:X8} reads back from {Path.GetFileName(pluginPath)} as " +
            $"{readBack.Forms} form(s), {readBack.Vertices} vertices and {readBack.Triangles} triangles, " +
            "the same geometry the editor built",
            pluginPath, bytes, expected, readBack);
    }

    /// Walks the record tree the way the kit writes it: a CELL record is a sibling of the child group that holds
    /// the cell's NAVM records, so the cell in scope is the last CELL seen at that level.
    public static NavmeshFileReport Read(string pluginPath)
    {
        using var stream = new FileStream(pluginPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);

        long bytes = stream.Length;
        var navmeshes = new List<NavmeshRecord>();
        string? error = null;

        void Walk(long start, long end, uint cell, ref string? failure)
        {
            long position = start;
            while (position + RecordHeaderSize <= end)
            {
                stream.Position = position;
                string signature = Encoding.ASCII.GetString(reader.ReadBytes(4));
                uint size = reader.ReadUInt32();
                uint flags = reader.ReadUInt32();
                uint formId = reader.ReadUInt32();

                if (signature == GroupSignature)
                {
                    int groupType = reader.ReadInt32();
                    long childStart = position + GroupHeaderSize;
                    long groupEnd = position + size;

                    if (size < GroupHeaderSize || groupEnd > end)
                    {
                        failure ??= $"a {groupType} group at {position} claims {size} bytes, past the end of the file";
                        return;
                    }

                    Walk(childStart, groupEnd, cell, ref failure);
                    position = groupEnd;
                    continue;
                }

                if (size > end - (position + RecordHeaderSize))
                {
                    failure ??= $"{signature} at {position} claims {size} bytes, past the end of its group";
                    return;
                }

                if (signature == CellSignature) cell = formId;

                if (signature == NavmSignature && cell != 0)
                {
                    // The record header also carries a revision, form version and two unknown bytes, so the
                    // payload starts at the header's end and not where the form id stopped.
                    stream.Position = position + RecordHeaderSize;
                    byte[] data = reader.ReadBytes((int)size);
                    navmeshes.Add(ReadNavmesh(cell, formId, flags, data));
                }

                position += RecordHeaderSize + size;
            }
        }

        Walk(0, bytes, 0, ref error);

        return new NavmeshFileReport(pluginPath, bytes, navmeshes.Count, navmeshes, error);
    }

    private static NavmeshRecord ReadNavmesh(uint cell, uint formId, uint flags, byte[] data)
    {
        bool compressed = (flags & CompressedFlag) != 0;

        try
        {
            byte[] payload = compressed ? Decompress(data) : data;

            // Constraint: the geometry lives in one NVNM subrecord; a record without it is a real, empty navmesh.
            int at = 0;
            while (at + 6 <= payload.Length)
            {
                string signature = Encoding.ASCII.GetString(payload, at, 4);
                uint length = BitConverter.ToUInt16(payload, at + 4);
                int dataAt = at + 6;

                if (signature == "XXXX")
                {
                    length = BitConverter.ToUInt32(payload, dataAt);
                    at += 6;
                    signature = Encoding.ASCII.GetString(payload, at, 4);
                    dataAt = at + 6;
                }

                if (signature == GeometrySignature)
                {
                    (int vertices, int triangles, string? error) = ReadGeometry(payload, dataAt, (int)length);
                    return new NavmeshRecord(cell, formId, compressed, vertices, triangles, error);
                }

                at = dataAt + (int)length;
                if (length == 0) break;
            }

            return new NavmeshRecord(cell, formId, compressed, 0, 0, null);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException)
        {
            return new NavmeshRecord(cell, formId, compressed, 0, 0, $"its payload could not be decompressed: {exception.Message}");
        }
    }

    /// The NVNM header the kit writes is version, the pathing-cell CRC, two grid words, then the vertex array and
    /// the triangle count, so the counts are read positionally and the layout is checked before they are trusted.
    private static (int Vertices, int Triangles, string? Error) ReadGeometry(byte[] payload, int start, int length)
    {
        int end = start + length;
        if (length < 20 || end > payload.Length)
            return (0, 0, "its NVNM subrecord is shorter than the geometry header");

        uint crc = BitConverter.ToUInt32(payload, start + 4);
        if (crc != PathingCellCrc)
        {
            return (0, 0,
                $"its NVNM header carries 0x{crc:X8} where the navmesh geometry magic 0x{PathingCellCrc:X8} belongs");
        }

        int vertices = (int)BitConverter.ToUInt32(payload, start + 16);
        if (vertices < 0 || vertices > MaxVertices)
            return (0, 0, $"its NVNM vertex count {vertices} is outside anything the editor writes");

        long trianglesAt = start + 20 + (long)vertices * 12;
        if (trianglesAt + 4 > end)
            return (0, 0, $"its NVNM vertex array of {vertices} runs past the subrecord");

        int triangles = (int)BitConverter.ToUInt32(payload, (int)trianglesAt);
        if (triangles < 0 || triangles > MaxVertices)
            return (0, 0, $"its NVNM triangle count {triangles} is outside anything the editor writes");

        return (vertices, triangles, null);
    }

    private static byte[] Decompress(byte[] data)
    {
        if (data.Length < 8) throw new InvalidDataException("a compressed record holds no zlib stream");

        int declared = (int)BitConverter.ToUInt32(data, 0);
        using var input = new MemoryStream(data, 4, data.Length - 4, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(declared > 0 ? declared : 0);
        zlib.CopyTo(output);
        return output.ToArray();
    }

    /// The plugin reply names a form id as eight hex digits, which is the only key that identifies a cell.
    public static bool TryParseFormId(string? text, out uint formId)
    {
        formId = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        string trimmed = text.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[2..];
        return uint.TryParse(trimmed, System.Globalization.NumberStyles.HexNumber,
            System.Globalization.CultureInfo.InvariantCulture, out formId);
    }

    private static bool IsPlugin(string path)
    {
        string extension = Path.GetExtension(path);
        return PluginExtensions.Any(wanted => string.Equals(wanted, extension, StringComparison.OrdinalIgnoreCase));
    }
}
