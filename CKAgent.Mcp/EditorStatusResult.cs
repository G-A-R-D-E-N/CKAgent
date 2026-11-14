namespace Garden.CkAgent;

public sealed record EditorStatusResult(
    string Status,
    string Code,
    string Message,
    bool Running,
    int? Pid,
    string? MainWindowTitle,
    long? MainWindowHandle,
    string CreationKitPath,
    string GameDirectory,
    string? EditorVersion,
    int ManifestCommands,
    string? ManifestBuild,
    string? ManifestCapturedUtc,
    bool PluginPipeAvailable,
    string StateDirectory,
    string? BlockedBy,
    IReadOnlyList<string>? ModalButtons,
    string? DismissBy,
    string? StateDirectoryNote);
