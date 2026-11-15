using System.IO.Compression;

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

        Assert.All(meshes, mesh => Assert.NotNull(mesh.VertexDigest));
        Assert.All(meshes, mesh => Assert.NotNull(mesh.TriangleDigest));
        Assert.Equal(4, meshes.Select(mesh => mesh.VertexDigest).Distinct().Count());

        NavmeshExpected expected = NavmeshPersist.Expect(report, 0x00240045);
        Assert.Equal(4, expected.Records.Count);
        Assert.Equal(455, expected.Records.Sum(record => record.Vertices));
        Assert.Equal(529, expected.Records.Sum(record => record.Triangles));

        NavmeshPersistence persisted = NavmeshPersist.Verify(SavedPlugin(), expected);

        Assert.True(persisted.Persisted);
        Assert.Equal("persisted", persisted.Code);
        Assert.Equal(45653L, persisted.Bytes!.Value);
        Assert.NotNull(persisted.ReadBack);
        Assert.Equal(4, persisted.ReadBack!.Forms);
        Assert.Equal(455, persisted.ReadBack.Vertices);
        Assert.Equal(529, persisted.ReadBack.Triangles);
    }

    /// The digest is the contract between the plugin and this reader, so the vector is pinned here as it is in
    /// NavmeshDigest.h; a drift on either side has to fail a check rather than a save verdict.
    [Fact]
    public void TheGeometryDigestIsThePinnedFnv1aVector()
    {
        Assert.Equal("e71fa2190541574b", NavmeshPersist.Digest("abc"u8));
        Assert.Equal("cbf29ce484222325", NavmeshPersist.Digest(ReadOnlySpan<byte>.Empty));
    }

    /// Regression: a triangle index the file holds but the editor did not build changes no count, so a totals
    /// comparison read it as delivered. The verdict has to name the record whose indices differ.
    [Fact]
    public void ATamperedTriangleIndexIsReportedAsATriangleIndexMismatchForThatRecord()
    {
        NavmeshExpected expected = NavmeshPersist.Expect(NavmeshPersist.Read(SavedPlugin()), 0x00240045);

        string tampered = Tamper(0x01000FA3, payload =>
        {
            int vertices = BitConverter.ToInt32(payload, NvnmAt(payload) + 16);
            int trianglesAt = TriangleIndicesAt(payload);
            ushort index = BitConverter.ToUInt16(payload, trianglesAt + 2);
            BitConverter.GetBytes((ushort)((index + 1) % vertices)).CopyTo(payload, trianglesAt + 2);
        });

        try
        {
            NavmeshPersistence verdict = NavmeshPersist.Verify(tampered, expected);

            Assert.False(verdict.Persisted);
            Assert.Equal("triangle_index_mismatch", verdict.Code);
            Assert.Contains("0x01000FA3", verdict.Message);
            Assert.DoesNotContain("0x01000FA4", verdict.Message);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(tampered)!, true);
        }
    }

    /// Regression: moving a vertex keeps every count, so only the positions themselves can catch it.
    [Fact]
    public void AShiftedVertexIsReportedAsAVertexMismatchForThatRecord()
    {
        NavmeshExpected expected = NavmeshPersist.Expect(NavmeshPersist.Read(SavedPlugin()), 0x00240045);

        string tampered = Tamper(0x01000FA5, payload =>
        {
            int vertexAt = VertexAt(payload);
            float x = BitConverter.ToSingle(payload, vertexAt);
            BitConverter.GetBytes(x + 0.5f).CopyTo(payload, vertexAt);
        });

        try
        {
            NavmeshPersistence verdict = NavmeshPersist.Verify(tampered, expected);

            Assert.False(verdict.Persisted);
            Assert.Equal("vertex_mismatch", verdict.Code);
            Assert.Contains("0x01000FA5", verdict.Message);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(tampered)!, true);
        }
    }

    /// A record the editor built but the file does not carry is short, and the verdict names which one.
    [Fact]
    public void ARecordTheFileDoesNotCarryIsReportedAsGeometryShort()
    {
        NavmeshExpected original = NavmeshPersist.Expect(NavmeshPersist.Read(SavedPlugin()), 0x00240045);
        var grown = new NavmeshExpected(original.CellFormId, original.Records
            .Append(new NavmeshExpectedRecord(0x01000FA7, 12, 10, "0", "0"))
            .ToList());

        NavmeshPersistence missing = NavmeshPersist.Verify(SavedPlugin(), grown);

        Assert.False(missing.Persisted);
        Assert.Equal("geometry_short", missing.Code);
        Assert.Contains("0x01000FA7", missing.Message);

        var shrunk = new NavmeshExpected(original.CellFormId, original.Records
            .Select(record => record.NavmFormId == 0x01000FA4 ? record with { Triangles = record.Triangles + 1 } : record)
            .ToList());

        NavmeshPersistence shorted = NavmeshPersist.Verify(SavedPlugin(), shrunk);

        Assert.False(shorted.Persisted);
        Assert.Equal("geometry_short", shorted.Code);
        Assert.Contains("0x01000FA4", shorted.Message);
    }

    /// Regression: the editor can be left holding a build for a cell that its Save never wrote, so a plugin
    /// without that cell's NAVM records must not be reported as persisted.
    [Fact]
    public void ACellTheSavedPluginDoesNotCarryIsNotReportedPersisted()
    {
        NavmeshPersistence missing = NavmeshPersist.Verify(SavedPlugin(),
            new NavmeshExpected(0x00172297, new[] { new NavmeshExpectedRecord(0x01000FA3, 242, 289, "0", "0") }));

        Assert.False(missing.Persisted);
        Assert.Equal("cell_missing", missing.Code);
        Assert.Contains("0x00172297", missing.Message);
    }

    /// A save the editor never performed must be named as nothing on disk rather than assumed.
    [Fact]
    public void APluginThatIsNotOnDiskIsNotReportedPersisted()
    {
        string absent = Path.Combine(AppContext.BaseDirectory, "Fixtures", "NotSaved.esp");

        NavmeshPersistence nothing = NavmeshPersist.Verify(absent,
            NavmeshPersist.Expect(NavmeshPersist.Read(SavedPlugin()), 0x00240045));

        Assert.False(nothing.Persisted);
        Assert.Equal("readback_unavailable", nothing.Code);
        Assert.Null(nothing.ReadBack);
        Assert.Empty(NavmeshPersist.Snapshot(Path.Combine(AppContext.BaseDirectory, "no-such-data")));
    }

    /// The cover path reaches its verdict through this function, so a cover run that finished over a file whose
    /// geometry differs reports the read-back's code instead of done. Driven with real read-backs, no editor.
    [Fact]
    public void ACoverRunCannotBeDoneOnAReadBackThatDoesNotMatch()
    {
        NavmeshExpected expected = NavmeshPersist.Expect(NavmeshPersist.Read(SavedPlugin()), 0x00240045);

        NavmeshPersistence persisted = NavmeshPersist.Verify(SavedPlugin(), expected);
        (string doneStatus, string doneCode, string doneMessage) =
            NavmeshTools.CoverVerdict(persisted, "Find Cover Edges finished in 4s");

        Assert.Equal("ok", doneStatus);
        Assert.Equal("saved", doneCode);
        Assert.Contains("saved and read back", doneMessage);

        string tampered = Tamper(0x01000FA6, payload =>
        {
            BitConverter.GetBytes((ushort)0xFFFF).CopyTo(payload, TriangleIndicesAt(payload));
        });

        try
        {
            NavmeshPersistence mismatched = NavmeshPersist.Verify(tampered, expected);
            (string failedStatus, string failedCode, string failedMessage) =
                NavmeshTools.CoverVerdict(mismatched, "Find Cover Edges finished in 4s");

            Assert.Equal("error", failedStatus);
            Assert.Equal("triangle_index_mismatch", failedCode);
            Assert.Contains("0x01000FA6", failedMessage);
            Assert.Contains("not persisted", failedMessage);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(tampered)!, true);
        }
    }

    /// Regression: the kit's Check NavMeshes writes nothing for a cell with no findings, so the report window held
    /// only this tool's CALL/RPLY lines and every clean apply was refused as verdict_mismatch. A silent report is a
    /// clean signal only when the plugin's own verdict agrees and the geometry digests the save compares exist.
    [Fact]
    public void ASilentCheckReportIsAcceptedOnlyWithThePluginVerdictAndGeometryDigests()
    {
        string[] wire =
        {
            "CALL 5 nav.apply recast confirm -> RPLY 5 OK {\"stage\":\"check\",\"pending\":true}",
            "CALL 6 nav.apply recast confirm -> RPLY 6 OK {\"stage\":\"check\",\"pending\":true}",
        };

        NavmeshVerdict silent = NavmeshVerdictParser.Parse(wire);
        Assert.Equal("unknown", silent.Code);

        NavmeshExpected geometry = NavmeshPersist.Expect(NavmeshPersist.Read(SavedPlugin()), 0x00240045);

        NavmeshCheckEvidence accepted = NavmeshCheckGate.Resolve("clean", silent, wire, false, geometry, "");
        Assert.True(accepted.Clean);
        Assert.Equal("clean", accepted.Code);
        Assert.True(accepted.Silent);
        Assert.True(accepted.Corroborated);
        Assert.Contains("no findings", accepted.Reason);

        NavmeshCheckEvidence uncorroborated = NavmeshCheckGate.Resolve("clean", silent, wire, false, null, "");
        Assert.False(uncorroborated.Clean);
        Assert.Equal("unknown", uncorroborated.Code);
        Assert.Contains("corroborate", uncorroborated.Reason);

        NavmeshCheckEvidence noReport = NavmeshCheckGate.Resolve(null, silent, wire, false, geometry, "");
        Assert.False(noReport.Clean);
        Assert.Equal("unknown", noReport.Code);
    }

    /// The silence resolution must not swallow a check that did report a finding.
    [Fact]
    public void ACheckReportWithAnErrorIsStillRefused()
    {
        string[] reported =
        {
            "Navmesh 01001740 Cell DmndRadio01, Triangle 3 is degenerate and has an invalid area",
        };

        NavmeshVerdict dirty = NavmeshVerdictParser.Parse(reported);
        Assert.Equal("errors", dirty.Code);

        NavmeshExpected geometry = NavmeshPersist.Expect(NavmeshPersist.Read(SavedPlugin()), 0x00240045);

        NavmeshCheckEvidence refused = NavmeshCheckGate.Resolve("errors", dirty, reported, false, geometry, "");
        Assert.False(refused.Clean);
        Assert.Equal("dirty", refused.Code);
        Assert.False(refused.Silent);
    }

    /// A plugin that calls a cell clean while the report carries an error is the mismatch the gate exists for.
    [Fact]
    public void APluginVerdictThatDisagreesWithTheParsedReportIsStillRefused()
    {
        string[] reported =
        {
            "Navmesh 01001740 Cell DmndRadio01, Triangle 3 is degenerate and has an invalid area",
        };

        NavmeshVerdict dirty = NavmeshVerdictParser.Parse(reported);
        NavmeshExpected geometry = NavmeshPersist.Expect(NavmeshPersist.Read(SavedPlugin()), 0x00240045);

        NavmeshCheckEvidence mismatch = NavmeshCheckGate.Resolve("clean", dirty, reported, false, geometry, "");
        Assert.False(mismatch.Clean);
        Assert.Equal("mismatch", mismatch.Code);
        Assert.Contains("clean", mismatch.Reason);
    }

    /// An accepted silent check still has to survive the read-back: a save whose file differs is never `saved`.
    [Fact]
    public void ADigestMismatchAfterASilentCleanCheckIsStillRefused()
    {
        string[] wire = { "CALL 6 nav.apply recast confirm -> RPLY 6 OK {\"stage\":\"check\"}" };
        NavmeshVerdict silent = NavmeshVerdictParser.Parse(wire);
        NavmeshExpected geometry = NavmeshPersist.Expect(NavmeshPersist.Read(SavedPlugin()), 0x00240045);

        NavmeshCheckEvidence accepted = NavmeshCheckGate.Resolve("clean", silent, wire, false, geometry, "");
        Assert.True(accepted.Clean);

        string tampered = Tamper(0x01000FA4, payload =>
        {
            int vertexAt = VertexAt(payload);
            float x = BitConverter.ToSingle(payload, vertexAt);
            BitConverter.GetBytes(x + 0.25f).CopyTo(payload, vertexAt);
        });

        try
        {
            NavmeshPersistence readBack = NavmeshPersist.Verify(tampered, geometry);
            Assert.False(readBack.Persisted);
            Assert.Equal("vertex_mismatch", readBack.Code);
            Assert.Contains("0x01000FA4", readBack.Message);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(tampered)!, true);
        }
    }

    /// Regression: mode=check answered `unknown` for the same silent report mode=apply now saves on, so telling a
    /// clean cell from an unread one needed the raw lines. Check resolves through the gate apply saves on.
    [Fact]
    public void ACheckResolvesASilentReportWithTheGateApplySavesOn()
    {
        string[] wire = { "CALL 5 nav.check -> RPLY 5 OK {\"stage\":\"done\",\"pending\":false}" };
        const string payload = "{\"stage\":\"done\",\"pending\":false,\"report\":{\"verdict\":\"clean\",\"errors\":0}}";
        NavmeshExpected geometry = NavmeshPersist.Expect(NavmeshPersist.Read(SavedPlugin()), 0x00240045);

        NavmeshCheckOutcome outcome = NavmeshCheckGate.ResolveReport(payload, wire, geometry, "");

        // The parse alone still sees nothing, which is why the gate, not the parser, decides.
        Assert.Equal("unknown", outcome.Verdict.Code);
        Assert.Equal("clean", outcome.Evidence.Code);
        Assert.True(outcome.Evidence.Clean);
        Assert.True(outcome.Evidence.Silent);
        Assert.True(outcome.Evidence.Corroborated);
        Assert.Contains("no findings", outcome.Evidence.Reason);

        NavmeshCheckOutcome noDigests = NavmeshCheckGate.ResolveReport(payload, wire,
            new NavmeshExpected(0x00240045, Array.Empty<NavmeshExpectedRecord>()), "");
        Assert.False(noDigests.Evidence.Clean);
        Assert.Equal("unknown", noDigests.Evidence.Code);
        Assert.Contains("corroborate", noDigests.Evidence.Reason);
    }

    /// Regression: a check that wrote a finding must not read clean in either mode, and silence alone never passes.
    [Fact]
    public void ACheckStillRefusesADirtyReportAndASilentOneWithNoVerdict()
    {
        string[] reported = { "Navmesh 01001740 Cell DmndRadio01, Triangle 3 is degenerate and has an invalid area" };
        NavmeshExpected geometry = NavmeshPersist.Expect(NavmeshPersist.Read(SavedPlugin()), 0x00240045);

        NavmeshCheckOutcome dirty = NavmeshCheckGate.ResolveReport("{\"report\":{\"verdict\":\"errors\"}}", reported,
            geometry, "");
        Assert.Equal("errors", dirty.Verdict.Code);
        Assert.False(dirty.Evidence.Clean);
        Assert.Equal("dirty", dirty.Evidence.Code);

        string[] wire = { "CALL 5 nav.check -> RPLY 5 OK {\"stage\":\"done\"}" };
        NavmeshCheckOutcome noVerdict = NavmeshCheckGate.ResolveReport("{\"stage\":\"done\"}", wire, geometry, "");
        Assert.False(noVerdict.Evidence.Clean);
        Assert.Equal("unknown", noVerdict.Evidence.Code);

        NavmeshCheckOutcome mismatch = NavmeshCheckGate.ResolveReport("{\"report\":{\"verdict\":\"clean\"}}",
            reported, geometry, "");
        Assert.Equal("mismatch", mismatch.Evidence.Code);
        Assert.False(mismatch.Evidence.Clean);
    }

    /// Regression: every apply put its mesh in a new NAVM form and left the replaced one behind, measured as six
    /// forms on DmndRadio01 after four applies while the file only carried the last mesh. The run drops the form the
    /// generation emptied, never a master record and never a form that still holds geometry.
    [Fact]
    public void RepeatedAppliesDoNotGrowTheCellsFormList()
    {
        NavmeshFormCount masterA = new(0x000C6F16, 0, 0);
        NavmeshFormCount masterB = new(0x00002307, 0, 0);

        // One apply in a session: the cell's own records plus the mesh, so nothing was superseded.
        Assert.Empty(NavmeshApplyPrune.Superseded(new[] { masterA, masterB, new NavmeshFormCount(0x01001EDC, 21, 14) }));

        // The second apply empties 01001EDC and builds 01002345 beside it.
        NavmeshFormCount[] duringSecond =
        {
            masterA, masterB, new NavmeshFormCount(0x01001EDC, 0, 0), new NavmeshFormCount(0x01002345, 21, 14),
        };

        IReadOnlyList<NavmeshFormCount> drop = NavmeshApplyPrune.Superseded(duringSecond);
        Assert.Single(drop);
        Assert.Equal(0x01001EDCu, drop[0].FormId);

        // Dropping it leaves the cell at its own records plus one mesh, so N applies do not grow the list.
        Assert.Equal(3, duringSecond.Length - drop.Count);

        // A third apply drops the second run's form, and still never a master record.
        IReadOnlyList<NavmeshFormCount> third = NavmeshApplyPrune.Superseded(new[]
        {
            masterA, masterB, new NavmeshFormCount(0x01002345, 0, 0), new NavmeshFormCount(0x01002AAA, 7, 5),
        });
        Assert.Single(third);
        Assert.Equal(0x01002345u, third[0].FormId);

        // Two live meshes mean nothing is provably the replaced one, so nothing is offered.
        Assert.Empty(NavmeshApplyPrune.Superseded(new[]
        {
            new NavmeshFormCount(0x01001EDC, 21, 14), new NavmeshFormCount(0x01002345, 7, 5),
        }));
    }

    /// Regression: the form table the prune reads comes from nav.state, so a reply without one must offer nothing
    /// rather than guess at ids that are not there.
    [Fact]
    public void ThePruneReadsOnlyTheNavmeshFormsTheStateReplyCarries()
    {
        const string state = "{\"navmesh\":{\"count\":2,\"forms\":[" +
                             "{\"formId\":\"000C6F16\",\"type\":29,\"navmesh\":false,\"vertices\":9,\"triangles\":9}," +
                             "{\"formId\":\"01001EDC\",\"type\":76,\"navmesh\":true,\"vertices\":21,\"triangles\":14}]}}";

        IReadOnlyList<NavmeshFormCount> forms = NavmeshApplyPrune.ReadForms(state);

        Assert.Single(forms);
        Assert.Equal(0x01001EDCu, forms[0].FormId);
        Assert.True(forms[0].HasGeometry);
        Assert.Empty(NavmeshApplyPrune.ReadForms("{\"cell\":{}}"));
        Assert.Empty(NavmeshApplyPrune.ReadForms(null));
    }

    /// The geometry offsets live inside the NVNM subrecord's own data, which starts after the subrecord's
    /// six-byte signature and length, so a tamper measured from the record header would rewrite the wrong bytes.
    private static int NvnmAt(byte[] payload)
    {
        for (int at = 0; at + 6 <= payload.Length; at++)
        {
            if (payload[at] != (byte)'N' || payload[at + 1] != (byte)'V' || payload[at + 2] != (byte)'N' ||
                payload[at + 3] != (byte)'M') continue;
            return at + 6;
        }

        throw new InvalidOperationException("the fixture record carries no NVNM subrecord");
    }

    private static int VertexAt(byte[] payload) => NvnmAt(payload) + 20;

    private static int TriangleIndicesAt(byte[] payload) =>
        NvnmAt(payload) + 20 + BitConverter.ToInt32(payload, NvnmAt(payload) + 16) * 12 + 4;

    /// Rewrites one NAVM record of the fixture with `edit` applied to its payload, stored uncompressed so the
    /// tampered bytes go in verbatim; the groups around it are widened by the same delta so the tree still walks.
    private static string Tamper(uint navmFormId, Action<byte[]> edit)
    {
        byte[] file = File.ReadAllBytes(SavedPlugin());
        var records = new List<int>();
        for (int at = 0; at + 24 <= file.Length; at++)
        {
            if (file[at] != (byte)'N' || file[at + 1] != (byte)'A' || file[at + 2] != (byte)'V' ||
                file[at + 3] != (byte)'M') continue;
            if (BitConverter.ToUInt32(file, at + 12) == navmFormId) records.Add(at);
        }

        Assert.Single(records);
        int header = records[0];
        uint flags = BitConverter.ToUInt32(file, header + 8);
        int size = (int)BitConverter.ToUInt32(file, header + 4);
        byte[] stored = file[(header + 24)..(header + 24 + size)];
        byte[] payload = (flags & 0x00040000) != 0 ? Inflate(stored) : stored;

        edit(payload);

        int delta = payload.Length - size;
        int recordEnd = header + 24 + size;
        byte[] rewritten = new byte[file.Length + delta];
        Array.Copy(file, 0, rewritten, 0, header);

        var recordHeader = new byte[24];
        Array.Copy(file, header, recordHeader, 0, 24);
        BitConverter.GetBytes((uint)payload.Length).CopyTo(recordHeader, 4);
        BitConverter.GetBytes(flags & ~0x00040000u).CopyTo(recordHeader, 8);
        recordHeader.CopyTo(rewritten, header);
        payload.CopyTo(rewritten, header + 24);
        Array.Copy(file, recordEnd, rewritten, recordEnd + delta, file.Length - recordEnd);

        // Constraint: a group's size covers its children, so every group the record sits in grows with it.
        for (int at = 0; at + 24 <= header; at++)
        {
            if (rewritten[at] != (byte)'G' || rewritten[at + 1] != (byte)'R' || rewritten[at + 2] != (byte)'U' ||
                rewritten[at + 3] != (byte)'P') continue;

            uint groupSize = BitConverter.ToUInt32(rewritten, at + 4);
            if (at + 24 + groupSize < recordEnd) continue;
            BitConverter.GetBytes(groupSize + delta).CopyTo(rewritten, at + 4);
        }

        string directory = Path.Combine(Path.GetTempPath(), "ckagent-tamper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "Tampered.esp");
        File.WriteAllBytes(target, rewritten);
        return target;
    }

    private static byte[] Inflate(byte[] stored)
    {
        using var input = new MemoryStream(stored, 4, stored.Length - 4, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        zlib.CopyTo(output);
        return output.ToArray();
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
