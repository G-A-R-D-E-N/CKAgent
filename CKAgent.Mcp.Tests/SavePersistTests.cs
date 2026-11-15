using Garden.CkAgent;

namespace CKAgent.Mcp.Tests;

/// Regression: a mesh generated in memory must never be reported as a delivered cell, and a save must never run
/// over a plugin that has not been backed up first.
public sealed class SavePersistTests
{
    private static SaveOutcome Outcome(string status, string code) =>
        new(status, code, "message", "Fallout4.esm", @"C:\Data\Fallout4.esm", @"C:\state\backups\1\Fallout4.esm",
            136, status == "ok" ? 45653 : 136, 12, 40001);

    [Fact]
    public void ASaveTheEditorConfirmedIsTheOnlyOneReportedPersisted()
    {
        var stamp = new DateTime(2026, 11, 14, 21, 24, 0, DateTimeKind.Utc);

        SaveVerdict ok = NavmeshTools.PersistOutcome(Outcome("ok", "saved"), stamp);
        SaveVerdict unobserved = NavmeshTools.PersistOutcome(Outcome("warning", "save_not_observed"), stamp);
        SaveVerdict errored = NavmeshTools.PersistOutcome(Outcome("error", "post_failed"), stamp);

        Assert.True(ok.Persisted);
        Assert.Equal("saved", ok.Code);
        Assert.Equal(stamp.ToString("O"), ok.SavedAtUtc);

        Assert.False(unobserved.Persisted);
        Assert.Equal("save_failed", unobserved.Code);
        Assert.Null(unobserved.SavedAtUtc);

        Assert.False(errored.Persisted);
        Assert.Equal("save_failed", errored.Code);
        Assert.Null(errored.SavedAtUtc);
    }

    [Fact]
    public void APluginThatIsNotInstalledIsRefusedBeforeAnythingIsCopied()
    {
        using var install = new TestInstall();

        SaveOutcome outcome = install.Save.Save("Missing.esp", IntPtr.Zero, 5);

        Assert.Equal("plugin_not_found", outcome.Code);
        Assert.False(Directory.Exists(install.Paths.BackupsDirectory) &&
                     Directory.EnumerateDirectories(install.Paths.BackupsDirectory).Any());
    }

    [Fact]
    public void APluginWithNoCommandManifestIsRefusedWithTheReasonToFixIt()
    {
        using var install = new TestInstall();
        install.Plugin("CKGardenNav.esp", 136);

        SaveOutcome outcome = install.Save.Save("CKGardenNav.esp", IntPtr.Zero, 5);

        Assert.Equal("command_unavailable", outcome.Code);
        Assert.Contains("command manifest", outcome.Message);
    }

    [Fact]
    public void TheBackupIsOnDiskWithTheOriginalBytesEvenWhenTheSaveNeverPosts()
    {
        using var install = new TestInstall();
        string plugin = install.Plugin("CKGardenNav.esp", 136);
        install.WriteManifest("File/Save", 40001);

        SaveOutcome outcome = install.Save.Save("CKGardenNav.esp", IntPtr.Zero, 5);

        Assert.Equal("post_failed", outcome.Code);
        Assert.NotNull(outcome.BackupPath);
        Assert.True(File.Exists(outcome.BackupPath));
        Assert.Equal(File.ReadAllBytes(plugin), File.ReadAllBytes(outcome.BackupPath!));
        Assert.StartsWith(install.Paths.BackupsDirectory, outcome.BackupPath);
        Assert.False(NavmeshTools.PersistOutcome(outcome, DateTime.UtcNow).Persisted);
    }

    /// A throwaway install: the game directory only has to hold CreationKit.exe and Data for EditorPaths, so the
    /// save path can be driven without a real editor.
    private sealed class TestInstall : IDisposable
    {
        public TestInstall()
        {
            Root = Path.Combine(AppContext.BaseDirectory, "scratch", Guid.NewGuid().ToString("N"));
            Game = Path.Combine(Root, "game");
            State = Path.Combine(Root, "state");
            Directory.CreateDirectory(Path.Combine(Game, "Data"));
            Directory.CreateDirectory(State);
            File.WriteAllText(Path.Combine(Game, "CreationKit.exe"), "not really the editor, only the file has to exist");

            Assert.True(EditorPaths.TryCreate(null, Game, State, out EditorPaths? paths, out string error), error);
            Paths = paths!;
            Save = new SaveCoordinator(Paths, new CommandStore(Paths));
        }

        public string Root { get; }
        public string Game { get; }
        public string State { get; }
        public EditorPaths Paths { get; }
        public SaveCoordinator Save { get; }

        public string Plugin(string name, int bytes)
        {
            string path = Path.Combine(Paths.DataDirectory, name);
            File.WriteAllBytes(path, new byte[bytes]);
            return path;
        }

        public void WriteManifest(string path, uint id)
        {
            Directory.CreateDirectory(Paths.StateDirectory);
            File.WriteAllText(Paths.ManifestPath,
                $"{{\"build\":\"test\",\"capturedUtc\":\"2026-11-15T00:00:00Z\"," +
                $"\"commands\":[{{\"menu\":\"File\",\"path\":\"{path}\",\"text\":\"Save\",\"id\":{id}}}]}}");
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }
}
