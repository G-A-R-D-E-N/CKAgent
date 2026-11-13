using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Garden.CkAgent;

public sealed record CommandEntry(
    [property: JsonPropertyName("menu")] string Menu,
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("text")] string Text,
    [property: JsonPropertyName("id")] uint Id);

public sealed class CommandManifestDocument
{
    [JsonPropertyName("build")] public string Build { get; set; } = "";
    [JsonPropertyName("capturedUtc")] public string CapturedUtc { get; set; } = "";
    [JsonPropertyName("commands")] public List<CommandEntry> Commands { get; set; } = new();
}

// Command ids must come from the running editor: a Steam update renumbers them.
public sealed class CommandStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly EditorPaths _paths;
    private readonly object _gate = new();
    private CommandManifestDocument? _document;

    public CommandStore(EditorPaths paths) => _paths = paths;

    public CommandManifestDocument? Load()
    {
        lock (_gate)
        {
            if (_document != null) return _document;
            if (!File.Exists(_paths.ManifestPath)) return null;

            try
            {
                var loaded = JsonSerializer.Deserialize<CommandManifestDocument>(
                    File.ReadAllText(_paths.ManifestPath), JsonOptions);
                _document = loaded;
                return _document;
            }
            catch (Exception)
            {
                // Capture must rewrite a corrupt manifest instead of failing.
                return null;
            }
        }
    }

    public CommandManifestDocument Capture(IntPtr menu, string build)
    {
        var commands = new List<CommandEntry>();
        int topLevel = Win32.GetMenuItemCount(menu);

        for (int i = 0; i < topLevel; i++)
        {
            if (!TryReadItem(menu, (uint)i, out uint id, out IntPtr submenu, out string label))
                continue;
            if (submenu == IntPtr.Zero)
                continue;

            string menuName = Clean(label);
            if (menuName.Length == 0)
                continue;

            Walk(submenu, menuName, new List<string> { menuName }, commands, depth: 0);
        }

        var document = new CommandManifestDocument
        {
            Build = build,
            CapturedUtc = DateTime.UtcNow.ToString("O"),
            // Ordering must be ordinal so the file stays byte-stable between captures.
            Commands = commands
                .GroupBy(c => (c.Path, c.Id))
                .Select(g => g.First())
                .OrderBy(c => c.Path, StringComparer.Ordinal)
                .ToList(),
        };

        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_paths.ManifestPath)!);
            File.WriteAllText(_paths.ManifestPath, JsonSerializer.Serialize(document, JsonOptions));
            _document = document;
        }

        return document;
    }

    // A query must resolve a path, a unique leaf name, or a numeric id.
    public bool TryResolve(string query, out CommandEntry entry, out string error)
    {
        entry = null!;
        error = "";
        var document = Load();
        if (document == null)
        {
            error = "no command manifest yet: call ck_commands while the editor is running";
            return false;
        }

        string wanted = Normalize(query);
        if (wanted.Length == 0)
        {
            error = "command is required";
            return false;
        }

        if (uint.TryParse(query, out uint numeric))
        {
            var byId = document.Commands.FirstOrDefault(c => c.Id == numeric);
            if (byId != null) { entry = byId; return true; }
        }

        var exact = document.Commands.FirstOrDefault(c => Normalize(c.Path) == wanted);
        if (exact != null) { entry = exact; return true; }

        string leaf = wanted.Contains('/') ? wanted[(wanted.LastIndexOf('/') + 1)..] : wanted;
        var matches = document.Commands.Where(c => Normalize(Clean(c.Text)) == leaf).ToList();
        if (matches.Count == 1) { entry = matches[0]; return true; }
        if (matches.Count > 1)
        {
            error = $"command '{query}' is ambiguous: {string.Join(", ", matches.Take(6).Select(m => m.Path))}";
            return false;
        }

        error = $"no command '{query}' in the manifest ({document.Commands.Count} commands from build {document.Build})";
        return false;
    }

    private static void Walk(IntPtr menu, string menuName, List<string> trail,
                             List<CommandEntry> sink, int depth)
    {
        if (depth > 6) return;

        int count = Win32.GetMenuItemCount(menu);
        for (int i = 0; i < count; i++)
        {
            if (!TryReadItem(menu, (uint)i, out uint id, out IntPtr submenu, out string label))
                continue;

            if (submenu != IntPtr.Zero)
            {
                string name = Clean(label);
                if (name.Length == 0) continue;
                trail.Add(name);
                Walk(submenu, menuName, trail, sink, depth + 1);
                trail.RemoveAt(trail.Count - 1);
                continue;
            }

            if (id == 0) continue;                     // separator
            string leaf = Clean(label);
            if (leaf.Length == 0) continue;

            trail.Add(leaf);
            sink.Add(new CommandEntry(menuName, string.Join('/', trail), label.Trim(), id));
            trail.RemoveAt(trail.Count - 1);
        }
    }

    private static bool TryReadItem(IntPtr menu, uint position, out uint id, out IntPtr submenu, out string label)
    {
        id = 0;
        submenu = IntPtr.Zero;
        label = "";

        var info = new Win32.MenuItemInfo
        {
            Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Win32.MenuItemInfo>(),
            Mask = Win32.MiimId | Win32.MiimSubMenu,
        };

        if (!Win32.GetMenuItemInfoW(menu, position, true, ref info))
            return false;

        id = info.Id;
        submenu = info.SubMenu;

        var buffer = new StringBuilder(512);
        int length = Win32.GetMenuStringW(menu, position, buffer, buffer.Capacity, Win32.MfByPosition);
        label = length > 0 ? buffer.ToString() : "";
        return true;
    }

    // Callers must pass raw menu text; accelerator and shortcut are stripped here.
    private static string Clean(string label)
    {
        int tab = label.IndexOf('\t');
        if (tab >= 0) label = label[..tab];
        return label.Replace("&", "").Trim();
    }

    private static string Normalize(string value)
    {
        string cleaned = Clean(value).Replace('\\', '/');
        var parts = cleaned.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join('/', parts).ToLowerInvariant();
    }
}
