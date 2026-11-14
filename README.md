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
| Camera pose | `██████████████████░░` 90% | read position, rotation and scale; set position with yaw and pitch; step along the camera's own axes | orthographic / zoom control; field of view |
| Framing and aiming | `████████████████░░░░` 80% | stand in front of a named reference at a distance and yaw; turn the current camera onto a name, tilting for objects above or below, and report the achieved aim | automatic distance and height that fit the object's bounds |
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
| `ck.camera` | `get`, `set` (position + yaw/pitch), `move` along camera axes, `lookat`, `orbit` a point at a distance and angle |
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
- **Zoom and projection.** The camera can be aimed and stood anywhere, but its field of view, orthographic
  mode and zoom are not exposed: framing fits a distance you choose, not the object's bounds.

---

## Where the numbers come from

Everything above was measured against a live editor, not read off the SDK:

- 36 865 cells listed under the Commonwealth worldspace; 3 616 references enumerated in `AndrewStation01`.
- Camera offsets are identical inside and outside: same camera object, same `+0xC0`, same row-shifted
  scene rotation, yaw 0 → view row `+X`, yaw 90° → `+Y`, at 22 528 / −43 008 world units as well as at
  interior scale. The view row is row 1 of the camera node's matrix (row 0 of the scene camera's), which
  the probe confirms independently of what the tool writes — and the aim every tool reports is graded on
  the scene camera, not on the matrix it just wrote.
- `ck.frame` on `Loot_Prewar_Safe` reports `aim 1.0000`; `ck.aim` does the same from 400 units above the
  object, writing a pitch of −1.0015 rad and a view row of `(0.5391, 0, −0.8423)`, the direction to it.
- `lookat` at `(1000, 2000, 3000)` from `(39, 9492, 272)` lands the view on `(0.1197, −0.9329, 0.3397)`,
  the exact unit direction, and the editor's own `LookAt` produces the same matrix.
- `orbit` around `Loot_Prewar_Safe` at radius 400 holds the radius on all three angles tried (0, π/2 and
  π with pitch 0.4), measures 400.00 from the probe's own translation each time, and leaves `pivotRead`
  equal to the point with `hasPivot: true`, so the editor's own orbit and zoom now work around it.
- A dialog created while the editor is not foreground stays invisible and disables the frame; CKAgent detects
  it from `status`, reveals it, retries once, and names it if it still blocks. `tools/blocked-modal-probe.ps1`
  recreates that state and asserts the whole sequence, so it is no longer a manual check.

---

## Build it

You need Windows x64, Visual Studio 2022 with the C++ workload (the plugin is MSVC-only), CMake 3.25 or
newer, .NET 8 SDK for the server, and a **built** CKPE checkout, because the plugin links CKPE's own
libraries rather than re-deriving the editor's internals.

### 1. The plugin, `CKAgent.dll`

```powershell
# from an "x64 Native Tools Command Prompt for VS 2022"
git clone https://github.com/Perchik71/Creation-Kit-Platform-Extended ckpe
# build CKPE x64 first, so that ckpe\x64\ holds CKPE.lib, CKPE.Common.lib and CKPE.PluginAPI.lib

cmake -S CKAgent/plugin -B build/ckagent -G Ninja `
      -DCKPE_ROOT=ckpe `
      -DCKPE_PLUGIN_DIR="D:/SteamLibrary/steamapps/common/Fallout 4/CKPEPlugins"
cmake --build build/ckagent --config Release
```

- `CKPE_ROOT` is the checkout; the three import libraries are read from `<CKPE_ROOT>/x64`.
- `CKPE_PLUGIN_DIR` is optional. When set, the build copies the DLL into the game's `CKPEPlugins\` folder
  as `CKAgent.dll`. Without it, take `build/ckagent/CKAgentPlugin.dll` and copy it there under that name.
- Ninja is single-config, so the binary lands in `build/ckagent/`. A `-G "Visual Studio 17 2022" -A x64`
  configure works too, and then the path carries `\Release\`.
- Configure fails with an actionable message when `CKPE_ROOT` is unset, when an import library is missing,
  or when the compiler is not MSVC — it never half-configures and fails later at link time.

CKPE must be installed in the game folder for any of this to run; the server cannot provide it. The plugin
loads at editor startup, so the DLL has to be in `CKPEPlugins\` before `ckpe_loader.exe` is launched. It
serves `\\.\pipe\ckagent` and writes `CKAgent.log` beside CKPE's other plugin logs.

### 2. The MCP server, `ckagent`

.NET 8 only — the server builds and runs without the plugin, and reports `CKPEUnavailable` from tools that
need it:

```powershell
dotnet build CKAgent/CKAgent.Mcp/CKAgent.Mcp.csproj -c Release
dotnet CKAgent/CKAgent.Mcp/bin/Release/net8.0/ckagent.dll
```

For a folder to point a client at, publish instead:

```powershell
dotnet publish CKAgent/CKAgent.Mcp/CKAgent.Mcp.csproj -c Release -r win-x64 --self-contained false -o out/ckagent
```

### 3. Point an MCP client at it

```json
{
  "mcpServers": {
    "ckagent": {
      "command": "dotnet",
      "args": ["C:/path/to/CKAgent/CKAgent.Mcp/bin/Release/net8.0/ckagent.dll"]
    }
  }
}
```

Start it through `dotnet`, not `ckagent.exe`, if you hit `pipe_denied`: a process started from a sandboxed
workspace directory inherits a low-integrity token, and Windows refuses that token access to the
medium-integrity pipe. `dotnet` runs at medium integrity, so the DLL host connects. This was measured on
this host, not assumed — the same binary launched from a folder outside the sandbox connects fine.

Working files — the command manifest, save backups, job logs — live in `%LOCALAPPDATA%\ckagent` and survive
a server restart. A job left `running` is reported `interrupted` rather than silently forgotten.

### 4. Launch the editor

Start CreationKit through `ckpe_loader.exe` and give it a minute: the plugin answers on the pipe once the
editor's main window is up. `ck.status` says whether it does.

## Documentation

`docs/ck-agent-mcp.md` is the full account — measured offsets and why each one sits where it does, the pipe
protocol, the per-tool behaviour, and what was tried and rejected. Everything marked "measured" in it came
from a live editor build, not from the SDK headers.

`docs/ckpe-render-control.md` is the anchor table the plugin is pinned to: which CKPE header supplies each
id, and where the corresponding class table, singleton and global sit in the 1.11.246.0 image.

## Checks

`.github/workflows/camera-layout.yml` runs `tools/camera-layout.test.ps1` on every pull request. It fails
when the plugin's hand-measured camera constants drift (world `0x70`, translate `world+0x30`, scale
`world+0x3C`, row stride `4`), when the readable window stops covering the scale word, when the stride stops
being used to address columns, or when `mode=orbit` stops setting the editor's pivot and placing the camera
a distance away from it. Three extra steps drift the offset, the stride and the orbit placement on purpose
and fail if the test still passes, so the gate itself is checked. The test reads source text only — no
editor, no plugin build, nothing to install, which is why it can run on a stock runner.

`tools/blocked-modal-probe.ps1` covers the part that cannot run on a runner: it needs a live editor. It
opens `File/Data` while the editor is in the background so the dialog is created hidden, asserts `status`
names it, then makes a call that must fail (`ck.frame` with no cell loaded) and asserts the dialog stops
being hidden and the editor is usable again — with a control call first that succeeds and must leave the
dialog alone, so the change can only come from the reveal-and-retry path. The MCP step runs through
`tools/mcp-call.mjs`, so the retry above the plugin pipe is what is exercised. Run it from the repository
root with the editor running and no cell open; exit code 2 means the hidden state did not reproduce, not
that the code failed, and the dialog is always dismissed with Cancel so the load order is untouched.
