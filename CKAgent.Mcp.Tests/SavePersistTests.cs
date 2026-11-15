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

    /// The plugin a real run saved on this machine: Fallout4.esm's DiamondCity cell 0x00240045, written by the
    /// Creation Kit with four NAVM records, each in a zlib-compressed record the way the kit writes them.
    private static string SavedPlugin() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "CKGardenNav.esp");

    /// Regression: the save used to count as delivered once the file's bytes changed, which a plugin written
    /// without the generated mesh satisfies too. The read-back must come from the NAVM geometry the kit wrote.
    [Fact]
    public void TheNavmeshSavedIntoThePluginIsReadBackFromItsOwnGeometry()
    {
        NavmeshFileReport report = NavmeshPersist.Read(SavedPlugin());

        Assert.Null(report.Error);
        Assert.Equal(4, report.Records);
        Assert.All(report.Navmeshes, mesh => Assert.Equal(0x00240045u, mesh.CellFormId));
        Assert.All(report.Navmeshes, mesh => Assert.Null(mesh.Error));
        Assert.All(report.Navmeshes, mesh => Assert.True(mesh.Compressed));

        NavmeshRecord[] meshes = report.Navmeshes.OrderBy(mesh => mesh.NavmFormId).ToArray();
        Assert.Equal(new uint[] { 0x01000FA3, 0x01000FA4, 0x01000FA5, 0x01000FA6 },
            meshes.Select(mesh => mesh.NavmFormId).ToArray());
        Assert.Equal(new[] { 242, 113, 79, 21 }, meshes.Select(mesh => mesh.Vertices).ToArray());
        Assert.Equal(new[] { 289, 127, 91, 22 }, meshes.Select(mesh => mesh.Triangles).ToArray());

        NavmeshPersistence persisted = NavmeshPersist.Verify(SavedPlugin(), new NavmeshExpected(0x00240045, 4, 455, 529));

        Assert.True(persisted.Persisted);
        Assert.Equal("persisted", persisted.Code);
        Assert.Equal(45653L, persisted.Bytes!.Value);
        Assert.NotNull(persisted.ReadBack);
        Assert.Equal(4, persisted.ReadBack!.Forms);
        Assert.Equal(455, persisted.ReadBack.Vertices);
        Assert.Equal(529, persisted.ReadBack.Triangles);
    }

    /// Regression: a mesh left in memory reads as the file's stale counts, so the comparison has to fail on them
    /// instead of accepting "the file changed".
    [Fact]
    public void AFileWhoseGeometryDiffersFromTheBuildIsNotReportedPersisted()
    {
        NavmeshPersistence mismatch = NavmeshPersist.Verify(SavedPlugin(), new NavmeshExpected(0x00240045, 4, 455, 528));

        Assert.False(mismatch.Persisted);
        Assert.Equal("readback_mismatch", mismatch.Code);
        Assert.Contains("455 vertices", mismatch.Message);
        Assert.Equal(528, mismatch.Expected!.Triangles);
        Assert.Equal(529, mismatch.ReadBack!.Triangles);
    }

    /// Regression: the editor can be left holding a build for a cell that its Save never wrote, so a plugin
    /// without that cell's NAVM records must not be reported as persisted.
    [Fact]
    public void ACellTheSavedPluginDoesNotCarryIsNotReportedPersisted()
    {
        NavmeshPersistence missing = NavmeshPersist.Verify(SavedPlugin(), new NavmeshExpected(0x00172297, 3, 42, 34));

        Assert.False(missing.Persisted);
        Assert.Equal("cell_missing", missing.Code);
        Assert.Contains("0x00172297", missing.Message);
    }

    /// A save the editor never performed must be named as nothing on disk rather than assumed.
    [Fact]
    public void APluginThatIsNotOnDiskIsNotReportedPersisted()
    {
        string absent = Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotSaved.esp");

        NavmeshPersistence nothing = NavmeshPersist.Verify(absent, new NavmeshExpected(0x00240045, 4, 455, 529));

        Assert.False(nothing.Persisted);
        Assert.Equal("readback_unavailable", nothing.Code);
        Assert.Null(nothing.ReadBack);
        Assert.Empty(NavmeshPersist.Snapshot(Path.Combine(AppContext.BaseDirectory, "no-such-data")));
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
