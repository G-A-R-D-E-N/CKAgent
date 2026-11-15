using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Garden.CkAgent;

// Constraint: placement runs the editor's own edit commands, so this tool never writes cell data itself.
[McpServerToolType]
public sealed class PlaceTools
{
    private readonly PluginClient _plugin;

    public PlaceTools(PluginClient plugin)
    {
        _plugin = plugin;
    }

    [McpServerTool(Name = "ck.place"),
     Description("Place references by running the Creation Kit's own Edit commands on the loaded cell. " +
                 "mode=commands lists the placement commands this build offers (Duplicate, Paste Render, " +
                 "Paste in Place, Copy Render) with their ids and enabled state, so nothing is addressed by " +
                 "a build-specific id. mode=duplicate runs Edit > Duplicate on the current render-window " +
                 "selection and reports the cell's reference count before and after, failing with no_change " +
                 "when the count did not move - a greyed Duplicate means nothing is selected, which is " +
                 "reported as command_disabled instead of a silent no-op. Placing a brand-new base object " +
                 "still needs the Object Window drag, which the editor only accepts as a real drag-and-drop; " +
                 "duplicate and paste are the menu-addressable ways to add a reference. The cell must be " +
                 "writable: set an active plugin with ui.dataload <plugin> active, or the editor cannot " +
                 "check the cell out. mode=duplicate changes cell data and is refused without confirm=true.")]
    public NavmeshResult Place(string mode = "commands", bool confirm = false)
    {
        switch ((mode ?? "").Trim().ToLowerInvariant())
        {
            case "commands":
                return Post("place.commands");

            case "duplicate":
                if (!confirm)
                    return new NavmeshResult("error", "confirmation_required",
                        "mode=duplicate adds a reference to the loaded cell; repeat with confirm=true when " +
                        "that is intended", null, null, null);

                return Post("place.duplicate confirm");

            default:
                return new NavmeshResult("error", "invalid_argument",
                    "mode must be commands or duplicate", null, null, null);
        }
    }

    private NavmeshResult Post(string command)
    {
        if (!BlockedEditor.TryCall(_plugin, command, out string payload, out string code, out string message))
            return new NavmeshResult("error", code, message, null, null, null);

        return new NavmeshResult("ok", "ok", command, payload, null, null);
    }
}
