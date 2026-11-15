using System.Text.Json;

namespace Garden.CkAgent;

/// One entry of nav.state's form table: the form the cell lists and the geometry the editor holds for it.
public sealed record NavmeshFormCount(uint FormId, int Vertices, int Triangles)
{
    public bool HasGeometry => Vertices > 0 || Triangles > 0;

    public bool Empty => !HasGeometry;

    /// The load-order index of the plugin the form lives in, which is the high byte of its form id.
    public uint PluginIndex => FormId >> 24;
}

// Regression: every Recast pass puts the new mesh in a form of its own and empties the form it replaced, so a second
// apply in one session left that idle placeholder beside the mesh - measured as 6 forms for DmndRadio01 after four
// applies, where the file only ever carried the last mesh. The run drops the form the generation emptied, identified by
// the plugin that holds the replacement, so the cell ends at its own records plus the one mesh.
public static class NavmeshApplyPrune
{
    /// The empty placeholders the run may drop. A form still carrying geometry is never offered, and neither is a
    /// record a master owns: only an emptied form in the plugin the replacement lives in was demonstrably replaced.
    public static IReadOnlyList<NavmeshFormCount> Superseded(IReadOnlyList<NavmeshFormCount> forms)
    {
        var prune = new List<NavmeshFormCount>();
        NavmeshFormCount? generated = null;

        foreach (NavmeshFormCount form in forms)
        {
            if (!form.HasGeometry) continue;

            // Constraint: two live meshes mean nothing here is provably the replaced one, so nothing is dropped.
            if (generated is not null) return Array.Empty<NavmeshFormCount>();
            generated = form;
        }

        if (generated is null) return Array.Empty<NavmeshFormCount>();

        foreach (NavmeshFormCount form in forms)
        {
            if (!form.Empty) continue;
            if (form.FormId == 0) continue;
            if (form.PluginIndex != generated.PluginIndex) continue;

            prune.Add(form);
        }

        return prune;
    }

    /// nav.state's per-form counts, or an empty list when the reply carries no form table.
    public static IReadOnlyList<NavmeshFormCount> ReadForms(string? statePayload)
    {
        var forms = new List<NavmeshFormCount>();

        if (string.IsNullOrWhiteSpace(statePayload)) return forms;

        try
        {
            using JsonDocument document = JsonDocument.Parse(statePayload);

            if (!document.RootElement.TryGetProperty("navmesh", out JsonElement navmesh) ||
                navmesh.ValueKind != JsonValueKind.Object ||
                !navmesh.TryGetProperty("forms", out JsonElement list) ||
                list.ValueKind != JsonValueKind.Array)
                return forms;

            foreach (JsonElement entry in list.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;

                // Constraint: the list also holds non-navmesh entries, so only a navmesh form may become a candidate.
                if (!entry.TryGetProperty("navmesh", out JsonElement flag) || flag.ValueKind != JsonValueKind.True)
                    continue;

                string? id = entry.TryGetProperty("formId", out JsonElement formId) &&
                             formId.ValueKind == JsonValueKind.String
                    ? formId.GetString()
                    : null;

                if (!NavmeshPersist.TryParseFormId(id ?? "", out uint parsed)) continue;

                forms.Add(new NavmeshFormCount(parsed, Count(entry, "vertices"), Count(entry, "triangles")));
            }
        }
        catch (JsonException)
        {
            forms.Clear();
        }

        return forms;
    }

    private static int Count(JsonElement entry, string name) =>
        entry.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out int number)
            ? number
            : 0;
}
