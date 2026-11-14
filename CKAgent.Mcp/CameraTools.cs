using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Garden.CkAgent;

public sealed record CameraResult(
    string Status,
    string Code,
    string Message,
    string? Payload);

[McpServerToolType]
public sealed class CameraTools
{
    private readonly IPluginCaller _plugin;

    public CameraTools(IPluginCaller plugin) => _plugin = plugin;

    [McpServerTool(Name = "ck.frame"),
     Description("Load a cell and put the camera in front of an object, so a reference can be inspected " +
                 "without navigating by hand. cell is an EditorID or display name (skipped when omitted, and " +
                 "not reloaded when it names the cell already open, so framing several objects in one cell costs " +
                 "one load); reference matches a reference EditorID, its base object " +
                 "EditorID, or an 8-digit form id; distance is chosen from the reference's own bounds (2.4 " +
                 "bounding-sphere radii back, 30 degrees above the box centre) and may be overridden, and yaw " +
                 "picks which side to stand on. Returns the matched object, its position, the bounds it was " +
                 "framed from and whether they came from the reference, the camera pose and whether the " +
                 "object sits ahead along the view row, plus the cell the render window actually holds, so a " +
                 "load that landed elsewhere is visible in the same reply. Needs a loaded cell and CKPE with " +
                 "CKAgent.dll installed.")]
    public CameraResult Frame(string reference, string? cell = null, double? distance = null, double? yaw = null)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return new CameraResult("error", "invalid_argument",
                "reference is required: a reference EditorID, a base object EditorID, or a form id", null);
        }

        string? requestedCell = null;
        bool reused = false;

        if (!string.IsNullOrWhiteSpace(cell))
        {
            requestedCell = cell.Trim();
            CellWatch.OpenCell? before = CellWatch.Read(_plugin);

            // Constraint: a cell that is already open must not be opened again, so the load and its wait are skipped.
            if (CellWatch.IsOpen(before, requestedCell))
            {
                reused = true;
            }
            else
            {
                // Constraint: an already loaded cell would satisfy the wait for another one, so the handle must change.
                if (!BlockedEditor.TryCall(_plugin, $"ui.cellload {requestedCell}", out string opened, out string openCode, out string openMessage))
                    return new CameraResult("error", openCode, openMessage, opened);

                string? matched = CellWatch.Matched(opened);
                CellWatch.OpenCell? loaded = CellWatch.WaitForChange(_plugin, before, requestedCell, matched);

                if (loaded == null)
                {
                    return new CameraResult("error", "cell_not_loaded",
                        $"the Cell View accepted {requestedCell}, but the render window still holds " +
                        $"{CellWatch.Describe(before)} after 15 s",
                        CellWatch.WithOpened(opened, requestedCell, before));
                }
            }

        }

        // Constraint: a yaw needs the distance word in front of it, and 0 is how it asks for the bounds fit.
        if (yaw != null && distance == null) distance = 0;

        string command = $"frame {reference.Trim()}";
        if (distance != null) command += " " + PluginClient.Format(distance.Value);
        if (yaw != null) command += " " + PluginClient.Format(yaw.Value);

        if (!BlockedEditor.TryCall(_plugin, command, out string payload, out string code, out string message))
            return new CameraResult("error", code, message, null);

        CellWatch.OpenCell? framed = CellWatch.Read(_plugin);
        string openedIn = framed?.EditorId ?? framed?.FormId ?? "an unreadable cell";
        string note = reused ? " (the cell was already loaded)" : string.Empty;

        return new CameraResult("ok", "ok", $"{command} in {openedIn}{note}",
            CellWatch.WithOpened(payload, requestedCell, framed));
    }

    [McpServerTool(Name = "ck.aim"),
     Description("Rotate the camera until a named reference in the loaded cell is straight ahead, keeping " +
                 "the camera where it stands so the object is seen from the view already chosen. reference " +
                 "matches a reference EditorID, its base object EditorID, or an 8-digit form id, and may be a " +
                 "comma-joined list aimed in order from the same camera position. Optional distance backs the " +
                 "camera away to that standoff first and is refused with a list, because it moves the camera; " +
                 "the tilt comes from where that leaves it. level holds the horizon instead of centring: the " +
                 "camera turns its heading onto the target and writes pitch 0, so aim is below 1 unless the " +
                 "target is level with it, and yawAim is what reports the heading reached the target. A target " +
                 "straight above or below keeps the current heading and tilts to the pole, or keeps the " +
                 "horizon when level. Reports the yaw and pitch it wrote and the achieved aim, measured " +
                 "against the scene camera's own view row rather than the matrix just written, and the cell the " +
                 "render window holds, so an aim in a cell other than the one expected is visible in the reply. " +
                 "Needs a loaded cell and CKPE with CKAgent.dll installed.")]
    public CameraResult Aim(string reference, double? distance = null, bool level = false)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return new CameraResult("error", "invalid_argument",
                "reference is required: a reference EditorID, a base object EditorID, or a form id", null);
        }

        // Constraint: the plugin reads level as a word of its own after the standoff, so the order matters.
        string command = $"aim {reference.Trim()}";
        if (distance != null) command += " " + PluginClient.Format(distance.Value);
        if (level) command += " level";

        if (!BlockedEditor.TryCall(_plugin, command, out string payload, out string code, out string message))
            return new CameraResult("error", code, message, null);

        return Placed(command, payload);
    }

    // Constraint: a reply must name the cell it acted in, so a move in the wrong one shows up in the same reply.
    private CameraResult Placed(string command, string payload)
    {
        CellWatch.OpenCell? cell = CellWatch.Read(_plugin);
        string place = cell?.EditorId ?? cell?.FormId ?? "an unreadable cell";
        return new CameraResult("ok", "ok", $"{command} in {place}", CellWatch.WithOpened(payload, null, cell));
    }

    [McpServerTool(Name = "ck.camera"),
     Description("Drive the render window camera through the CKAgent plugin. mode=Get returns the " +
                 "scene camera's position, rotation matrix and scale; mode=Set places it at x/y/z with " +
                 "optional yaw/pitch (radians, +pitch tilts up); mode=Move steps by x/y/z in the camera's " +
                 "own axes, so +y is its right and not world +Y; mode=Lookat aims at the world point " +
                 "x/y/z, calling the editor's LookAt and then writing the pose it computes when that " +
                 "does not verify, and reports which path aimed it as via plus the achieved aim; " +
                 "mode=Orbit circles a target at the given angle (radians, angle 0 looks along +X), optionally " +
                 "tilted, where the target is either the world point x/y/z or a named reference whose bounds " +
                 "centre is circled, and the distance defaults to the same bounds fit ck.frame uses; it sets " +
                 "the editor's own pivot to that point so its own " +
                 "orbit and zoom continue from there, and reports the reference it used, the pivot it read back plus the radius " +
                 "and aim it achieved. mode=Rotate turns the camera by the step x/y/z in radians through " +
                 "the editor's own RotateBy, and reports the yaw and pitch it achieved with their deltas, so " +
                 "an orbit step can be compared against the editor's rotation directly; a step that leaves " +
                 "the view unchanged fails as rotate_noop. mode=Lens reads the field of view from the scene camera's own " +
                 "frustum and reports it in degrees along with the near and far planes, the zoom measured " +
                 "against the render window's own lens, the raw frustum and the view mode; passing fov " +
                 "sets a vertical field of view in degrees, and passing zoom scales the field of view by a " +
                 "factor, where above 1 zooms in; both report what was achieved rather than what was asked " +
                 "for, and the editor's own Zoom call is not used because it leaves the frustum unchanged. " +
                 "Every mode returns the resulting position " +
                 "and matrix together with the cell the render window holds, the way ck.frame does. Needs a " +
                 "loaded cell and reports camera_transform_unverified when the scene " +
                 "camera has no usable transform yet. " +
                 "Requires CKPE plus CKAgent.dll in <game>\\CKPEPlugins\\.")]
    public CameraResult Camera(string mode = "get", double? x = null, double? y = null, double? z = null,
                               double? yaw = null, double? pitch = null, double? distance = null,
                               double? fov = null, double? zoom = null, string? reference = null)
    {
        string command;

        switch (mode.Trim().ToLowerInvariant())
        {
            case "get":
                command = "camera get";
                break;

            case "set":
                if (x == null || y == null || z == null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "mode=Set needs x, y and z", null);
                }

                if ((yaw == null) != (pitch == null))
                {
                    return new CameraResult("error", "invalid_argument",
                        "yaw and pitch must be given together", null);
                }

                command = yaw != null
                    ? $"camera set {PluginClient.Format(x.Value)} {PluginClient.Format(y.Value)} {PluginClient.Format(z.Value)} " +
                      $"{PluginClient.Format(yaw.Value)} {PluginClient.Format(pitch.Value)}"
                    : $"camera set {PluginClient.Format(x.Value)} {PluginClient.Format(y.Value)} {PluginClient.Format(z.Value)}";
                break;

            case "rotate":
            {
                if (x == null || y == null || z == null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "mode=Rotate needs x, y and z: the step in radians around each of the camera's own axes", null);
                }

                if (yaw != null || pitch != null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "yaw and pitch belong to mode=Set; mode=Rotate takes the step in x, y and z", null);
                }

                command = $"camera rotate {PluginClient.Format(x.Value)} {PluginClient.Format(y.Value)} {PluginClient.Format(z.Value)}";
                break;
            }

            case "lens":
            {
                if (fov != null && zoom != null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "fov and zoom change the same lens; pass one", null);
                }

                command = "camera lens";
                if (fov != null) command += " fov " + PluginClient.Format(fov.Value);
                else if (zoom != null) command += " zoom " + PluginClient.Format(zoom.Value);
                break;
            }

            case "move":
            {
                if (x == null || y == null || z == null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "mode=Move needs x, y and z: a step along the camera's own axes", null);
                }

                if (yaw != null || pitch != null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "yaw and pitch belong to mode=Set; mode=Move only takes a local step", null);
                }

                command = $"camera move {PluginClient.Format(x.Value)} {PluginClient.Format(y.Value)} {PluginClient.Format(z.Value)}";
                break;
            }

            case "lookat":
            {
                if (x == null || y == null || z == null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "mode=Lookat needs x, y and z: the world point to aim at", null);
                }

                if (yaw != null || pitch != null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "yaw and pitch belong to mode=Set; mode=Lookat derives the rotation from the target", null);
                }

                command = $"camera lookat {PluginClient.Format(x.Value)} {PluginClient.Format(y.Value)} {PluginClient.Format(z.Value)}";
                break;
            }

            case "orbit":
            {
                if (yaw == null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "mode=Orbit needs yaw: the angle around the target, in radians", null);
                }

                if (reference != null)
                {
                    if (x != null || y != null || z != null)
                    {
                        return new CameraResult("error", "invalid_argument",
                            "mode=Orbit takes a reference or x/y/z, not both", null);
                    }

                    if (distance != null && !(distance > 0))
                    {
                        return new CameraResult("error", "invalid_argument",
                            "mode=Orbit needs a positive distance when it is given", null);
                    }

                    // Constraint: the plugin needs the radius word before the angle, and 0 asks for the bounds fit.
                    string radius = distance != null ? PluginClient.Format(distance.Value) : "0";
                    command = $"orbit {reference.Trim()} {radius} {PluginClient.Format(yaw.Value)}" +
                              (pitch != null ? $" {PluginClient.Format(pitch.Value)}" : string.Empty);
                    break;
                }

                if (x == null || y == null || z == null)
                {
                    return new CameraResult("error", "invalid_argument",
                        "mode=Orbit needs a reference, or x, y and z: the point to circle", null);
                }

                if (distance == null || !(distance > 0))
                {
                    return new CameraResult("error", "invalid_argument",
                        "mode=Orbit needs a positive distance: the radius to keep", null);
                }

                command = $"orbit {PluginClient.Format(x.Value)} {PluginClient.Format(y.Value)} {PluginClient.Format(z.Value)} " +
                          $"{PluginClient.Format(distance.Value)} {PluginClient.Format(yaw.Value)}" +
                          (pitch != null ? $" {PluginClient.Format(pitch.Value)}" : string.Empty);
                break;
            }

            default:
                return new CameraResult("error", "invalid_argument",
                    "mode must be get, set, move, lookat, orbit, rotate or lens", null);
        }

        if (!BlockedEditor.TryCall(_plugin, command, out string payload, out string code, out string message))
            return new CameraResult("error", code, message, null);

        return Placed(command, payload);
    }
}
