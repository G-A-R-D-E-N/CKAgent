using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Garden.CkAgent;

/// One NAVM record as it sits in a saved plugin, with the geometry digest read out of its NVNM payload.
public sealed record NavmeshRecord(
    uint CellFormId,
    uint NavmFormId,
    bool Compressed,
    int Vertices,
    int Triangles,
    string? VertexDigest,
    string? TriangleDigest,
    string? Error);

/// What one saved plugin holds, read without the editor: every NAVM record, keyed by the cell that owns it.
public sealed record NavmeshFileReport(
    string Path,
    long Bytes,
    int Records,
    IReadOnlyList<NavmeshRecord> Navmeshes,
    string? Error);

/// The geometry the editor just produced for one of the cell's navmeshes, which the saved record has to match.
public sealed record NavmeshExpectedRecord(
    uint NavmFormId,
    int Vertices,
    int Triangles,
    string VertexDigest,
    string TriangleDigest);

/// The cell under test and every navmesh record the editor holds for it.
public sealed record NavmeshExpected(uint CellFormId, IReadOnlyList<NavmeshExpectedRecord> Records);

/// What the saved plugin turned out to carry for that cell.
public sealed record NavmeshReadback(
    int Forms,
    int Vertices,
    int Triangles,
    IReadOnlyList<NavmeshRecord> Records);

/// Invariant: a mesh is delivered only when the saved records hold the same vertices and triangle indices the
/// editor built, so a record that differs is named rather than averaged into a cell total.
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

/// Regression: the save chain used to stop at "the file's bytes changed", then at per-cell totals, so a corrupted
/// triangle index or a shifted vertex still read as delivered. This compares the geometry itself.
public static class NavmeshPersist
{
    private const string NavmSignature = "NAVM";
    private const string CellSignature = "CELL";
    private const string GroupSignature = "GRUP";
    private const string GeometrySignature = "NVNM";
    private const uint CompressedFlag = 0x00040000;
    private const uint PathingCellCrc = 0xA5E9A03C;
    private const uint ModernVersion = 15;
    private const int RecordHeaderSize = 24;
    private const int GroupHeaderSize = 24;
    private const int MaxElements = 2000000;

    // The kit's NVNM geometry sizes, measured against the fixture and pinned by Radicle's reader: three f32 per
    // vertex, and 21-byte triangles (three u16 indices, three i16 edges, f32 clearance, u8 traversal, u32 flags).
    private const int VertexStride = 12;
    private const int TriangleStride = 21;
    private const int TriangleIndexBytes = 6;
    private const int EdgeStride = 11;
    private const int DoorStride = 10;

    // The digest both sides compute: FNV-1a 64 over the bytes the file stores. NavmeshDigest.h asserts the same
    // vector at compile time, so a drift on either side fails a build instead of a save verdict.
    private const ulong FnvOffsetBasis = 0xCBF29CE484222325UL;
    private const ulong FnvPrime = 0x100000001B3UL;

    private static readonly string[] PluginExtensions = { ".esp", ".esm", ".esl" };

    public static string Digest(ReadOnlySpan<byte> bytes)
    {
        ulong hash = FnvOffsetBasis;
        foreach (byte value in bytes)
        {
            hash ^= value;
            hash *= FnvPrime;
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

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

    /// The expectations a saved file's own records would satisfy, used to compare one read against another.
    public static NavmeshExpected Expect(NavmeshFileReport report, uint cellFormId)
    {
        var records = report.Navmeshes
            .Where(mesh => mesh.CellFormId == cellFormId && mesh.Error is null && mesh.VertexDigest is not null)
            .Select(mesh => new NavmeshExpectedRecord(mesh.NavmFormId, mesh.Vertices, mesh.Triangles,
                mesh.VertexDigest!, mesh.TriangleDigest!))
            .ToList();

        return new NavmeshExpected(cellFormId, records);
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
                $"{Path.GetFileName(pluginPath)} holds {report.Records} NAVM record(s), none of cell " +
                $"0x{expected.CellFormId:X8}",
                pluginPath, bytes, expected, null);
        }

        NavmeshRecord? unreadable = owned.FirstOrDefault(mesh => mesh.Error is not null);
        if (unreadable is not null)
        {
            return new NavmeshPersistence(false, "readback_unreadable",
                $"NAVM 0x{unreadable.NavmFormId:X8} in cell 0x{expected.CellFormId:X8} could not be read: {unreadable.Error}",
                pluginPath, bytes, expected, null);
        }

        var readBack = new NavmeshReadback(owned.Count, owned.Sum(mesh => mesh.Vertices),
            owned.Sum(mesh => mesh.Triangles), owned);

        foreach (NavmeshExpectedRecord wanted in expected.Records)
        {
            NavmeshRecord? found = owned.FirstOrDefault(mesh => mesh.NavmFormId == wanted.NavmFormId);
            if (found is null)
            {
                return Mismatch("geometry_short",
                    $"NAVM 0x{wanted.NavmFormId:X8} is not in {Path.GetFileName(pluginPath)}, so the geometry the " +
                    $"editor built for it ({wanted.Vertices} vertices, {wanted.Triangles} triangles) was not written");
            }

            if (found.Vertices != wanted.Vertices || found.Triangles != wanted.Triangles)
            {
                return Mismatch("geometry_short",
                    $"NAVM 0x{wanted.NavmFormId:X8} holds {found.Vertices} vertices and {found.Triangles} triangles " +
                    $"in {Path.GetFileName(pluginPath)}, but the editor built {wanted.Vertices} and {wanted.Triangles}");
            }

            if (!string.Equals(found.VertexDigest, wanted.VertexDigest, StringComparison.Ordinal))
            {
                return Mismatch("vertex_mismatch",
                    $"NAVM 0x{wanted.NavmFormId:X8} has vertex positions {found.VertexDigest} in " +
                    $"{Path.GetFileName(pluginPath)}, but the editor built {wanted.VertexDigest}");
            }

            if (!string.Equals(found.TriangleDigest, wanted.TriangleDigest, StringComparison.Ordinal))
            {
                return Mismatch("triangle_index_mismatch",
                    $"NAVM 0x{wanted.NavmFormId:X8} has triangle indices {found.TriangleDigest} in " +
                    $"{Path.GetFileName(pluginPath)}, but the editor built {wanted.TriangleDigest}");
            }
        }

        // Constraint: a navmesh the editor emptied reports no digest, so its record is neither expected nor extra.
        List<uint> extra = owned.Where(mesh => mesh.Vertices != 0 || mesh.Triangles != 0)
            .Select(mesh => mesh.NavmFormId)
            .Where(id => expected.Records.All(wanted => wanted.NavmFormId != id))
            .ToList();
        if (extra.Count > 0)
        {
            return Mismatch("record_mismatch",
                $"{Path.GetFileName(pluginPath)} carries NAVM record(s) the editor did not build for cell " +
                $"0x{expected.CellFormId:X8}: {string.Join(", ", extra.Select(id => $"0x{id:X8}"))}");
        }

        return new NavmeshPersistence(true, "persisted",
            $"cell 0x{expected.CellFormId:X8} reads back from {Path.GetFileName(pluginPath)} as " +
            $"{readBack.Forms} form(s), {readBack.Vertices} vertices and {readBack.Triangles} triangles, byte-for-byte " +
            "the geometry the editor built",
            pluginPath, bytes, expected, readBack);

        NavmeshPersistence Mismatch(string code, string message) =>
            new(false, code, message, pluginPath, bytes, expected, readBack);
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
                    return ReadGeometry(cell, formId, compressed, payload, dataAt, (int)length);
                }

                at = dataAt + (int)length;
                if (length == 0) break;
            }

            return new NavmeshRecord(cell, formId, compressed, 0, 0, null, null, null);
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or ArgumentException)
        {
            return new NavmeshRecord(cell, formId, compressed, 0, 0, null, null,
                $"its payload could not be decompressed: {exception.Message}");
        }
    }

    /// The counted NVNM grammar the kit writes at version 15: version, pathing-cell CRC, world link, the parent
    /// word, then the counted vertex, triangle, edge and door arrays, and the cover/grid sections left opaque.
    private static NavmeshRecord ReadGeometry(uint cell, uint formId, bool compressed, byte[] payload, int start, int length)
    {
        NavmeshRecord Bad(string error) => new(cell, formId, compressed, 0, 0, null, null, error);

        int end = start + length;
        if (length < 20 || end > payload.Length)
            return Bad("its NVNM subrecord is shorter than the geometry header");

        uint version = BitConverter.ToUInt32(payload, start);
        if (version != ModernVersion)
            return Bad($"its NVNM geometry version is {version}, and only version {ModernVersion} is a layout this reads");

        uint crc = BitConverter.ToUInt32(payload, start + 4);
        if (crc != PathingCellCrc)
            return Bad($"its NVNM header carries 0x{crc:X8} where the pathing-cell magic 0x{PathingCellCrc:X8} belongs");

        int at = start + 16;
        if (!TryCount(payload, ref at, end, out int vertices)) return Bad("its NVNM vertex count runs past the subrecord");
        if (!Fits(at, vertices, VertexStride, end, out int verticesAt)) return Bad("its NVNM vertex array runs past the subrecord");

        int vertexBytes = vertices * VertexStride;
        string vertexDigest = Digest(payload.AsSpan(verticesAt, vertexBytes));

        at = verticesAt + vertexBytes;
        if (!TryCount(payload, ref at, end, out int triangles)) return Bad("its NVNM triangle count runs past the subrecord");
        if (!Fits(at, triangles, TriangleStride, end, out int trianglesAt)) return Bad("its NVNM triangle array runs past the subrecord");

        var indices = new byte[triangles * TriangleIndexBytes];
        for (int t = 0; t < triangles; t++)
            Buffer.BlockCopy(payload, trianglesAt + t * TriangleStride, indices, t * TriangleIndexBytes, TriangleIndexBytes);
        string triangleDigest = Digest(indices);

        // The edge and door arrays are counted the same way; reading past them is how a wrong stride shows up here
        // instead of as a wrong digest later.
        at = trianglesAt + triangles * TriangleStride;
        if (!TryCount(payload, ref at, end, out int edges)) return Bad("its NVNM edge-link count runs past the subrecord");
        if (!Fits(at, edges, EdgeStride, end, out _)) return Bad("its NVNM edge-link array runs past the subrecord");

        at += edges * EdgeStride;
        if (!TryCount(payload, ref at, end, out int doors)) return Bad("its NVNM door-link count runs past the subrecord");
        if (!Fits(at, doors, DoorStride, end, out _)) return Bad("its NVNM door-link array runs past the subrecord");

        return new NavmeshRecord(cell, formId, compressed, vertices, triangles, vertexDigest, triangleDigest, null);
    }

    private static bool TryCount(byte[] payload, ref int at, int end, out int count)
    {
        count = 0;
        if (at + 4 > end) return false;

        uint raw = BitConverter.ToUInt32(payload, at);
        at += 4;
        if (raw > MaxElements) return false;

        count = (int)raw;
        return true;
    }

    private static bool Fits(int at, int count, int stride, int end, out int dataAt)
    {
        dataAt = at;
        return (long)at + (long)count * stride <= end;
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
        return uint.TryParse(trimmed, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out formId);
    }

    private static bool IsPlugin(string path)
    {
        string extension = Path.GetExtension(path);
        return PluginExtensions.Any(wanted => string.Equals(wanted, extension, StringComparison.OrdinalIgnoreCase));
    }
}
