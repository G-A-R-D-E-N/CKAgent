# CKAgent

Drive the Fallout 4 Creation Kit from an MCP client: open cells, move the render camera, point it at named
objects, read editor state, and run the editor's own menus — without a mouse.

Written for **CreationKit 1.11.246.0** (the CKPE build it is pinned to) on Windows. It refuses to touch
internals on any other build rather than guessing at offsets.

---

## How it fits together

```
MCP client ── stdio ──▶ ckagent        (MCP server, .NET 8)
                            │  named pipe \\.\pipe\ckagent
                            ▼
                        CKAgent.dll    (CKPE plugin, running inside CreationKit)
                            │  editor thread, in the same process
                            ▼
                        CreationKit 1.11.246.0
```

Two design choices explain most of the code:

- **Geometry comes from the loaded cell.** A reference is found through `TESObjectCELL::GetItems`, not by
  guessing coordinates, so framing works in any cell without a table of hand-measured positions.
- **Writes go through the editor's own methods.** The camera is posed with `SetPosition`/`SetRotation` on
  `BGSRenderWindowCamera`; the `NiCamera` inside it is a slave the editor overwrites every frame. Cells open
  through the Cell View's own double-click handler, delivered in-process on the editor thread.

---

## What works today

The bars are how much of a workflow an agent can finish **unaided** — not lines of code. `█` is earned, `░`
is not, and one block is 5%. A 90% row means "an agent can finish this end to end, with one known gap".

| Area | | What an agent can do | What is missing |
|---|---|---|---|
| Camera pose | `██████████████████░░` 90% | read position, rotation and scale; set position with yaw; step along the camera's own axes | pitch that actually tilts the view; orthographic / zoom control; field of view |
| Framing and aiming | `████████████████░░░░` 80% | stand in front of a named reference at a distance and yaw; turn the current camera onto a name and report how far off-level it sits | automatic distance and height that fit the object's bounds; tilt for objects above or below |
| Cell navigation | `███████████████████░` 95% | list and open any interior or exterior cell by name (36 865 Commonwealth cells), confirm what is loaded | opening by grid coordinates; the World Space X/Y/Go fields |
| Reference lookup | `███████████░░░░░░░░░` 55% | find a reference in the loaded cell by EditorID, base EditorID or form id; read its position; get named samples when a name misses | reading a reference's property sheet; listing base objects; persistent ids; picking in the render window |
| Editor UI | `█████████████████░░░` 85% | enumerate windows and controls, read trees/lists/tabs/combos, select rows, fire `NM_DBLCLK`/`NM_CLICK`, run menu commands, press dialog buttons, list and reveal dialogs | arbitrary text entry into fields; drag and drop; context menus; keyboard simulation |
| Plugin load and save | `██████████░░░░░░░░░░` 50% | open the Data dialog and tick a plugin (two calls); save a plugin with a timestamped backup and verify the file changed | editing load order; creating a new plugin; saving as a new name; merging |
| Precombine / previs | `███████████████░░░░░` 75% | start headless generation jobs, follow exit code, artifacts and captured editor warnings, tail the console | verifying the result visually; per-cell regeneration; diagnosing what a pass skipped |
| Diagnostics and recovery | `████████████████░░░░` 80% | report build, process, window, loaded cell and the hidden dialog blocking the editor; re-resolve stale anchors; reveal and retry around a blocking modal | crash triage; log correlation; restarting the editor on demand |
| **Record editing** | `██░░░░░░░░░░░░░░░░░░` 10% | browse what the Object Window lists; open a record's dialog | creating, editing or saving any record field; conflict handling; copy-as-override |
| **Navmesh** | `░░░░░░░░░░░░░░░░░░░░` 0% | — | everything: geometry, triangles, cover, baking, verification |
| **Papyrus / scripting** | `░░░░░░░░░░░░░░░░░░░░` 0% | — | attaching scripts, editing properties, compiling, reading VM state |
| **Assets** (mesh, texture, material) | `█░░░░░░░░░░░░░░░░░░░` 5% | list assets in the Object Window tree | previews, importing, replacing paths, material editing |
| **Landscape, weather, lighting** | `░░░░░░░░░░░░░░░░░░░░` 0% | — | terrain editing, region painting, weather and lighting preview, LOD |
| **Reference placement** | `░░░░░░░░░░░░░░░░░░░░` 0% | read positions only | creating, moving, rotating or deleting references, and any undo integration |

---

## The tools

### MCP tools (16)

| Tool | What it does |
|---|---|
| `ck.status` | editor process, build, main window, command manifest age, whether the plugin answers |
| `ck.commands` | capture the live menu tree into a manifest of build-specific command ids, or read the cache |
| `ck.invoke_command` | run a menu command by path (`File/Save`) or id; refuses while a modal has the window disabled |
| `ck.save` | back the plugin up out of `Data`, post Save, and confirm the file actually changed |
| `ck.generate_precombine` | start a headless `-GeneratePreCombined` job; returns an id immediately |
| `ck.generate_previs` | start a headless `-GeneratePreVisData` job |
| `ck.job_status` | job state, exit code, artifacts, captured `EditorWarnings.txt` |
| `ck.job_log` | tail a job's console log |
| `ck.frame` | load a cell and stand the camera in front of a named reference (`distance`, `yaw`) |
| `ck.aim` | turn the camera onto a named reference without moving it; optional `distance` standoff |
| `ck.camera` | `get`, `set` (position + yaw/pitch), `move` along camera axes, `lookat` |
| `ck.cells` | list the Cell View's cells as EditorID, FormID, name and row |
| `ck.cell_load` | open a cell exactly as double-clicking it does |
| `ck.cell` | which cell the render window holds, as EditorID and form id, so a load is confirmed rather than assumed |
| `ck.ui` | passthrough to the in-process control tools below |
| `ck.resolve` | re-read the render window and camera once, for when anchors go stale after a load |

### Inside the plugin

`status`, `camera get|set|move|lookat`, `cell`, `resolve`, `frame`, `aim`, and the control tools reached
through `ck.ui`:

`ui.frame`, `ui.windows`, `ui.tree`, `ui.items`, `ui.select`, `ui.notify`, `ui.command`, `ui.menu`,
`ui.click`, `ui.modals`, `ui.cells`, `ui.cellload`, `ui.dataload`

---

## What it deliberately does not do

Nothing here is a promise of future work — these are gaps, listed so nobody assumes otherwise.

- **Navmesh.** No geometry, no triangles, no cover, no baking. The editor's navmesh tools are not wired up at
  all.
- **Record editing.** CKAgent can open windows and press buttons, but it cannot create, modify or save a
  record field. Anything that changes a plugin's contents beyond "the editor's own Save command" is out of
  scope today.
- **Papyrus.** No script attachment, no property editing, no compilation.
- **Landscape and lighting.** No terrain, weather or LOD editing.
- **Placing references.** Positions can be read; nothing can be placed, moved or deleted.
- **Assets.** The Object Window tree is readable; meshes, textures and materials are not.
- **Load order.** A plugin can be ticked in the Data dialog; the order and the file list cannot be edited.
- **Tilt.** `ck.camera set` accepts pitch and `ck.aim` reports the vertical offset it cannot fix: the pitch
  rows of the camera matrix were measured not to move the view row, so a tilted view is not reachable this
  way. The editor's own `lookat` was measured not to aim at the point given, which is why `ck.aim` computes
  its own yaw instead.

---

## Where the numbers come from

Everything above was measured against a live editor, not read off the SDK:

- 36 865 cells listed under the Commonwealth worldspace; 3 616 references enumerated in `AndrewStation01`.
- Camera offsets are identical inside and outside: same camera object, same `+0xC0`, same 6.0 scene
  rotation offset, yaw 0 → view row `+X`, yaw 90° → `+Y`, at 22 528 / −43 008 world units as well as at
  interior scale.
- `ck.frame` on `Loot_Prewar_Safe` reports `aim 1.0000`; `ck.aim` reports `aligned:true` when the camera is
  level with the object and `aligned:false` with a non-zero `vertical` when it is not.
- A dialog created while the editor is not foreground stays invisible and disables the frame; CKAgent detects
  it from `status`, reveals it, retries once, and names it if it still blocks.

---

## Running it

1. Install CKPE for CreationKit 1.11.246.0 and drop `CKAgent.dll` into `<game>\CKPEPlugins\`.
2. Launch the editor through `ckpe_loader.exe`.
3. Start the MCP server: `dotnet CKAgent.Mcp.dll`. Running `ckagent.exe` directly can fail with
   `pipe_denied`: a process started from a sandboxed workspace directory inherits a low-integrity token,
   and Windows refuses that token access to the medium-integrity pipe. `dotnet` itself runs at medium
   integrity, so the DLL host always connects.

Working files — the command manifest, save backups, job logs — live in `%LOCALAPPDATA%\ckagent` and survive
a server restart. A job left `running` is reported `interrupted` rather than silently forgotten.

## Checks

`.github/workflows/camera-layout.yml` runs `tools/camera-layout.test.ps1` on every pull request. It fails
when the plugin's hand-measured camera constants drift (world `0x70`, translate `world+0x30`, scale
`world+0x3C`, row stride `4`), when the readable window stops covering the scale word, or when the stride
stops being used to address columns. Two extra steps drift the offset and the stride on purpose and fail if
the test still passes, so the gate itself is checked. The test reads source text only — no editor, no
plugin build, nothing to install.
