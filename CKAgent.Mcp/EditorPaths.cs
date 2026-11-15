using System.Diagnostics;

namespace Garden.CkAgent;

/// Paths must come from --ck/--game: the MCP client launches us from any directory.
public sealed class EditorPaths
{
    private EditorPaths(string creationKitPath, string gameDirectory, string stateDirectory)
    {
        CreationKitPath = creationKitPath;
        GameDirectory = gameDirectory;
        DataDirectory = Path.Combine(gameDirectory, "Data");
        StateDirectory = stateDirectory;
        ManifestPath = Path.Combine(stateDirectory, "commands.json");
        JobsDirectory = Path.Combine(stateDirectory, "jobs");
        BackupsDirectory = Path.Combine(stateDirectory, "backups");
    }

    public string CreationKitPath { get; }
    public string GameDirectory { get; }
    public string DataDirectory { get; }
    public string StateDirectory { get; }
    public string ManifestPath { get; }
    public string JobsDirectory { get; }
    public string BackupsDirectory { get; }

    public string EditorWarningsPath => Path.Combine(GameDirectory, "EditorWarnings.txt");

    public string CkpeLogPath => Path.Combine(GameDirectory, "Logs", "CKPE", "CreationKitPlatformExtended.log");

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

        string stateRoot;
        if (!string.IsNullOrWhiteSpace(state))
        {
            try { stateRoot = Path.GetFullPath(state); }
            catch (Exception) { error = "--state is not a valid path"; return false; }
        }
        else
        {
            stateRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ckagent");
        }

        try
        {
            Directory.CreateDirectory(stateRoot);
            Directory.CreateDirectory(Path.Combine(stateRoot, "jobs"));
            Directory.CreateDirectory(Path.Combine(stateRoot, "backups"));
        }
        catch (Exception exception)
        {
            error = $"state directory {stateRoot} is not writable: {exception.Message}";
            return false;
        }

        paths = new EditorPaths(exe, gameDirectory, stateRoot);
        return true;
    }
}
