using System.Diagnostics;

namespace Garden.CkAgent;

/// Paths must come from --ck/--game: the MCP client launches us from any directory.
public sealed class EditorPaths
{
    private EditorPaths(string creationKitPath, string gameDirectory, string stateDirectory, string? stateNote)
    {
        CreationKitPath = creationKitPath;
        GameDirectory = gameDirectory;
        DataDirectory = Path.Combine(gameDirectory, "Data");
        StateDirectory = stateDirectory;
        StateDirectoryNote = stateNote;
        ManifestPath = Path.Combine(stateDirectory, "commands.json");
        JobsDirectory = Path.Combine(stateDirectory, "jobs");
        BackupsDirectory = Path.Combine(stateDirectory, "backups");
    }

    public string CreationKitPath { get; }
    public string GameDirectory { get; }
    public string DataDirectory { get; }
    public string StateDirectory { get; }

    // Constraint: the note is null for a first-choice folder and otherwise names why the host settled elsewhere.
    public string? StateDirectoryNote { get; }
    public string ManifestPath { get; }
    public string JobsDirectory { get; }
    public string BackupsDirectory { get; }

    public string EditorWarningsPath => Path.Combine(GameDirectory, "EditorWarnings.txt");

    public string? EditorVersion => File.Exists(CreationKitPath)
        ? FileVersionInfo.GetVersionInfo(CreationKitPath).FileVersion
        : null;

    public static bool TryCreate(string? creationKit, string? game, string? state,
                                 out EditorPaths? paths, out string error)
    {
        paths = null;
        error = "";

        string exe;
        if (!string.IsNullOrWhiteSpace(creationKit))
        {
            try { exe = Path.GetFullPath(creationKit); }
            catch (Exception) { error = "--ck is not a valid path"; return false; }

            if (!File.Exists(exe))
            {
                error = $"CreationKit.exe was not found at {creationKit}";
                return false;
            }
        }
        else if (!string.IsNullOrWhiteSpace(game))
        {
            string dir;
            try { dir = Path.GetFullPath(game); }
            catch (Exception) { error = "--game is not a valid path"; return false; }

            exe = Path.Combine(dir, "CreationKit.exe");
            if (!File.Exists(exe))
            {
                error = $"no CreationKit.exe in {dir}";
                return false;
            }
        }
        else
        {
            error = "--ck (or --game) is required: ckagent drives an installed Creation Kit, it cannot find one by itself";
            return false;
        }

        string gameDirectory = Path.GetDirectoryName(exe)!;
        string dataDirectory = Path.Combine(gameDirectory, "Data");
        if (!Directory.Exists(dataDirectory))
        {
            error = $"{gameDirectory} has no Data folder, so it does not look like a Fallout 4 install";
            return false;
        }

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(state))
        {
            // Constraint: an explicit --state is the caller's decision, so it is used or refused, never replaced.
            try { candidates.Add(Path.GetFullPath(state)); }
            catch (Exception) { error = "--state is not a valid path"; return false; }
        }
        else
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            candidates.Add(Path.Combine(profile, "ckagent"));
            candidates.Add(Path.Combine(profile, "Temp", "Low", "ckagent"));
            candidates.Add(Path.Combine(Path.GetTempPath(), "ckagent"));

            // Constraint: the host's own folder is the last resort, because a low-integrity host sits in a low-labelled folder.
            candidates.Add(Path.Combine(AppContext.BaseDirectory, "state"));
        }

        if (!StateFolder.Choose(candidates, out string stateRoot, out string stateNote))
        {
            error = $"no state directory is writable: {stateNote}";
            return false;
        }

        paths = new EditorPaths(exe, gameDirectory, stateRoot, stateNote.Length == 0 ? null : stateNote);
        return true;
    }
}
