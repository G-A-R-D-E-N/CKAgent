using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Garden.CkAgent;

public enum JobKind
{
    Precombine,
    Previs,
}

public sealed class JobArtifact
{
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("exists")] public bool Exists { get; set; }
    [JsonPropertyName("bytes")] public long? Bytes { get; set; }
}

public sealed class JobRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("plugin")] public string Plugin { get; set; } = "";
    [JsonPropertyName("mode")] public string Mode { get; set; } = "";
    [JsonPropertyName("scope")] public string Scope { get; set; } = "";
    [JsonPropertyName("commandLine")] public string CommandLine { get; set; } = "";
    [JsonPropertyName("state")] public string State { get; set; } = "running";
    [JsonPropertyName("message")] public string? Message { get; set; }
    [JsonPropertyName("startedUtc")] public string StartedUtc { get; set; } = "";
    [JsonPropertyName("endedUtc")] public string? EndedUtc { get; set; }
    [JsonPropertyName("exitCode")] public int? ExitCode { get; set; }
    [JsonPropertyName("logPath")] public string LogPath { get; set; } = "";
    [JsonPropertyName("warningsPath")] public string? WarningsPath { get; set; }
    [JsonPropertyName("artifacts")] public List<JobArtifact> Artifacts { get; set; } = new();
}

public sealed record JobStart(string Status, string Code, string Message, JobRecord? Job);

/// Jobs must outlive the MCP connection: state lives in jobs/&lt;id&gt;/job.json.
public sealed class JobRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] Modes = { "clean", "filtered" };
    private static readonly string[] Scopes = { "all", "main", "ints", "other" };

    private readonly EditorPaths _paths;
    private readonly object _gate = new();
    private readonly Dictionary<string, JobRecord> _jobs = new(StringComparer.OrdinalIgnoreCase);
    private string? _running;

    public JobRunner(EditorPaths paths)
    {
        _paths = paths;
        LoadExisting();
    }

    public JobStart Start(JobKind kind, string plugin, string mode, string scope)
    {
        if (!IsValidPluginName(plugin, out string pluginError))
            return new JobStart("error", "invalid_plugin", pluginError, null);

        if (!File.Exists(Path.Combine(_paths.DataDirectory, plugin)))
            return new JobStart("error", "plugin_not_found",
                $"{plugin} is not in {_paths.DataDirectory}", null);

        string normalizedMode = mode.Trim().ToLowerInvariant();
        if (!Modes.Contains(normalizedMode))
            return new JobStart("error", "invalid_mode",
                $"mode must be one of {string.Join(", ", Modes)}", null);

        string normalizedScope = scope.Trim().ToLowerInvariant();
        if (!Scopes.Contains(normalizedScope))
            return new JobStart("error", "invalid_scope",
                $"scope must be one of {string.Join(", ", Scopes)}", null);

        if (!File.Exists(_paths.CreationKitPath))
            return new JobStart("error", "editor_missing",
                $"CreationKit.exe is not at {_paths.CreationKitPath}", null);

        lock (_gate)
        {
            if (_running != null)
                return new JobStart("error", "job_running",
                    $"job {_running} is still running; the editor generates one plugin at a time", null);

            string id = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" +
                        Guid.NewGuid().ToString("N")[..6];

            string folder = Path.Combine(_paths.JobsDirectory, id);
            Directory.CreateDirectory(folder);

            string switchName = kind == JobKind.Precombine ? "-GeneratePreCombined" : "-GeneratePreVisData";

            var record = new JobRecord
            {
                Id = id,
                Kind = kind == JobKind.Precombine ? "precombine" : "previs",
                Plugin = plugin,
                Mode = normalizedMode,
                Scope = normalizedScope,
                CommandLine = $"CreationKit.exe {switchName}:{plugin} {normalizedMode} {normalizedScope}",
                StartedUtc = DateTime.UtcNow.ToString("O"),
                LogPath = Path.Combine(folder, "console.log"),
            };

            var startInfo = new ProcessStartInfo(_paths.CreationKitPath)
            {
                WorkingDirectory = _paths.GameDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            startInfo.ArgumentList.Add($"{switchName}:{plugin}");
            startInfo.ArgumentList.Add(normalizedMode);
            startInfo.ArgumentList.Add(normalizedScope);

            SnapshotWarnings(folder);

            _jobs[id] = record;
            _running = id;
            Save(record);

            var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };

            try
            {
                process.Start();
            }
            catch (Exception exception)
            {
                _running = null;
                record.State = "failed";
                record.Message = $"could not start the editor: {exception.Message}";
                record.EndedUtc = DateTime.UtcNow.ToString("O");
                Save(record);
                return new JobStart("error", "start_failed", record.Message, record);
            }

            _ = Task.Run(() => RunAsync(record, folder, process));

            return new JobStart("ok", "started", $"job {id} started", record);
        }
    }

    public JobRecord? Get(string? id)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(id))
                return _running != null && _jobs.TryGetValue(_running, out var running) ? running : null;

            return _jobs.TryGetValue(id, out var job) ? job : null;
        }
    }

    public IReadOnlyList<JobRecord> All()
    {
        lock (_gate) return _jobs.Values.OrderByDescending(j => j.StartedUtc, StringComparer.Ordinal).ToList();
    }

    public static string ReadTail(string path, int lines)
    {
        if (!File.Exists(path)) return "";

        lines = Math.Clamp(lines, 1, 5000);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var ring = new Queue<string>(lines);
        while (reader.ReadLine() is { } line)
        {
            ring.Enqueue(line);
            if (ring.Count > lines) ring.Dequeue();
        }

        return string.Join('\n', ring);
    }

    private async Task RunAsync(JobRecord record, string folder, Process process)
    {
        var clock = Stopwatch.StartNew();
        using var log = new StreamWriter(new FileStream(record.LogPath, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite), new UTF8Encoding(false)) { AutoFlush = true };
        var gate = new object();

        void Write(string text)
        {
            lock (gate)
            {
                log.WriteLine(text);
            }
        }

        Write($"# ckagent job {record.Id}");
        Write($"# {record.CommandLine}");
        Write($"# started {record.StartedUtc}");

        process.OutputDataReceived += (_, e) => { if (e.Data != null) Write(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) Write("[stderr] " + e.Data); };

        try
        {
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Write($"[ckagent] lost the editor process: {exception.Message}");
        }

        int exitCode = -1;
        try { exitCode = process.ExitCode; }
        catch (Exception) { /* never started or already reaped */ }
        finally { process.Dispose(); }

        lock (_gate)
        {
            record.ExitCode = exitCode;
            record.EndedUtc = DateTime.UtcNow.ToString("O");
            record.Artifacts = CollectArtifacts(record);
            record.WarningsPath = CollectWarnings(record, folder);

            bool anyArtifact = record.Artifacts.Any(a => a.Exists);
            if (exitCode != 0)
            {
                record.State = "failed";
                record.Message = $"editor exited with {exitCode}; see {record.LogPath}";
            }
            else if (!anyArtifact)
            {
                // Exit 0 must not count as success when no artifact was written.
                record.State = "suspect";
                record.Message = "editor exited 0 but none of the expected artifacts exist; " +
                                 "check the log and the plugin's output paths";
            }
            else
            {
                record.State = "succeeded";
                record.Message = $"finished in {clock.Elapsed:hh\\:mm\\:ss}";
            }

            _running = null;
            Write($"# state={record.State} exit={exitCode} elapsed={clock.Elapsed}");
            Save(record);
        }
    }

    private List<JobArtifact> CollectArtifacts(JobRecord record)
    {
        var candidates = new List<string>
        {
            Path.Combine(_paths.DataDirectory, "Meshes", "Precombined", record.Plugin),
            Path.Combine(_paths.DataDirectory, "vis", record.Plugin),
            Path.Combine(_paths.DataDirectory, "CombinedObjects.esp"),
            Path.Combine(_paths.DataDirectory, "Previs.esp"),
        };

        var artifacts = new List<JobArtifact>();
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
                artifacts.Add(new JobArtifact { Path = candidate, Exists = true, Bytes = new FileInfo(candidate).Length });
            else if (Directory.Exists(candidate))
                artifacts.Add(new JobArtifact { Path = candidate, Exists = true, Bytes = null });
            else
                artifacts.Add(new JobArtifact { Path = candidate, Exists = false });
        }

        return artifacts;
    }

    private void SnapshotWarnings(string folder)
    {
        try
        {
            if (File.Exists(_paths.EditorWarningsPath))
                File.Copy(_paths.EditorWarningsPath, Path.Combine(folder, "EditorWarnings-before.txt"), true);
        }
        catch (Exception)
        {
            // A missing snapshot must not fail the job.
        }
    }

    private string? CollectWarnings(JobRecord record, string folder)
    {
        try
        {
            if (!File.Exists(_paths.EditorWarningsPath)) return null;
            string target = Path.Combine(folder, "EditorWarnings.txt");
            File.Copy(_paths.EditorWarningsPath, target, true);
            return target;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void Save(JobRecord record)
    {
        try
        {
            string folder = Path.Combine(_paths.JobsDirectory, record.Id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "job.json"), JsonSerializer.Serialize(record, JsonOptions));
        }
        catch (Exception)
        {
            // The file must only mirror the in-memory record.
        }
    }

    private void LoadExisting()
    {
        if (!Directory.Exists(_paths.JobsDirectory)) return;

        foreach (string folder in Directory.EnumerateDirectories(_paths.JobsDirectory))
        {
            string file = Path.Combine(folder, "job.json");
            if (!File.Exists(file)) continue;

            try
            {
                var record = JsonSerializer.Deserialize<JobRecord>(File.ReadAllText(file), JsonOptions);
                if (record == null || string.IsNullOrEmpty(record.Id)) continue;

                // A record left running must mean the editor is gone: nothing we own survives a restart.
                if (record.State == "running")
                {
                    record.State = "interrupted";
                    record.Message = "ckagent restarted while this job was running; the editor process is no longer tracked";
                    Save(record);
                }

                _jobs[record.Id] = record;
            }
            catch (Exception)
            {
                // An unreadable job folder must be skipped, never fatal.
            }
        }
    }

    private static bool IsValidPluginName(string plugin, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(plugin))
        {
            error = "plugin is required";
            return false;
        }

        if (plugin.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
        {
            error = "plugin must be a file name, not a path";
            return false;
        }

        string extension = Path.GetExtension(plugin).ToLowerInvariant();
        if (extension is not (".esp" or ".esm" or ".esl"))
        {
            error = "plugin must end in .esp, .esm or .esl";
            return false;
        }

        return true;
    }
}
