// Constraint: driving a generator dialog is its own job - post, wait for the window, press it, wait for the build.
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Server;

namespace Garden.CkAgent;

public sealed partial class NavmeshTools
{
    // Constraint: a Recast build is finished long after its dialog closes, so a post-and-poll is the only honest wait.
    private const int DefaultGenerateWaitSeconds = 240;
    private const int MaxGenerateWaitSeconds = 900;
    private const int GeneratePollMs = 5000;

    // Constraint: the counts must come from the loaded cell itself, so every wait re-reads nav.state rather than a cache.
    private bool TryGeometry(out int forms, out int vertices, out int triangles, out string code, out string message)
    {
        forms = 0;
        vertices = 0;
        triangles = 0;

        if (!BlockedEditor.TryCall(_plugin, "nav.state", out string state, out code, out message))
            return false;

        try
        {
            using JsonDocument document = JsonDocument.Parse(state);
            JsonElement coverage = Member(Member(document.RootElement, "navmesh"), "coverage");
            forms = Number(Member(document.RootElement, "navmesh"), "navmeshForms");
            vertices = Number(coverage, "vertices");
            triangles = Number(coverage, "triangles");
            return true;
        }
        catch (JsonException)
        {
            code = "bad_reply";
            message = "nav.state answered without the JSON this tool reads";
            return false;
        }
    }

    private string GenerationDialogTitle()
    {
        foreach (string title in new[] { "Recast Navmesh Generation", "Havok Navmesh Generation",
                                         "Advanced NavMesh Generation" })
        {
            if (ModalByTitle(title).Length != 0)
                return title;
        }

        return "Recast Navmesh Generation";
    }

    private static string Caption(string kind) => kind switch
    {
        "havok" => "Havok-Based Generation",
        "advanced" => "Advanced",
        _ => "Recast-Based Generation",
    };

    private string WaitForModal(string title, int budgetMs)
    {
        int elapsed = 0;
        while (elapsed < budgetMs)
        {
            string hwnd = ModalByTitle(title);
            if (hwnd.Length != 0)
                return hwnd;

            Thread.Sleep(500);
            elapsed += 500;
        }

        return "";
    }

    // Constraint: the dialog handle is never cached, because the editor rebuilds these windows per run.
    private string ModalByTitle(string title)
    {
        if (!BlockedEditor.TryCall(_plugin, "ui.modals", out string modals, out _, out _))
            return "";

        try
        {
            using JsonDocument document = JsonDocument.Parse(modals);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return "";

            foreach (JsonElement modal in document.RootElement.EnumerateArray())
            {
                if (!StringMember(modal, "title").Equals(title, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (Member(modal, "visible").ValueKind != JsonValueKind.True)
                    continue;

                return StringMember(modal, "hwnd");
            }
        }
        catch (JsonException)
        {
            return "";
        }

        return "";
    }

    private string PostRaw(string command)
    {
        if (!BlockedEditor.TryCall(_plugin, command, out string payload, out string code, out string message))
            return JsonSerializer.Serialize(new { error = code, message }, EnvelopeJson);

        return payload;
    }

    // Constraint: generation answers its dialog and returns while the build is still running, so the wait is the evidence.
    private NavmeshResult Generate(string kind, int waitSeconds)
    {
        if (!TryGeometry(out int formsBefore, out int verticesBefore, out int trianglesBefore,
                         out string code, out string message))
            return new NavmeshResult("error", code, message, null, null, null);

        // Constraint: nav.run posts the caption alone, because the plugin's own probe can run before the dialog exists.
        string posted = PostRaw($"nav.run {Caption(kind)} confirm");
        string dialog = WaitForModal(GenerationDialogTitle(), 15000);

        if (posted.Contains("\"error\"", StringComparison.Ordinal) || dialog.Length == 0)
            return new NavmeshResult("error", "generation_dialog_missing",
                $"the {kind} command was posted but no generation dialog appeared within 15s, so nothing is " +
                "building; a command this build has disabled answers this way - read mode=commands for its " +
                "enabled state, then retry", posted, _paths.CkpeLogPath, null);

        string accepted = PostRaw($"ui.click {dialog} OK");
        if (accepted.Contains("\"error\"", StringComparison.Ordinal))
            return new NavmeshResult("error", "generation_refused",
                $"the {kind} dialog appeared but its OK could not be pressed, so nothing is building",
                accepted, _paths.CkpeLogPath, null);

        int wait = waitSeconds <= 0 ? DefaultGenerateWaitSeconds : Math.Min(waitSeconds, MaxGenerateWaitSeconds);
        int trianglesAfter = trianglesBefore;
        int verticesAfter = verticesBefore;
        int formsAfter = formsBefore;

        // Constraint: a build empties the arrays before it writes them, so a settled count is the only proof it finished.
        int previous = int.MinValue;
        int sameReadings = 0;
        int busyReads = 0;
        string lastError = "";
        bool built = false;

        // Constraint: the clock is wall time, because a busy read spends seconds the sleep count never sees.
        Stopwatch clock = Stopwatch.StartNew();
        // Constraint: the wait window is the only limit on a build, because the pipe goes quiet while the editor
        // builds and every earlier "it stopped answering" guard ended builds that were still running.
        while (clock.ElapsedMilliseconds < wait * 1000L && !built)
        {
            Thread.Sleep(GeneratePollMs);

            if (!TryGeometry(out formsAfter, out verticesAfter, out trianglesAfter, out code, out message))
            {
                busyReads++;
                lastError = code + ": " + message;
                continue;
            }

            sameReadings = trianglesAfter == previous ? sameReadings + 1 : 0;
            previous = trianglesAfter;
            built = trianglesAfter > 0 && sameReadings >= 2;
        }

        int elapsed = (int)Math.Min(clock.ElapsedMilliseconds, int.MaxValue);

        string envelope = JsonSerializer.Serialize(new
        {
            kind,
            before = new { forms = formsBefore, vertices = verticesBefore, triangles = trianglesBefore },
            after = new { forms = formsAfter, vertices = verticesAfter, triangles = trianglesAfter },
            waitedMs = elapsed,
            busyReads,
            lastReadError = lastError.Length == 0 ? null : lastError,
            built = trianglesAfter > 0,
            generation = posted,
            accepted,
        }, EnvelopeJson);

        if (trianglesAfter == 0)
            return new NavmeshResult("error", "generation_incomplete",
                $"the {kind} build posted and its dialog was pressed, but the cell still reports no triangles " +
                $"after waiting {elapsed / 1000}s of its {wait}s window ({busyReads} reads failed while the editor worked" +
                (lastError.Length == 0 ? "" : ", last: " + lastError) +
                "); a cell with no walkable geometry builds nothing, so check the cell before treating this as a failure",
                envelope, _paths.CkpeLogPath, null);

        return new NavmeshResult("ok", "generated",
            $"{kind} built {trianglesAfter} triangles and {verticesAfter} vertices in {elapsed / 1000}s " +
            $"(was {trianglesBefore} triangles)", envelope, _paths.CkpeLogPath, null);
    }
}
