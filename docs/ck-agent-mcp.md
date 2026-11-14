# Controlling the Creation Kit from an MCP agent

> This copy ships with the CKAgent repository, and so do the CKPE anchor findings it cites
> (`docs/ckpe-render-control.md`). The memory probes it names live in the GARDEN-Tools monorepo under
> `tools/ck-surface-probe/`, because they read the editor from outside the process. Build steps are in the
> repository `README.md`.

Investigation into driving `CreationKit.exe` so an AI agent can operate the live editor the way a
person does: select forms, place references, move the render camera, generate precombines, navmesh
and finalise cells.

The short answer: **yes, and it splits cleanly into three surfaces that need different amounts of
effort.** Most of "everything" is reachable with no DLL injected at all, because the editor's
commands are Win32 menu items with stable IDs and its lists are real `SysListView32` controls.
The part that is *not* reachable without injection is the render window's live scene, and that is
the part that matters most for world building.

**CKPE is a required dependency, not an option.** Creation Kit Platform Extended is what modern
Creation Kit work already installs, and it is the only thing that gives a plugin the editor's own
class layouts and jump targets. So the plan below leads with the in-process half instead of
treating it as a later nicety.

Everything labelled "measured" below came from `tools/ck-surface-probe/ck-surface-probe.ps1` run
against the installed editor, not from documentation.

## What is installed here

| Fact | Value |
|---|---|
| `CreationKit.exe` file version | `1.11.246.0` (x64, "Creation Kit 2.0") |
| CKPE present in the game folder | no (no `CKPE*` files or `CKPEPlugins\`) |
| Editor launches directly off Steam | yes, without `steam_appid.txt` |
| Other tooling present | Papyrus Compiler, `Archive2`, `Elric`, LipGen, xSE dlls |

CKPE (Creation Kit Platform Extended, LGPL-3.0, github.com/Perchik71/Creation-Kit-Platform-Extended)
lists `1.11.246.0` among its supported editor versions. That matters because CKPE ships a
per-version offset tables and an `EditorAPI` header set that declares the editor's own classes —
`TESDataHandler`, `TESForm`, `TESObjectCELL`, `TESObjectREFR`, `TESWorldSpace`, `TESQuest`,
`TESRenderControl`, `TESFile`, and ~90 more. A plugin compiles against those and calls the editor's
own functions instead of faking input.

## Surface 1 — Win32 menu commands (measured, works today, no injection)

The main window is a plain MFC frame: class `Creation Kit`, children `MDIClient`,
`ToolbarWindow32`, `msctls_statusbar32`. Its menu bar is a **real `HMENU`**, and every editor command
is an ordinary menu item with a stable ID.

`GetMenuString` + `GetMenuItemInfo` over the whole tree returned **9 top-level menus and 230 unique
command IDs**, including:

| Command | ID | Command | ID |
|---|---|---|---|
| Data... | 40017 | Object Window | 40199 |
| **Save** | **40127** | Cell View Window | 40200 |
| Undo | 40097 | Warnings | 41037 |
| Redo | 40162 | Validate Loaded Data | 40620 |
| Duplicate | 40185 | PreCombine Geometry For World | 41186 |
| Run Havok Sim | 40426 | PreCombine Geometry for Loaded Area | 41218 |
| World Spaces... | 283 | Check NavMeshes | 40568 |
| Cells... | 40035 | Finalize Cell NavMeshes | 40608 |
| Generate Max Height Data For World | 40902 | Compact Active File Form IDs | 41551 |
| Convert Active File to Light Master | 41549 | Create Archive | 40993 |

So `PostMessage(hMain, WM_COMMAND, id, 0)` executes an editor command exactly as the user would,
with the editor's own validation, undo recording and dialogs. That alone is a large usable tool
surface, and the full ID table should be captured as a checked-in manifest rather than hard-coded
from memory.

Limits: a command that needs a modal dialog (Preferences, Data) will block, and only a hidden one is
cleared for you; commands that act on a
selection need the selection made first, and the render window has no "command" for placing a
reference where you want it.

## Surface 2 — the editor's lists (measured; real Win32 controls)

| Window | Class | Children |
|---|---|---|
| Object Window | `FaceClass` | `SysTreeView32` (categories) + `SysListView32` (forms) |
| Cell View | `ViewerClass` | 2 × `SysListView32` (cells, refs) + 2 × `SysHeader32` |
| Layers | `#32770` | `SysTreeView32` |
| Material Palette | `#32770` | `SysListView32` |
| Warnings | `#32770` | `SysListView32` |
| Render Window | `MonitorClass` | 4 × `Static` (the scene surface) |

These are genuine common controls, not owner-drawn fakes, which means `LVM_*`/`TVM_*` messages apply.
The one caveat to settle with a test: if the form list is `LVS_OWNERDATA` (virtual), item text is
supplied on demand by the editor's own callback, and reading it cross-process needs a block of
memory allocated inside the target process (`VirtualAllocEx` + `LVM_GETITEMTEXT` marshalling), not
just `SendMessage`. The item *count* and the selection/hit-test/scroll messages work either way, so
"select row N and press the default action" is reachable without injection even in the worst case.

## What does not work: UI Automation

This was the obvious cheap answer and it is **not** good enough. Walking the automation tree of the
Object Window returned 10 panes, none of them carrying a name, a pattern, or any item: the list view
appears as an unnamed `Pane` with `patterns=` empty. Cell View likewise. The render window exposes
nothing at all (`nodes walked: 0`).

So accessibility-based automation would be blind guessing by on-screen coordinates. Rejected.

## Surface 3 — in-process via CKPE (the only route to the live scene)

For camera control, dragging a reference into the render window, paint-mode landscape work, live
navmesh editing and truthful scene readback, you need to be inside the process. CKPE provides: a
loader, per-version offset tables, a plugin contract
(`CKPEPlugin_Version`, `CKPEPlugin_Load`, optional dependency list), and `EditorAPI` class
declarations for FO4. A plugin DLL dropped in `<GAMEDIR>\CKPEPlugins\` is loaded by the editor and
can call editor internals on the main thread.

The plugin should stay thin — a transport plus a command table:

```
CKPEPlugins\CKAgent.dll
  DllMain                     capture module path
  CKPEPlugin_Version          pin kGameFallout4 + 1.11.246.0
  CKPEPlugin_Load             open \\.\pipe\ckagent, start worker thread
  worker thread               read JSON-RPC frames -> enqueue to main thread
  message hook (WM_APP+N)     drain queue on the editor thread, dispatch, reply
```

Two rules make or break this: everything editor-facing must execute on the process's main thread
(the editor has no thread-safety story), and version pinning must be strict — CKPE's addresses are
build-specific, so a Steam CK update must fail loudly, not silently call the wrong function.

### The APIs the plugin is written against (verified against CKPE master)

CKPE's `TES.h` carries a comment for **this exact editor build**, which is why `1.11.246.0` is the
pinned target rather than a hopeful guess:

> `1.11.246.0: size 0x138, virtual table at +0x30120A8, TES_Singleton global at 0x143D49150.`

| Anchor | Where it comes from | What it buys |
|---|---|---|
| `TES::Singleton` (`ISingleton<TES>::GetSingleton()`) | `EditorAPI/T/TES.h` | loaded interior cell, grid coordinates, `worldSpace`, `HasLoadedInterior()`, `HasLoadedExterior()` |
| `TES_Singleton` @ `0x143D49150` | same comment | the singleton is default-constructed empty, so the plugin assigns the address itself when the offset table has not |
| `TESRenderControl` — `SceneRoot` 0x20, `CameraRoot` 0x50, `Camera` 0x58 (`NiPointer<NiCamera>`), `RenderWindow` 0x60, `DialogWindow` 0x08, `DisplayControlID` 0x10, `sizeof` 0xB0 | `EditorAPI/T/TESRenderControl.h` | CKPE's view of the class; this editor build only instantiates it as `TESPreviewControl`'s base, so its fields describe a preview control's view, not the render window's |
| `TESObjectREFR` — `_parentCell` 0xC8, `_rotate` 0xD0, `_position` 0xE0, `_baseForm` 0xF0, `_scale` 0x118, plus `Position`/`Rotate`/`ScaleFloat` properties and the `SetPosition` / `SetParentWithRedraw` function pointers | `EditorAPI/T/TESObjectREFR.h` | moving, rotating, rescaling and re-parenting placed references |
| `RenderWindowManager::SetSoundMarkerNodeCulled`, CK `1.11.246.0` `0x1408426B0`, relocation id `506883` | `EditorAPI/R/RenderWindowManager.h` | proves the per-build relocation scheme a plugin must use instead of hard-coded offsets |
| `CKPEPluginVersionData` / `CKPEPlugin_Load` / `GET_EXE_VERSION_EX_*` | `CKPE.PluginAPI` | ABI and runtime build checks |

### The render control is *not* reachable from its window (measured)

`TESRenderControl` has no singleton, so the first hypothesis was that the `MonitorClass` render
window publishes its instance through `GWLP_USERDATA`. `tools/ck-surface-probe/render-control-probe.ps1`
tested it and **refuted it**:

- Every window's `GWLP_USERDATA` is readable, and the render window's object has a real
  virtual table at +`0x2FD40B8` inside `CreationKit.exe` — so it is a genuine editor object, not a `CWnd`.
- That object does **not** satisfy the `TESRenderControl` layout: `+0x08` (`DialogWindow`) is `0`
  and `+0x20` (`SceneRoot`) reads as `3`.
- Scanning 4096 bytes of its pointers for a target whose `DialogWindow` is a live window of this
  process *and* whose `DisplayControlID` resolves a child found nothing. The same scan over every
  top-level window and child found nothing.

So the window is not the control; it is the object that owns the view. The corpus has no decompile of
either class, so the chain comes from the 1.11.246.0 image itself (`docs/ckpe-render-control.md`
holds the addresses):

- `TESRenderControl`'s constructor has exactly one caller, `TESPreviewControl`'s, and
  `TESPreviewControl`'s RTTI lists `TESRenderControl` as its base. The editor never creates a plain
  `TESRenderControl`, so an exact-class-table scan cannot match the view — the earlier walk was
  looking for an object that does not exist.
- The `MonitorClass` window publishes a `BGSRenderWindow` (class table `0x2FD40B8`, `0x180` bytes),
  and the editor stores that same object in image data at `0x143D491E8` — 0x98 bytes after the
  `TES_Singleton` address CKPE records.
- The window keeps its camera at `+0xC0`: a `BGSRenderWindowCamera` (class table `0x142FD4828`,
  `0x88` bytes). It has no base class, so it is a camera *parameter* block, not an `NiCamera`.
- The live view is the `NiCamera` that object points at from `+0x10` (class table `0x143430CC0`),
  confirmed in a running 1.11.246.0 editor. That class *is* an `NiAVObject`, so the transform offsets
  CKPE documents are the ones to use: `_World` at `+0x70`, `m_Translate` at `+0xA0`, `m_fScale` at `+0xAC`.

The plugin reads the slot at offset `0x3D491E8` **from the image base it reads at load** — the editor is
relocated, so a hardcoded `0x140000000` base resolves to unmapped memory and faults — and validates
each class table in turn (`BGSRenderWindow`, `BGSRenderWindowCamera`, `NiCamera`), at load and again on
a bounded timer until the window exists;
`ck.resolve` repeats that pass on demand, because loading a cell can replace the camera object behind
an unchanged window. Nothing walks memory and no offset is guessed: before any float is read or
written, the rotation must be orthonormal with a positive scale, otherwise `ck.camera` reports
`camera_transform_unverified` (which is what an empty editor gives).

### Diagnostics inside the editor

The plugin must be debuggable without a pipe client, so it writes every diagnostic twice: to its own
`Logs/CKPE/Plug-ins/CKAgent.log` and to CKPE's main log via `CKPE::_MESSAGE`, which is the stream the
editor's **Log Window** shows. Every agent call appears there as `CALL <request> -> <reply>`.

For a live view the plugin appends an **MCP** entry to the editor's menu bar and subclasses the frame
only to route that one command. It opens **CK Agent — MCP**, a read-only window showing the current
binding state (pid, resolved window and camera, why the camera is unresolved) plus the last 400 log
lines, refreshed every 600 ms.

A toolbar button cannot be the entry point: CKPE owns that toolbar and paints its text itself, so an
appended `TB_ADDBUTTONS` entry is accepted (`58 -> 59 buttons`) and renders as nothing. The menu bar is
native, so the entry is visible.

The frame itself must be found by what it owns (a menu), because its window class name is not stable:
looking it up by name silently found no window.

The window must match the editor's palette, so it reads `CreationKitPlatformExtended.toml`
(`bUIDarkTheme`, `uUIDarkThemeId`, `sFont`, `nFontSize`) and, for a custom theme, `rEdit_BkColor` and
`rText_3` from `CreationKitPlatformExtendedCustomTheme.toml`. The result is logged as
`theme background #… text #… font …` so a wrong palette is diagnosable without a screenshot.

### Keeping JSON out of the injected DLL

The bridge speaks MCP and owns all JSON. The pipe carries a flat line protocol
(`HELO` / `CALL <seq> <tool> [args]` / `RPLY <seq> OK|ERR …`) because a parser inside the editor
process is another way to lose unsaved work. The plugin composes its small reply payloads by hand
and never parses.

## Surface 4 — headless batch CLI (measured strings, long used in the wild)

The editor parses a large undocumented switch set before it opens a window. Confirmed present in
this binary, each with its own parse-error string:

`-ExportText`, `-CompileTextExport`, `-ExportDialogue`, `-ImportScalingData`, `-ExportFaceGenData`,
`-ExportDismemberData`, `-GenerateAnimInfo`, `-GenerateLips`, `-GenerateSingleLip`,
`-GenerateSEQ`, `-GeneratePreCombined`, `-GeneratePreVisData`, `-GenerateStaticCollections`,
`-GenerateWarnings`, `-MapInfo`, `-MapMaker`, `-OutputAreaArt`, `-OptimizeMasterFile`,
`-OptimizeDLC`, `-ConvertToESL`, `-BuildCDX`, `-DepersistRefs`, `-DelocalizeMasterfile`,
`-TagifyMasterfile`, `-TagifyPlugin`, `-CheckInPlugin`, `-SaveDefaultPlugin`, `-UpdateModelData`,
`-ConvertMaterials`, `-CompressPSG`, `-DumpNeededFiles`.

Example usage string from the binary:

```
-GeneratePreCombined:<ESMFilename> [clean,filtered] [all,main,ints,other] [currentset(1-totalsetcount)] [totalsetcount]
```

These run the editor headless with no interaction, and the community already drives
`-GeneratePrecombined:`, `-GeneratePreVisData:` and `-GenerateLips:` this way from batch files. They
are the cheapest big win: the six-hour previs rebuild is the most valuable thing an agent can start
and walk away from.

## Recommended architecture

Two processes, one tool namespace, following the existing in-repo MCP convention
(`BGS/tools/mcp` — .NET host, `WithStdioServerTransport()`, `[McpServerTool]` classes):

```
agent  ──stdio MCP──▶  ckagent.exe  ──┬── process control: launch CK, wait for ready, read logs
                                       ├── WM_COMMAND to the menu IDs (surface 1)
                                       ├── LVM_*/TVM_* on Object Window / Cell View (surface 2)
                                       ├── batch CLI jobs, streamed to a job file (surface 4)
                                       └── named pipe ──▶ CKAgent.dll inside CK (surface 3, required)
```

`ckagent.exe` is the only thing the AI talks to. Surfaces 1, 2 and 4 still work when the plugin is
absent, but the tool set is defined around the plugin: surface 3 tools fail with `CKPEUnavailable`
naming the missing `CKPEPlugins\CKAgent.dll`, so a half-installed CKPE is loud instead of silent.

Launch it as `dotnet ckagent.dll`. On this host an apphost started from inside the sandboxed workspace
comes up at **low integrity**, and a low-integrity client cannot open a medium-integrity pipe for
read/write, so `NamedPipeClientStream.Connect` fails with `ERROR_ACCESS_DENIED` (reported as
`pipe_denied`). Measured: the same `ckagent.exe` copied outside the workspace runs at medium integrity
and connects; so does `dotnet ckagent.dll`, whose host lives in `C:\Program Files\dotnet`. The plugin
does not inspect clients, and signing is not the factor — a signed copy inside the workspace is refused
the same way. Register the server with `command: dotnet`, `args: [<build>\ckagent.dll, --game, ...]`.

The plugin label is why the apphost works anyway: its pipe carries a low mandatory label
(`D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)S:(ML;;NW;;;LW)`), so a client at low integrity can still write to
it. Before that label, `ckagent.exe` from the workspace was refused while `dotnet ckagent.dll` connected —
both measured. A client cannot raise itself instead: a low process may not write a medium-labelled file, so
copying the build somewhere else and relaunching fails by design.

When a denial does happen, `pipe_denied` reads the client's own token and names its level, because that
decides the cause: at low it means the loaded plugin predates the low-labelled pipe or the pipe belongs to
another user; at medium it means the pipe belongs to another account or a higher-integrity process.

Tool naming, namespaced so it does not collide with the existing `fo4editor` server:

| Tool | Surface | What it does |
|---|---|---|
| `ck.status` | 1/3 | Is the editor up, which plugin is loaded, load order, active plugin, dirty flag |
| `ck.invoke_command` | 1 | Run any menu command by name from the command manifest |
| `ck.save` / `ck.save_all` | 1 | Explicit save, with a backup copy first |
| `ck.open_plugin` / `ck.set_active_plugin` | 1 | Load a plugin, make it active |
| `ck.list_forms` | 2 | Read Object Window rows (type, editor id, form id) for a signature |
| `ck.select_form` | 2 | Select a form in the Object Window; returns the selection |
| `ck.open_form` | 2 | Open a form's detail dialog by editor id |
| `ck.load_cell` / `ck.current_cell` | 1/3 | Load a cell into the render window |
| `ck.place_reference` | 3 | Place a base object at world coordinates |
| `ck.transform_selection` | 3 | Move/rotate selected references |
| `ck.camera` | 3 | Set camera position/rotation, screenshot the render window |
| `ck.generate_precombine` / `ck.generate_previs` | 4 | Start a batch job, return a job id |
| `ck.job_status` / `ck.job_log` | 4 | Poll the batch job, tail its log, report the artifacts |
| `ck.validate` | 1 | Run "Validate Loaded Data" and return the Warnings rows |

The `ck.camera` screenshot is what makes the loop usable for an agent: 3D results have to be
verifiable visually, not just claimed.

## Rules the implementation must hold

- **Main-thread affinity.** Every editor call is marshalled; the pipe thread never touches editor
  state. The plugin replies with a request id and the MCP layer correlates.
- **Never auto-save.** Saving a plugin is destructive and hard to review. `ck.save` is an explicit
  tool, and it writes a timestamped backup next to the plugin first.
- **Selection is a prerequisite, not a side effect.** Tools that mutate should assert what is
  selected and refuse when the selection is not what the caller expects.
- **Batch jobs are jobs.** Return an id immediately, write stdout/stderr and the editor's
  `EditorWarnings.txt` to a per-job folder, and treat a missing expected artifact as failure even
  when the process exits 0.
- **Crash containment.** This install has produced `CreationKit_*.dmp` files recently; a long
  automation run will eventually take the editor down. Long sequences must be resumable from a
  recorded step log, and the MCP server must detect a dead process and report it instead of hanging.
- **Version gate.** Refuse to run the in-process half against an unrecognised editor build.

## Phases

| Phase | Scope | Done when |
|---|---|---|
| P0 | Plugin transport: CKPE plugin DLL, pipe, main-thread dispatch, `ck.status` | `ck.status` inside the running editor returns the loaded cell and grid, and a wrong editor build is refused |
| P1 | Map the pose fields of `BGSRenderWindowCamera` so `ck.camera` can read and set the view | A camera move by tool call is visible in the render window |
| P2 | `ck.place_reference`, `ck.transform_selection` on `TESObjectREFR` | A reference placed by tool call is verifiable in the plugin file after save |
| P3 | Command manifest + `ck.invoke_command`, `ck.save` with backup; `ck.validate` | An agent can run any menu command by name and save deliberately |
| P4 | Batch job runner (surface 4) | A previs+precombine job runs end to end from a tool call, artifacts named correctly |
| P5 | Object Window and Cell View read/select (surface 2); `ck.open_form` | An agent finds a form by editor id and opens it with no coordinates involved |
| P6 | Scene readback (what is rendered, selection state, navmesh coverage) | Tools report editor truth instead of inferring it |

P0 and P1 are pure out-of-process work and land without any reverse engineering. P3 is where the
generated manifest has to be regenerated per CK build.

## Risks

| Risk | Mitigation |
|---|---|
| Steam updates the CK, CKPE addresses shift | Strict version gate; pin a known build; manifest is generated, not hand-written |
| LGPL-3.0 obligations if we ship CKPE-linked code | Check before distributing the plugin; keep the MCP server independent of CKPE headers |
| Menu IDs are MFC-generated and could shift between builds | Generate the manifest per build with the probe, diff it in CI |
| Owner-data list views block text readback | Test early; fall back to selection + default action |
| Editor dialogs block automation | Prefer commands that do not modal; time-box and detect modal windows |
| Agent fills a plugin with junk | Save gating, backups, and a dry-run/preview mode for placement |

## Reproducing the measurements

```powershell
# attach to a running editor
tools/ck-surface-probe/ck-surface-probe.ps1

# or start one, probe it, then stop what it started
tools/ck-surface-probe/ck-surface-probe.ps1 -Launch -Seconds 120
```

Output goes to `.probe/ck-surface.txt`: window classes, the full menu ID tree, and the UI Automation
walk. Note this shell sets `TEMP`/`TMP` to the literal `:TEMP`, which breaks `Add-Type`; the probe
points them at a scratch directory before compiling.

Because the probe hard-codes the game path, pass `-Exe` for other installs.

## Building and installing

1. Build CKPE (x64) from `Perchik71/Creation-Kit-Platform-Extended` for CK `1.11.246.0`, including
the matching offset tables.
2. Configure the plugin against that tree:

   ```
   cmake -S CKAgent/plugin -B build/ckagent -G Ninja \
         -DCKPE_ROOT=<ckpe checkout> -DCKPE_PLUGIN_DIR="D:/SteamLibrary/steamapps/common/Fallout 4/CKPEPlugins"
   ```

3. `CKAgent.dll` lands in `CKPEPlugins\`; the editor loads it at startup and writes `CKAgent.log`
   next to the other CKPE plugin logs.
4. `ckagent` (MCP) connects to `\\.\pipe\ckagent`.

CKPE must be installed in the game folder for any of this; it is not something the MCP server can
provide on its own.

## What exists now

`CKAgent/CKAgent.Mcp` is the P0 server: a stdio MCP host over `net8.0` with `ModelContextProtocol`,
matching the shape of `BGS/tools/mcp`. It needs no plugin to run:

| Tool | Behaviour |
|---|---|
| `ck.status` | editor process, main window (found by owning a menu bar — the frame's class carries the build, `Creation Kit Fallout 4 [v1.11.246.0]`, so a class match never fires), installed build, manifest age, plugin pipe presence, and the blocking dialog's title, button captions and the caption its dismissal presses |
| `ck.commands` | capture the live menu tree into `<state>\commands.json`, or return the cache |
| `ck.invoke_command` | resolve `File/Save`, `World/PreCombine Geometry For World` or an id, then `WM_COMMAND`; `dry_run` resolves only; refuses while a modal dialog has the window disabled |
| `ck.save` | copy the plugin to `<state>\backups\<stamp>\`, post `File/Save`, then confirm the file changed — and report `save_not_observed` rather than claiming success |
| `ck.camera` | read or set the camera pose through the plugin. Reads and writes `BGSRenderWindowCamera` (window at `0x143D491E8`, camera object at `+0xC0`) via its own `SetPosition`/`SetRotation`, because the `NiCamera` at `+0x10` is a slave the editor overwrites every frame — a write there is reverted within 400 ms (measured). Reports `rotate` (the node's nine values) and `sceneRotate` (the slave `NiCamera`'s nine, so the one-row difference is visible in the same reply instead of needing a probe), `positionSynced` (the scene camera's translation matched the request), `rotationOffset` (the pose-dependent `Σ|node − scene|`, since the scene camera's rotation is the node's shifted by one row), and for a lookat the `forward` row and the achieved `aim` plus a `via` naming whether the editor's own `LookAt` or the computed pose aimed it; `mode=orbit` circles a target at a given angle — either the world point `x/y/z` or a `reference` name, whose bounds centre is circled with the distance defaulting to the same fit `ck.frame` uses — sets the editor's pivot to that point, and reports the reference it used, the pivot it read back, the radius and the aim; `mode=rotate` turns the camera by the step `x/y/z` through the editor's own `RotateBy` and reports the view row, yaw, pitch and their deltas, so an orbit step can be compared against the editor's rotation directly; `mode=lens` reads the field of view from the scene camera's own frustum in degrees, and setting `fov` (degrees) or `zoom` (a factor, above 1 zooms in) writes the frustum and reports what was achieved; `camera_transform_unverified` until a cell gives a usable transform; `CKPEUnavailable` without the plugin |
| `ck.aim` | turn the camera on a named reference without moving it, so an object is inspected from the view already chosen; optional `distance` backs off to that standoff first; reports the yaw and pitch it wrote, the achieved aim and the vertical offset, and says `aligned=false` when the view still misses. It tilts, because the forward row carries the pitch: the pitch is computed from the position the camera ends up at, so a standoff is aimed from where it leaves the camera, and a target straight above or below keeps the current heading and tilts to the pole rather than being refused. A comma-joined list aims at several references in one call, in order, from the same camera position, so a small area can be scanned without the caller computing poses; each step reports its own yaw, pitch and aim, and a standoff is refused with a chain because it moves the camera. `tools/aim-probe.ps1` asserts that contract against a live editor — level, above, below, directly overhead, at a standoff and as a two-step chain — checking the camera did not move and that the view row re-read afterwards dots the target |
| `ck.cells` | list the Cell View's cells as EditorID, FormID, name and row index, optionally filtered — so a cell can be chosen by name instead of by index |
| `ck.cell_load` | open a cell by EditorID or display name through the Cell View's own `NM_DBLCLK` handler; reports the row it matched and the cell the render window holds afterwards (`opened.editorId`, `opened.formId`), and returns `cell_not_loaded` naming the cell that stayed when the try changed nothing |
| `ck.cell` | the cell the render window holds, by EditorID and form id, plus `interior`, `gridX`, `gridY` and `hasWorldSpace` — the same placement the plugin's `status` reports, read from the editor's own `TES` singleton, so a load is confirmed from the reply instead of inferred from a pointer. The grid only means anything when `interior` is false; an interior renderer reports `0, 0`. `tools/cell-probe.ps1` loads cells from the Cell View's own list and asserts the reply names each one, and fails if a fabricated pointer-only reply would pass |
| `ck.frame` | load a cell and stand the camera in front of one of its references (`reference` = reference EditorID, base object EditorID or 8-digit form id; optional `cell`, `distance`, `yaw`), so an object is inspected without navigating by hand; a `cell` that is already open is used as it is — the load and its wait-for-change are skipped and the reply says `(the cell was already loaded)`, measured at 0 s against the same cell and 1 s for a different one, so framing several references in one cell costs one load; the standoff and the height are taken from the reference's own bounds (`2.4` bounding-sphere radii back, `30°` above the box centre), and an explicit `distance` overrides that; reports the match, the target, the box centre and the bounds it framed, whether those bounds came from the reference or a fallback, the pose, whether the object sits ahead along the view row, and the cell the render window actually holds |
| `ck.dismiss` | clears the dialog blocking the editor before anything has failed, where `blockedBy` and `dismissBy` in `ck.status` let a caller see the block in advance. It takes the dialog `ui.modals` flags as blocking (or an explicit `hwnd`), presses the same caption the automatic dismissal uses, and only reports `dismissed` once a status read shows no `blockedBy`; `nothing_blocked` means there was nothing to do. Measured on the hidden `Data` dialog: `dismissed 'Data' with Cancel`, payload `{"dismissed":1,"by":"Cancel","hwnd":"0x1A00B8","title":"Data","cleared":true,"blockedBy":""}`, and `nothing_blocked` when nothing blocked |
| `ck.status` (`dismissBy`) | the caption the automatic dismissal will press, taken from the same choice the dismissal uses (`Cancel`, else `Close`, else `WM_CLOSE`), so a caller can see what will happen as well as what is available. Measured on the hidden `Data` dialog: `dismissBy \"Cancel\"`, and `ui.dismiss` then reported pressing `Cancel` |
| `ck.status` (`modalButtons`) | the captions of that dialog's buttons, so a caller picks a caption that exists instead of guessing. Measured on the hidden `Data` dialog: `blockedBy \"Data\"` with `modalButtons [\"OK\", \"Cancel\", \"Set as Active File\", \"Details...\"]`, the same four `ui.modals` lists for that hwnd; with nothing blocking, both are empty (`\"blockedBy\":\"\"`, `"modalButtons":[]`) |
| `ck.status` (`blockedBy`) | names the hidden modal that has disabled the editor, so a blocked editor is not mistaken for a hung or idle one; every plugin tool reads it after a failure, dismisses that dialog with `ui.dismiss`, retries, and only reports it by name and handle if the block survives. The reply keeps the original failure code and only the message gains the block, saying whether the dialog was dismissed but the block persisted, or could not be dismissed at all. `tools/blocked-modal-probe.ps1` covers both outcomes against a live editor instead of by hand; the still-blocked one is forced with `CKAGENT_TEST_DISMISS_FAILS=1` because a dialog the server does handle is gone by the time the call returns |
| `ck.ui` | passthrough to the in-process control tools (`ui.items`, `ui.select`, `ui.notify`, `ui.command`, `ui.click`, `ui.windows`, `ui.tree`, `ui.menu`, and `ui.cellclose`, which frees the render window's cell so `File/Data` opens again): `method=ui.notify`, `args=0x16C1752 dblclk 2` |
| `ck.resolve` | ask the plugin for one fresh resolution of both anchors and return the addresses, or the reason nothing validated — the refresh to call after loading a cell |
| `ck.generate_precombine` / `ck.generate_previs` | start `-GeneratePreCombined:` / `-GeneratePreVisData:` as a job, one at a time |
| `ck.job_status` / `ck.job_log` | job state, exit code, artifacts, captured `EditorWarnings.txt`, console tail |

```
ckagent --ck "D:/SteamLibrary/steamapps/common/Fallout 4/CreationKit.exe"
```

`--state` moves the working directory (manifest, backups, job logs); it defaults to
`%LOCALAPPDATA%\ckagent`. Jobs write `jobs\<id>\console.log`, `job.json`, the before/after
`EditorWarnings.txt`, and survive a server restart — a job left `running` is reported `interrupted`
rather than silently forgotten.

Deliberate refusals: a wrong id never runs silently (`ck.invoke_command` resolves through the captured
manifest, so a Steam update that renumbers menus shows up as `unresolved`), exit code 0 with no
artifacts is `suspect` rather than `succeeded`, and a plugin name with a path separator is rejected.

## Editor control surface (inside the plugin)

Loading a cell needs no mouse: the plugin drives the editor's own controls on the editor thread, where a
`SendMessage` is a direct call into the CK's handler. The pipe tools behind `ck.cells` / `ck.cell_load`:

| Tool | Behaviour |
|---|---|
| `ui.frame` / `ui.windows` | the frame (found by the menu it owns, never by class name) and every top-level window; reveals a hidden modal that leaves the frame disabled |
| `ui.tree <hwnd>` | control tree with ids, text, class and the contents of list views, trees, combos and tabs |
| `ui.items <hwnd> [max] [offset]` | rows or nodes of one control, paged |
| `ui.select <hwnd> <index\|handle>` | select a row or tree node |
| `ui.notify <hwnd> dblclk [index]` | deliver `NM_DBLCLK` to the parent — the notification the CK acts on |
| `ui.command <text\|path\|id> [dry]` / `ui.menu` | run a menu command. A path (`File/Data`, `File>Data`) is resolved level by level and refuses a disabled item; a bare caption is searched across the bar; `dry` resolves without posting, because ids change between builds |
| `ui.click <hwnd> <caption>` | press a dialog button by caption, which is how a command that opened a modal is dismissed |
| `ui.dismiss <hwnd>` | press the button that abandons a dialog's pending work — `Cancel`, then `Close`, else `WM_CLOSE` — so a blocking modal clears without knowing its captions; OK is never pressed |
| `ui.modals [reveal <hwnd>]` | list every dialog with its owner, visibility, enabled state and button captions, flagging as `blocking` the invisible one that owns a disabled window; `reveal` shows one |
| `ui.cells [filter]` / `ui.cellload <name>` | name-addressed cell list, and open one; both read the collection the Cell View's combo has selected, so an exterior worldspace is only reachable after switching it |
| `ui.cellclose` | free the cell the render window holds, at `BGSRenderWindow+0xF8`, which is the state `File/Data` checks before it opens; reports the pointer it cleared, and `closed:false` when nothing was held |
| `ui.dataload <plugin>` | tick a master file in `File > Data` (two calls: the command, then the dialog) |

Measured flow (editor 1.11.246.0, master `Fallout4.esm`): `ui.cellload AndrewStation01` opens
Andrew Station, `cell` reports `loaded:true` with the cell pointer, and the camera then resolves where
it previously refused with `camera_transform_unverified`.

### Reaching an exterior worldspace by name, measured

The Cell View lists one collection at a time: `Interiors`, `Recent Cells`, then one entry per worldspace.
`ui.cellload` matches rows in that list and nothing else, so a cell in an exterior worldspace is
unreachable by name until the combo is switched to that worldspace. The combo is the first `ComboBox`
under the Cell View pane — `ui.tree <Cell View hwnd>` prints it with its items and its current selection
(id `2083` in 1.11.246.0, `0x262A14` on the run these numbers come from, so read the pane's own tree
rather than hard-coding the handle).

| Step | Command | Reply |
|---|---|---|
| find the pane | `ui.windows`, the `#32770` titled `Cell View` | `0x4012E4` |
| read the entries | `ui.tree 0x4012E4` | first `ComboBox`: `Interiors`, `Recent Cells`, `Commonwealth`, `DiamondCity`, `DiamondCityFX`, `Goodneighbor`, `SanctuaryHillsWorld`, `selected: 0`, id `2083` |
| switch | `ui.select <combo> 3` | `{"selected":"3"}`; the cell list refills — 165 rows for `DiamondCity` against 324 for `Interiors` |
| load by name | `ck.cell_load {"name":"DiamondCityOrigin"}` | `ok`, `ui.cellload DiamondCityOrigin -> opened DiamondCityOrigin` |
| switch away | `ui.select <combo> 6` | list refills with `SanctuaryHillsWorld`'s cells |
| same name again | `ck.cell_load {"name":"DiamondCityOrigin"}` | `no_cell`, `no cell view item matches: DiamondCityOrigin` |

Two things that follow from this. Exterior cells share the editorId `Wilderness` — 100 of
`DiamondCityFX`'s 101 cells do — so a name load cannot disambiguate them: read the list with
`ui.items <list hwnd>` (its columns are `EditorID`, `FormID`, `Name`, `Loaded`, `Coords`, `Location`,
`Owner`, `Last Combined`, and `Coords` is the cell's grid position) and open the row with
`ui.notify <list hwnd> dblclk <row>`. And the combo is the only thing that decides what a name reaches:
loading a cell does not switch it, and nothing in the reply says which worldspace a name was resolved
against, so switch deliberately and check `ck.cell`'s `interior`/grid afterwards.

## Camera transform, measured (editor 1.11.246.0, cell open)

`tools/ck-surface-probe/camera-pose-probe.ps1` reads the `NiCamera` behind
`BGSRenderWindowCamera+0x10` and locates its transforms by testing geometry, not by assuming an
offset. With Andrew Station loaded it finds exactly three:

| Reading | Value |
|---|---|
| transforms found | `0x30`, `0x70`, `0xC0` = `_Local`, `_World`, `_PreviousWorld` (CKPE `NiAVObject`) |
| row stride | 4 floats (`NiMatrix43` is `float[3][4]`), so the rotate part is 0x30 bytes |
| translate | transform `+0x30` |
| scale | transform `+0x3C` (reads 1.0) |

The scan found nothing until it used the 4-float stride: a 3-wide read mixes rows and rejects a
valid matrix. The `0x70` translate equals the position the tool wrote (`3400, 9500, 1400`), which
is what ties the offset to the pose rather than to a lookalike block.

Re-measured on a cell opened by double-clicking it in the Cell View (`AndrewStation01`), at two
poses, so the tie is not one lucky read:

| `camera` reports | probe `0x70` translate | probe `0x70` rows |
|---|---|---|
| `(39, 9492, 272)`, yaw 0 | `39, 9492, 272` | `[[1,0,0],[0,0,1],[0,-1,0]]` |
| `(3500, 9500, 1200)`, yaw 90° | `3500, 9500, 1200` | `[[0,1,0],[0,0,1],[1,0,0]]` |

The probe's rows are the camera node's rows shifted by one both times: at the first pose the node holds
`[[0,-1,0],[1,0,0],[0,0,1]]`, and the probe reads its second, third and first rows. The same shift at two
unrelated poses is what makes `0x70` the world transform rather than a block that happens to hold a
position.

### Orbiting a point, measured

`camera orbit x y z distance angle [pitch]` sets the editor's own pivot to the point, places the camera on
the circle at the distance and angle asked for, and aims it at that pivot through the same pose builder
`set` and `frame` use. Angle 0 puts the camera on the `-X` side looking `+X`, and the angle is the same
`yaw` the other modes take. Around `Loot_Prewar_Safe`'s position `39.5606, 9492.1836, 272.3472`, radius
400:

| Angle | Camera | Probe's view row | Radius from pivot | `aim` |
|---|---|---|---|---|
| 0 | `-360.4394, 9492.1836, 272.3472` | `1, 0, 0` | 400.00 | 1.0000 |
| π/2 | `39.5606, 9092.1836, 272.3472` | `0, 1, 0` | 400.00 | 1.0000 |
| π, pitch 0.4 | `407.9850, 9492.1836, 116.5799` | `-0.9211, 0, 0.3894` | 400.00 | 1.0000 |

The radius is measured from the probe's own translation rather than from the reply, and `pivotRead` comes
back equal to the point with `hasPivot: true` — so the focus the editor's own orbit and zoom work from is
the target, not whatever the previous navigation left behind.

### Orbiting a reference, measured

The pivot can also be a name. `camera orbit Inst305Bulb05 0 0.5` uses the same box centre `ck.frame` frames
and sizes the radius from the same fit, so the two tools agree on where the object is:

| Call | Pivot | Distance | Camera | `aim` |
|---|---|---|---|---|
| `orbit Inst305Bulb05 0 0.5` | `1588.9611, -158.2818, 212.6093` | `62.35` | `1534.2405, -188.1758, 212.6093` | 1.0000 |
| `orbit 1588.9611 -158.2818 212.6093 62.35 0.5` | same | `62.35` | `1534.2438, -188.1740, 212.6093` | 1.0000 |
| `orbit Inst305Bulb05 120 0.5` | same | `120.00` | `1483.6511, -215.8129, 212.6093` | 1.0000 |
| `orbit 1588.9611 -158.2818 212.6093 120 0.5` | same | `120.00` | `1483.6511, -215.8129, 212.6093` | 1.0000 |

The two rows at a given radius agree to the last printed digit; the `62.35` pair differs by three thousandths
only because the true radius is `25.9814` and `2.4 ×` that is `62.3525`, which the coordinate row has to be
told. A name the cell does not hold is `ref_not_found` (the message lists some of the 142 it does hold), and
naming both a reference and `x/y/z` is `invalid_argument` rather than one silently winning.

### Framing a reference, measured

`ck.frame` finds the object by iterating the loaded cell's own `TESObjectCELL::Items` (`GetItemCount`,
`GetItems`, `TESObjectREFR::GetPosition`, `GetFormEditorID`, base object via `GetParent`), so nothing is
assumed about where a reference sits.

It then frames the reference from its own size rather than from a fixed standoff. `TESObjectREFR::GetBoundMin`
and `GetBoundMax` give the box in the reference's own units, so the centre is the reference position plus the
middle of the box, and the radius is half the box diagonal. The camera goes `2.4` radii back and `30°` up
from that centre — `distance = 2.4 × radius`, `camera.z = center.z + distance/2` — so a bulb and a building
each fill the view instead of sharing one standoff. A degenerate box (zero extent, or not finite) is floored
at radius `8` and capped at `8192`; an explicit `distance` in the request still wins.

With `DmndRadio01` loaded (142 references):

| Request | Camera | `radius` | `distance` | `aim` |
|---|---|---|---|---|
| `frame Inst305Bulb05` | `1534.9611, -158.2818, 243.7863` | `25.98` | `62.35` | `1.0000` |
| `frame Inst305Bulb05 0 1.5708` | `1588.9613, -212.2818, 243.7863` | `25.98` | `62.35` | `1.0000` |
| `frame Inst305Bulb05 300` | `1329.1536, -158.2818, 362.6096` | `25.98` | `300.00` | `1.0000` |

Three references in that cell framed at their own scale: `Inst305Bulb05` bounds `[-21, -15, -14, 9, 15, 16]`
radius `25.98` distance `62.35`, `DefaultLightGreenFungus01NS` radius `20.15` distance `48.36`, and
`MS14TrailerMarker` radius `27.37` distance `65.68` — the ratio held at `2.4` every time.

Framing also fits the lens to the box, because a standoff that clears the object still clips it if the near
plane sits further out. `ck.frame` writes the near plane at `distance - 1.5 × radius` (floored at `1`) and
reports `near`, `nearRead`, `far`, `farRead` and `farHeld`:

| Reference | `radius` | `distance` | Near plane | Box front | `far` / `farRead` | `farHeld` |
|---|---|---|---|---|---|---|
| `Inst305Bulb05` | `25.98` | `62.35` | `23.3827` | `36.37` | `114.3154` / `300000` | `false` |
| `MS14TrailerMarker` | `27.37` | `65.68` | `24.6311` | `38.31` | `120.4186` / `300000` | `false` |
| `DefaultLightGreenFungus01NS` | `20.15` | `48.36` | `18.1345` | `28.21` | `88.6575` / `300000` | `false` |

The near plane sits inside the box's front face in every case, so the framed object is not cut off, and a
later `mode=lens` read still shows `23.3827` — the fit survives. The far plane it asks for
(`distance + 2 × radius`) does not: the editor replaces it with its own `300000` every frame, which already
covers any interior cell, and `farHeld` says so rather than a reply claiming a plane that is gone.

Earlier measurements on `AndrewStation01` used the old fixed standoff and are still reproducible by passing
it: `frame Loot_Prewar_Safe 256` reaches `-216.4394, 9492.1836, 272.3472`, and
`frame Loot_Prewar_Safe 512 1.5708` reaches `39.5606, 8980.1836, 272.3472`, both `aim 1.0000`.

The `aim` the tool reports is not graded on the matrix it just wrote. It is read from the **scene camera**
— the `NiCamera` at `camera+0x10`, which the editor copies the node into every frame and which is what
actually renders — so the number means the editor took the pose. After the yaw-90° frame that row is
`(0, 1, 0)` while the unit direction to the object is `(0, 1, 0)`, dot `1.000000`; the memory probe reads
the same row from outside the process and agrees.

An unmatched name comes back with candidates from the loaded cell (`no reference in the loaded cell
matches X among 3616 references; for example: Loot_Prewar_Safe, MS07cCompanionMarker, ...`), because a
name the caller cannot discover is not a usable tool.

### Interior against a scaled worldspace, measured

`DiamondCityFX` is a worldspace whose contents are scaled, so it is the case where a hidden scale factor
would break the pose math. It is only reachable after the Cell View's combo is switched to it
(`ui.select <combo> 4`, see *Reaching an exterior worldspace by name*), and a cell row is then selected
and double-clicked. Cell `00000FC4` ("Wilderness", grid `0, 0`) is the first one with content.

| | `AndrewStation01` (interior) | `DiamondCityFX` (exterior, grid `0, 0`) |
|---|---|---|
| `camera get` `scale` | `1.0000` | `1.0000` |
| probe `0x70` scale word | `1` | `1` |
| node rows, yaw 0 | `[[0,-1,0],[1,0,0],[0,0,1]]` | `[[0,-1,0],[1,0,0],[0,0,1]]` |
| scene rows, yaw 0 | `[[1,0,0],[0,0,1],[0,-1,0]]` | `[[1,0,0],[0,0,1],[0,-1,0]]` |
| `frame` distance asked / reported | `256` / `256.00` | `512` / `512.00` |
| `aim` | `1.0000` | `1.0000` |
| `rotationOffset` | `6.0000` | `6.0000` |
| `camera-tool-cross-check.ps1` | PASS | PASS |

The same pose gives the same matrices in both worlds, and the camera's world transform carries `scale 1`
in both, so no world scale reaches the pose math: positions, yaw and standoff distances are plain world
units. The reported offset is not a scale artefact either — `6.0` is `Σ|R - S|` at an axis-aligned pose,
and it matches in both worlds because both poses are axis-aligned.

Limits of that finding: `DiamondCityFX`'s cells hold one or two references each (`ProceduralWater` and a
landscape), so nothing scaled was framed there — only the worldspace's own transform chain was measured. A
scale applied to geometry or at render time, rather than to the camera's parent, would not appear in any of
these readings, and the rendered image was not inspected.

Reproduce with `tools/camera-tool-cross-check.ps1` in each world; it reads the pose back through the tool
and compares the node matrix with the scene camera's, so the numbers do not come from the plugin's own
decode.

### Aiming up and down, measured

`ck.aim` keeps the camera where it stands and turns it until the target sits on the scene camera's view
row. The pitch is computed from the position the camera ends up at, so a standoff does not tilt at the old
distance, and a target directly above or below — where there is no yaw to derive — keeps the current
heading and tilts to the pole instead of refusing. `Loot_Prewar_Safe` at `39.5606, 9492.1836, 272.3472`:

| Camera | Request | Wrote | `aim` |
|---|---|---|---|
| `39.5606, 9492.1836, 600` | `aim Loot_Prewar_Safe` | pitch `-1.5708` | 1.0000 |
| `39.5606, 9492.1836, -100` | `aim Loot_Prewar_Safe` | pitch `1.5708` | 1.0000 |
| `39.5606, 9292.1836, 0` | `aim Loot_Prewar_Safe 256` | camera `39.5606, 9236.1836, 0`, pitch `0.8163` = `atan2(272.35, 256)` | 1.0000 |
| `-300, 9200, 0` | `aim Loot_Prewar_Safe` | yaw `0.7105`, pitch `0.5463` | 1.0000 |

Before this, the same standoff reached only `aim 0.8482` (`aligned: false`), because the pitch came from
the camera's pre-standoff distance, and a target straight overhead was refused with `too_close`. That code
now means only that the camera is on top of the target, where no direction exists.

A chain aims at each reference in the order given, from the position the camera already holds:

| Request | Reply |
|---|---|
| `aim MS07cCompanionMarker,DefaultEnableDisableTrigger` | `steps: 2`, step pitches `-0.0897` and `-0.0937` (each its own target's angle), final `aim 1.0000` |
| `aim Loot_Prewar_Safe,ZZZNoRef` | `ref_not_found`: `step 2 of 2 (ZZZNoRef): …` |
| `aim Loot_Prewar_Safe,MS07cCompanionMarker 256` | `bad_args`: a chain rotates in place, and a standoff moves the camera |

### Camera move and lookat, measured

- **Move** is `MoveLocal`, so the step is in the camera's own axes: from a yaw-90° view, stepping
  `0 200 0` moved the position from `(158, -359, 2691.9175)` to `(158, -359, 2891.9175)`, exactly
  +200 on world Z, which is the camera's right at that yaw.
- **Lookat** does point at the target. From `(39, 9492, 272)` at yaw 0 (view `+X`), `lookat 1000 2000
  3000` leaves the view row at `(0.1197, -0.9329, 0.3397)`, which is the unit direction from the camera
  to that point, to four decimals. The earlier reading that it missed came from taking row 0 of the camera
  object — the right vector — as the view; the view is row 1 of that matrix, which is row 0 of the scene
  camera's.
- `lookat` leaves the editor's orbit focus alone: every float in the camera object is unchanged by the
  call, and only the node and scene camera rotations move. The focus does exist — `GetPivot`, `HasPivot`,
  `SetPivot(point, scale, reposition)` and `ClearPivot` are all in `BGSRenderWindowCamera` — it is simply
  not what `lookat` writes. Setting it is the orbit tool's job, below.
- `ck.camera mode=lookat` still reports `via: lookat-computed` on this build. It calls the editor's
  `LookAt` first, but the node's world rotation does not show the new pose inside that same call, so the
  verification fails and the tool writes the pose it computed from the camera position and the target —
  the arithmetic above, measured against the node afterwards. The reply names which path aimed the
  camera so the two are never confused.

### Rotating the camera

`mode=rotate` drives the editor's own `BGSRenderWindowCamera::RotateBy`, which takes the steps as drag-like
units rather than radians: measured, `100` units is `1` radian, `x` is pitch, `y` is roll and `z` is yaw, and
the camera does not move. That makes it the reference an orbit step can be checked against, and the three
paths agree exactly at the same pose:

| Path | View row | Reads as |
|---|---|---|
| `set ... yaw 0.5` | `0.8776, 0.4794, 0.0000` | yaw `0.5` |
| `rotate 0 0 50` | `0.8776, 0.4794, 0.0000` | yaw `0.5`, `deltaYaw 0.5`, `moved 0` |
| `orbit ... angle 0.5` at radius `120` | `0.8776, 0.4794, 0.0000` | `aim 1.0000` |

`rotate 5 0 0` reads `deltaPitch 0.05` and `rotate 0 0.5 0` (roll) leaves yaw and pitch alone while moving the
matrix by `0.010025`, so the reply reports `matrixDelta` and `changed` from the whole matrix rather than from
yaw and pitch. A step that leaves the view where it was fails as `rotate_noop`, and a zero step is
`bad_args`, so a silent no-op cannot be mistaken for a rotation.

### Field of view

The scene camera's frustum holds the tangents of the half-angles, so the field of view is `atan` of them, and
that is what `mode=lens` reports. Measured on `DmndRadio01` at the default view: `frustum`
`[-0.83910, 0.83910, 0.53950, -0.53950, 10, 300000]` reads as horizontal `80.0000` and vertical `56.6941`
degrees. Setting `fov 30` writes `[±0.41675, ±0.26795]` and reads back exactly `30`, `fov 90` reads back
`90` with horizontal `114.5215`, and `fov 56.6941` restores the default frustum. `zoom 2` halves the field
of view to `28.3471`, `zoom 0.5` doubles it back to `56.6941`, and a factor of `0`, `-1` or `30` is refused,
as is a field of view at or outside `1` and `179` degrees.

The reply also carries `near`, `far` and `zoom` as the lens stands: `10` and `300000` at the default view, and
`zoom` is `56.6941 ÷ fov`, so the default reads `1`, `fov 30` reads `1.8898` and `zoom 2` from `fov 30`
reads `3.7796` because it lands at `fov 15`. `zoomRequested` keeps the factor that was asked for, so a zoom
that lands somewhere else is visible in the same reply instead of being assumed.

The far plane belongs to the editor. Writing `114.3154` reads back immediately, but the next `mode=lens`
read shows `300000` again, with the planes written before the pose or after it — while a written near plane
(`23.3827`) is still there on that same read. That is why `ck.frame` fits the near plane and reports
`farHeld` rather than assuming a far plane it set survived.

Two limits are worth stating. The editor's own `Zoom` call is not used: measured with and without a pivot
set, it left the frustum unchanged, so a zoom here is a field-of-view scale and a factor above `1` zooms
in. And the linear aspect `(right-left)/(top-bottom)` is preserved, not the angular one, so at `fov 30` the
horizontal field is `45.2474` rather than the `42.3` that scaling both angles would give. An orthographic
view is refused with `not_perspective`; that path is implemented but was not exercised, because the render
window stayed in perspective.

The tool and the probe read different nodes, which is why their matrices are not equal. `camera get`
reports `rotate` from the camera's own node — the matrix `MoveLocal` moves along, confirmed by
`camera move 0 256 0` stepping to world `0 -256 0` at that orientation. The probe reads the slave
`NiCamera` at `camera+0x10`. Measured on 1.11.246.0, the slave's rows are the node's rows shifted by one
(node `r1, r2, r0`), and the tool's `rotationOffset` is the sum of the differences that shift implies —
6.0 at the axis-aligned poses first measured, 6.4472 at a diagonal one, so the shift is the invariant and
the number is not.

`tools/camera-tool-cross-check.ps1` asserts that relation across a sweep rather than at one pose: 11 yaw
values (0 through 315 degrees) against 5 pitches (-1.0 to 1.0 rad), 55 orientations in all. At each one it
recomputes the offset from the node rows and the shift and compares it with the number the tool reports,
checks the view row is node row 1, checks that row carries the requested yaw and pitch, and checks the
translation both nodes share. Measured over the whole sweep: worst offset error `0.0002`, worst component
difference between the view row and node row 1 `0`, worst forward error `0.000049`.

`tools/live-evidence.ps1` runs both live checks in one pass and writes one file. They need opposite editor
states — the modal probe opens `File/Data`, which the editor greys out while a cell is open, and the sweep
needs a cell — and the probe closes a loaded cell itself (`ui.cellclose`, which clears
`BGSRenderWindow+0xF8` and re-reads it) and reloads the same cell afterwards, so the wrapper always runs the
probe, ensures a cell, then the sweep. Measured on `DmndRadio01` with the cell already open: the probe closed
it (`was 0x1E3D5A1D330`, `loadedAfterClose false`), found and dismissed the hidden `Data` dialog, restored the
cell (`status ok`), and the run reported `blockedModal: pass`, `cellLoad: exit 0`, `cameraCrossCheck: pass`,
exit `0` into one file. The close is checked rather than assumed: a write that does not reach the render
window leaves the menu greyed, and the probe stops with exit `2` instead of pretending `File/Data` is
available. `status.loaded` is not the test for that — it reads the TES placement, which stays after a close.

`camera get` reports both matrices, so the difference is visible without a probe. On `DmndRadio01` at yaw 0
the node rows are `(1,0,0) (0,-0,-1) (0,1,-0)` and `sceneRotate` holds `(0,-0,-1) (0,1,-0) (1,0,0)` — the
node's rows in the order `r1, r2, r0`, which is what the sweep then checks componentwise at all 55 poses
(worst difference `0`).

The sweep is what makes the assertion meaningful. A shift that is wrong still predicts `6.0` at an
axis-aligned pose, so a single pose proves little: with the shift removed the prediction collapses to `0`
where the tool reports `6.5027`, a comparison against row 0 differs by `1.3276`, and comparing `sceneRotate`
with the unshifted node rows differs by `1.3276` as well, all rejected. The
rows print to four decimals, which is why the comparison is componentwise: the dot of `0.6205, 0.6205,
0.4794` with itself is `0.99986`, not `1`, even though the two rows are identical. It needs the editor and
the plugin running, so it stays a local check rather than a CI one.

The CKAgent repo carries the plugin half of this check as `tools/camera-layout.test.ps1` and runs it from
`.github/workflows/camera-layout.yml` on every pull request, including two steps that drift the offset and
the stride on purpose and fail if the test still passes.

`tools/camera-layout.test.ps1` guards this layout. It holds the measured offsets as
expected values and fails if the plugin's constants drift from them (world `0x70`, translate
`world+0x30`, scale `world+0x3C`, stride 4), if the plugin stops addressing columns through the
stride, or if the probe starts decoding a different row width. With `-Live` it also runs the probe
and requires all three transforms plus orthonormal rows, which a wrong stride cannot produce. Aliases
are expected and reported rather than failed: with an axis-aligned rotation, a read 4, 8 or 12 bytes
before `0x70` (here `0x64/0x68/0x6C`) is still orthonormal.

Basis, as measured on 1.11.246.0:

- `S = L·R`, where `S` is the scene camera's world matrix at `0x70`, `R` the camera node's, and
  `L = [[0,1,0],[0,0,1],[1,0,0]]` the camera's fixed local basis. `L` is a row shift, not a constant: the
  `rotationOffset` the tool reports is `Σ|R - S|`, which is 6.0 only when the pose is axis-aligned and
  6.4472 at a diagonal one. The shift is the invariant.
- The view direction is **row 0 of `S`**, which is **row 1 of `R`**. `LookAt` at `+X`, `+Y` and `+Z`
  each put the target direction in that row and nowhere else, and the probe confirms it, so the forward
  axis is measured, not assumed. Every aim the tools report is graded on row 0 of `S`, because `R` is the
  matrix they wrote themselves.
- `+yaw` is the direction the camera looks, as an angle in the world `XY` plane: yaw 0 → view `+X`,
  yaw 90° → view `+Y`, both confirmed through the probe's rows.
- `+pitch` tilts the view up inside the forward row: yaw 0 with pitch 0.5 rad → view
  `(0.8776, 0, 0.4794)`.
- `MoveLocal` displaces along the node's own rows, so it can be used to check which row the engine moves
  on but must never be used to infer which way the camera faces.

## Decided

- **CKPE is in.** It is the only route to the live scene, it is what Creation Kit users install
  anyway, and the alternative is re-deriving per-build offsets ourselves.
- **Pinned to editor `1.11.246.0`**, the build CKPE's headers are currently annotated for. The
  plugin's `CKPEPlugin_Version` refuses other builds.
- **LGPL-3.0 handling:** the plugin links CKPE's libraries dynamically and we distribute our own
  source; we ship installation instructions rather than CKPE itself, and we keep CKPE headers out of
  the MCP server so only the plugin is a derived work.

## Still open

- ~~Does the Object Window list use `LVS_OWNERDATA`?~~ Answered in-process: the Cell View list is a
  plain `SysListView32`, and `LVM_GETITEMCOUNT` / `LVM_GETITEMTEXTW` return all 324 interior cells.
  Cross-process reads are no longer needed, since the plugin covers both directions.
- Whether the global that holds the `TESRenderControl` instance stays put across cell loads, or
  whether a resolved instance has to be re-checked after a worldspace change.
- Should `ckagent` grow from the `BGS/tools/mcp` .NET host, or live as its own project?
