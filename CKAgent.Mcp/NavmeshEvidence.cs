using System.Globalization;
using System.Text.Json;

namespace Garden.CkAgent;

/// One geometry record as the evidence file states it: the form, its counts and the digests both sides compare.
public sealed record NavmeshEvidenceGeometry(string FormId, int Vertices, int Triangles, string? VerticesHash,
                                             string? TrianglesHash);

/// What one run of the pipeline did, written beside the state directory so the record outlives the chat that ran it.
public sealed record NavmeshEvidenceRecord(
    string CapturedUtc,
    string Mode,
    string Cell,
    string FormId,
    string CheckCode,
    bool Clean,
    bool Silent,
    bool Corroborated,
    string Reason,
    string? SaveCode,
    bool Persisted,
    string? File,
    long? Bytes,
    IReadOnlyList<NavmeshEvidenceGeometry> Editor,
    IReadOnlyList<NavmeshEvidenceGeometry> Written,
    IReadOnlyList<string> Pruned,
    string Message);

// Regression: whether a cell was delivered used to live only in the tool answer, so proving it afterwards meant
// re-running the job with a hand-assembled evidence file. Every apply that reaches an answer writes its own record.
public static class NavmeshEvidence
{
    private static readonly JsonSerializerOptions EnvelopeJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    /// Writes the record under <c>&lt;state&gt;/navmesh</c> and answers its path. A second run in the same second gets its
    /// own file rather than overwriting the first, so the evidence for two applies is two records.
    public static string Write(EditorPaths paths, string mode, string cellName, uint cellFormId,
                               NavmeshCheckEvidence check, NavmeshPersistence? persistence, string? prunedJson,
                               string message)
    {
        string directory = Path.Combine(paths.StateDirectory, "navmesh");
        Directory.CreateDirectory(directory);

        DateTime captured = DateTime.UtcNow;
        string stem = $"{cellFormId:X8}-{captured.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";
        string path = Path.Combine(directory, stem + ".json");

        for (int copy = 2; File.Exists(path); copy++)
            path = Path.Combine(directory, $"{stem}-{copy}.json");

        File.WriteAllText(path, Json(mode, cellName, cellFormId, check, persistence, prunedJson, message, captured));
        return path;
    }

    public static string Json(string mode, string cellName, uint cellFormId, NavmeshCheckEvidence check,
                              NavmeshPersistence? persistence, string? prunedJson, string message, DateTime captured)
    {
        var editor = new List<NavmeshEvidenceGeometry>();
        var written = new List<NavmeshEvidenceGeometry>();

        if (persistence?.Expected is { } expected)
        {
            foreach (NavmeshExpectedRecord record in expected.Records)
                editor.Add(new NavmeshEvidenceGeometry(record.NavmFormId.ToString("X8"), record.Vertices,
                    record.Triangles, record.VertexDigest, record.TriangleDigest));
        }

        if (persistence?.ReadBack is { } readBack)
        {
            foreach (NavmeshRecord record in readBack.Records)
                written.Add(new NavmeshEvidenceGeometry(record.NavmFormId.ToString("X8"), record.Vertices,
                    record.Triangles, record.VertexDigest, record.TriangleDigest));
        }

        var record = new NavmeshEvidenceRecord(
            CapturedUtc: captured.ToString("O", CultureInfo.InvariantCulture),
            Mode: mode,
            Cell: cellName,
            FormId: cellFormId.ToString("X8"),
            CheckCode: check.Code,
            Clean: check.Clean,
            Silent: check.Silent,
            Corroborated: check.Corroborated,
            Reason: check.Reason,
            SaveCode: persistence?.Code,
            Persisted: persistence?.Persisted ?? false,
            File: persistence?.PluginPath,
            Bytes: persistence?.Bytes,
            Editor: editor,
            Written: written,
            Pruned: PrunedIds(prunedJson),
            Message: message);

        return JsonSerializer.Serialize(record, EnvelopeJson) + "\n";
    }

    /// The ids the run dropped, read back out of the prune block the apply reply carries.
    public static IReadOnlyList<string> PrunedIds(string? prunedJson)
    {
        var ids = new List<string>();

        if (string.IsNullOrWhiteSpace(prunedJson)) return ids;

        try
        {
            using JsonDocument document = JsonDocument.Parse(prunedJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return ids;
            if (!document.RootElement.TryGetProperty("ids", out JsonElement list) ||
                list.ValueKind != JsonValueKind.Array)
                return ids;

            foreach (JsonElement entry in list.EnumerateArray())
                if (entry.ValueKind == JsonValueKind.String && entry.GetString() is { Length: > 0 } id)
                    ids.Add(id);
        }
        catch (JsonException)
        {
            ids.Clear();
        }

        return ids;
    }
}
