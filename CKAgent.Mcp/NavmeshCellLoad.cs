using System.Diagnostics;
using System.Text.Json;

namespace Garden.CkAgent;

// Constraint: exterior addressing is its own subject, so the load path lives beside the tool instead of inside it.
public sealed partial class NavmeshTools
{
    // Constraint: an audit must not stop on one cell, so a load or read failure is that cell's own answer.
    // Constraint: the Cell View lists one worldspace at a time, so an exterior cell must be reached through its combo.
    private bool UseWorldSpace(string name, out string code, out string message)
    {
        code = "ok";
        message = "";

        string wanted = name.Trim();
        if (wanted.Length == 0) return true;

        string? combo = FindCellViewCombo(out code, out message);
        if (combo is null) return false;

        if (!BlockedEditor.TryCall(_plugin, $"ui.items {combo}", out string items, out code, out message))
            return false;

        int index = -1;
        try
        {
            using JsonDocument document = JsonDocument.Parse(items);
            JsonElement rows = Member(document.RootElement, "rows");
            if (rows.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (JsonElement row in rows.EnumerateArray())
                {
                    string text = row.ValueKind == JsonValueKind.String ? row.GetString() ?? "" : row.ToString();
                    if (text.Trim().Equals(wanted, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
                    i++;
                }
            }
        }
        catch (JsonException)
        {
            code = "bad_reply";
            message = "the worldspace combo answered without the list this tool reads";
            return false;
        }

        if (index < 0)
        {
            code = "no_worldspace";
            message = $"the Cell View has no worldspace named {wanted}";
            return false;
        }

        if (!BlockedEditor.TryCall(_plugin, $"ui.select {combo} {index}", out _, out code, out message))
            return false;

        // The list is repopulated by the editor after the notification, so the next read must wait for it.
        Thread.Sleep(WorldSpaceSettleMs);
        return true;
    }

    private string? FindCellViewCombo(out string code, out string message)
    {
        string? view = FindCellViewWindow(out code, out message);
        if (view is null) return null;

        if (!BlockedEditor.TryCall(_plugin, $"ui.tree {view}", out string tree, out code, out message)) return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(tree);
            JsonElement children = Member(document.RootElement, "children");
            if (children.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in children.EnumerateArray())
                {
                    if (!StringMember(child, "class").Equals("ComboBox", StringComparison.Ordinal)) continue;
                    return StringMember(child, "hwnd");
                }
            }
        }
        catch (JsonException)
        {
            code = "bad_reply";
            message = "the Cell View tree answered without children";
            return null;
        }

        code = "no_worldspace_combo";
        message = "the Cell View has no worldspace combo, so only interior cells can be named";
        return null;
    }

    private string? FindCellViewWindow(out string code, out string message)
    {
        code = "ok";
        message = "";

        if (!BlockedEditor.TryCall(_plugin, "ui.windows", out string windows, out code, out message)) return null;

        string? view = null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(windows);
            // Constraint: ui.windows answers a bare array, so the list is the root unless it is wrapped.
            JsonElement list = document.RootElement.ValueKind == JsonValueKind.Array
                ? document.RootElement
                : Member(document.RootElement, "windows");
            if (list.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement window in list.EnumerateArray())
                {
                    // The editor pads control and window titles, so identity is the trimmed text.
                    if (!StringMember(window, "title").Trim().Equals("Cell View", StringComparison.OrdinalIgnoreCase)) continue;
                    if (Member(window, "visible").ValueKind != JsonValueKind.True) continue;
                    view = StringMember(window, "hwnd");
                    break;
                }
            }
        }
        catch (JsonException)
        {
            code = "bad_reply";
            message = "ui.windows answered without the window list this tool reads";
            return null;
        }

        if (string.IsNullOrEmpty(view))
        {
            code = "no_cell_view";
            message = "no visible Cell View window was found, so a worldspace cannot be selected";
            return null;
        }

        return view;
    }

    // Constraint: a cell load answers before the render window settles, so the row must be proven loaded before reading.
    private bool LoadCell(CellRow row, out string code, out string message, string worldspace = "")
    {
        code = "ok";
        message = "";

        if (!UseWorldSpace(string.IsNullOrEmpty(worldspace) ? _worldspace : worldspace, out code, out message))
            return false;

        // Constraint: several exterior cells share an EditorID, so a row is opened by its own list index.
        if (row.Row >= 0)
        {
            string? list = FindCellViewList(out code, out message);
            if (list is null) return false;

            if (!BlockedEditor.TryCall(_plugin, $"ui.notify {list} dblclk {row.Row}", out _, out code, out message))
                return false;
        }
        else if (!BlockedEditor.TryCall(_plugin, $"ui.cellload {row.CleanId}", out _, out code, out message))
        {
            return false;
        }
        // Constraint: a loaded editor can take half a minute to put an exterior cell on screen, so the window is time.
        string lastError = "";
        Stopwatch loadClock = Stopwatch.StartNew();
        while (loadClock.ElapsedMilliseconds < LoadWindowMs)
        {
            Thread.Sleep(LoadPollMs);

            // Constraint: a read fails while the editor is mid-load, so a failed read waits instead of ending the proof.
            if (!BlockedEditor.TryCall(_plugin, "nav.state", out string state, out code, out message))
            {
                lastError = code + ": " + message;
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(state);
                JsonElement cell = Member(document.RootElement, "cell");
                string loadedId = StringMember(cell, "editorId");
                string loadedForm = StringMember(cell, "formId");

                bool same = row.FormId.Length > 0 && loadedForm.Length > 0
                    ? loadedForm.Equals(row.FormId, StringComparison.OrdinalIgnoreCase)
                    : loadedId.Trim().Equals(row.EditorId.Trim(), StringComparison.OrdinalIgnoreCase);

                if (same) return true;
            }
            catch (JsonException)
            {
                code = "bad_reply";
                message = "nav.state answered without the cell this sweep reads";
                return false;
            }
        }

        code = "load_timeout";
        message = $"after {LoadWindowMs / 1000}s " + $"{row.CleanId}" + (row.FormId.Length > 0 ? $" ({row.FormId})" : "") +
                  " did not become the loaded cell, so its geometry was not read" +
                  (lastError.Length == 0 ? "" : "; last read: " + lastError);
        return false;
    }

    // Constraint: the row index only means something inside the list the Cell View shows, so that list is resolved.
    private string? FindCellViewList(out string code, out string message)
    {
        code = "ok";
        message = "";

        string? view = FindCellViewWindow(out code, out message);
        if (view is null) return null;

        if (!BlockedEditor.TryCall(_plugin, $"ui.tree {view}", out string tree, out code, out message)) return null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(tree);
            JsonElement children = Member(document.RootElement, "children");
            if (children.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement child in children.EnumerateArray())
                {
                    if (!StringMember(child, "class").Equals("SysListView32", StringComparison.Ordinal)) continue;
                    return StringMember(child, "hwnd");
                }
            }
        }
        catch (JsonException)
        {
            code = "bad_reply";
            message = "the Cell View tree answered without children";
            return null;
        }

        code = "no_cell_list";
        message = "the Cell View has no list view, so a row index cannot be opened";
        return null;
    }
}
