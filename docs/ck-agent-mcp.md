# Controlling the Creation Kit from an MCP agent

> This copy ships with the CKAgent repository, and so do the CKPE anchor findings it cites
> (`docs/ckpe-render-control.md`). The memory probes it names live in the GARDEN-Tools monorepo under
> `tools/`, because they read the editor from outside the process. Build steps are in the
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

Everything labelled "measured" below came from `tools/ck-surface-probe.ps1` run
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

Measured again in-process on 1.11.246.0 (`ui.menu` on the frame): 11 top-level entries — FILE, EDIT,
VIEW, WORLD, NAVMESH, VISIBILITY, CHARACTER, GAMEPLAY, HELP, the CKPE entry and MCP — and no item named
"Warnings" anywhere in the bar, so 41037 is not reachable on this install and `ui.command Warnings`
answers `no_command`.

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
| Log Window | `RTEDITLOG` | `RICHEDIT50W` (its window text is the whole log) |
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
window publishes its instance through `GWLP_USERDATA`. `tools/render-control-probe.ps1`
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

Every pipe call carries a deadline (15 s), because the editor answers its commands from the UI thread: a
command it starts to run inline holds the read open, and the tool used to sit there until the caller's own
client timeout fired. A call that misses the deadline now answers `plugin_busy` with the command and the
deadline, so a busy editor reads as busy and the call is repeated when it is idle. This is what keeps a
cover pass from being lost: `mode=cover` waits its own bounded budget and answers `posted`, and
`cover=status` then reads the counts and saves the cell, so the pass it reads is the pass that reaches disk.

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
tools/ck-surface-probe.ps1

# or start one, probe it, then stop what it started
tools/ck-surface-probe.ps1 -Launch -Seconds 120
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
| `ck.status` | editor process, main window, installed build, manifest age, plugin pipe presence |
| `ck.commands` | capture the live menu tree into `<state>\commands.json`, or return the cache |
| `ck.invoke_command` | resolve `File/Save`, `World/PreCombine Geometry For World` or an id, then `WM_COMMAND`; `dry_run` resolves only; refuses while a modal dialog has the window disabled |
| `ck.save` | copy the plugin to `<state>\backups\<stamp>\`, post `File/Save`, then confirm the file changed — and report `save_not_observed` rather than claiming success |
| `ck.camera` | read or set the camera pose through the plugin. Reads and writes `BGSRenderWindowCamera` (window at `0x143D491E8`, camera object at `+0xC0`) via its own `SetPosition`/`SetRotation`, because the `NiCamera` at `+0x10` is a slave the editor overwrites every frame — a write there is reverted within 400 ms (measured). Reports `positionSynced` (the scene camera's translation matched the request), `rotationOffset` (the pose-dependent `Σ|node − scene|`, since the scene camera's rotation is the node's shifted by one row), and for a lookat the `forward` row and the achieved `aim` plus a `via` naming whether the editor's own `LookAt` or the computed pose aimed it; `mode=orbit` circles a point at a given distance and angle, sets the editor's pivot to that point, and reports the pivot it read back, the radius and the aim; `camera_transform_unverified` until a cell gives a usable transform; `CKPEUnavailable` without the plugin |
| `ck.aim` | turn the camera on a named reference without moving it, so an object is inspected from the view already chosen; optional `distance` backs off to that standoff first; reports the yaw and pitch it wrote, the achieved aim and the vertical offset, and says `aligned=false` when the view still misses |
| `ck.cells` | list the Cell View's cells as EditorID, FormID, name and row index, optionally filtered — so a cell can be chosen by name instead of by index |
| `ck.cell_load` | open a cell by EditorID or display name through the Cell View's own `NM_DBLCLK` handler; reports the item it opened |
| `ck.cell` | the cell the render window holds, by EditorID and form id, so a load is confirmed from the reply instead of inferred from a pointer |
| `ck.frame` | load a cell and stand the camera in front of one of its references (`reference` = reference EditorID, base object EditorID or 8-digit form id; optional `cell`, `distance`, `yaw`), so an object is inspected without navigating by hand; reports the match, the target, the pose and whether the object sits ahead along the view row |
| `ck.status` (`blockedBy`) | names the hidden modal that has disabled the editor, so a blocked editor is not mistaken for a hung or idle one; every plugin tool reads it after a failure, dismisses that dialog with `ui.dismiss`, retries, and only reports it by name and handle if the block survives. The reply keeps the original failure code and only the message gains the block, saying whether the dialog was dismissed but the block persisted, or could not be dismissed at all. `tools/blocked-modal-probe.ps1` covers both outcomes against a live editor instead of by hand; the still-blocked one is forced with `CKAGENT_TEST_DISMISS_FAILS=1` because a dialog the server does handle is gone by the time the call returns |
| `ck.ui` | passthrough to the in-process control tools (`ui.items`, `ui.select`, `ui.notify`, `ui.command`, `ui.click`, `ui.windows`, `ui.tree`, `ui.menu`): `method=ui.notify`, `args=0x16C1752 dblclk 2` |
| `ck.resolve` | ask the plugin for one fresh resolution of both anchors and return the addresses, or the reason nothing validated — the refresh to call after loading a cell |
| `ck.navmesh` | create, apply and inspect navmesh, and sweep a whole plugin: `mode=audit` walks the Cell View (`filter`, `after`, `limit`, 5 by default) and answers per cell whether it carries navmesh, its forms, vertices, triangles, cover triangles and whether it needs work (no navmesh, no triangles at all, or a form that could not be read - a build leaves an empty placeholder form behind, so an empty form on its own is not work), leaving the last cell loaded; `mode=batch` runs the whole pipeline on each listed cell through the same stages as `mode=apply` and saves only the cells whose check came back clean, one cell per call by default so a long sweep advances with `after`; `mode=cover` posts the cover group's cell command (`cover=generate` is `Find Cover Edges`; `generate_all` opts into `Generate Cover For All Spaces`, which sweeps the whole load order) or `clear` / `clear_generated`, then waits up to `wait_seconds` (default 20, limit 60) for the counts to read back, because those commands answer nothing and compute on the editor thread; a pass that outlasts that budget answers `posted` with the counts from before it and `cover=status` reads them once the editor is idle and saves the cell, so a long cover run is resumed instead of lost to a client timeout, and a finished pass is never left only in memory; `mode=apply` posts generation, then Finalize Cell NavMeshes, then Check NavMeshes, and saves the cell with the editor's own File/Save command only when that check reported no failure; a check that wrote nothing is accepted only when the plugin's own verdict is clean and the cell's geometry digests corroborate it, and the reply's `check` block says which of the two it was (`silent`, `corroborated`); `dry_run=true` resolves the steps and the save command and posts nothing; `mode=generate` runs the generation on its own and waits for it properly: it posts the generator's caption, waits for the dialog the editor opens after the reply, presses `OK`, then polls until the count settles (`wait_seconds`, default 240, limit 900) because a build empties the arrays first and writes them minutes later; the answer carries the counts before and after and the time taken, and a cell that reads 0 for the whole window answers `generation_incomplete` instead of claiming success; `mode=batch kind=mesh` is the worldspace sweep: one invocation walks the Cell View rows for the selected worldspace, and for each row proves the load by form id, strips the cell, runs one Recast build, and answers per cell with the strip reply, the generate payload, the counts read back from `nav.state`, and `agree`; a row that does not load or does not become the requested form is a named per-cell failure (`load_timeout`, `form_mismatch`) and the pass continues. `mode=batch kind=mesh` saves what it generates: each agreed cell runs the editor's own Save and answers `persisted` plus a `save` block (status, code, file, path, back-up, bytes before and after, `savedAtUtc`, elapsed). A cell whose save did not reach disk is reported `save_failed`, a cell whose mesh does not read back out of the written file is reported with the read-back's own code (`vertex_mismatch`, `triangle_index_mismatch`, `geometry_short`, `record_mismatch`, `cell_missing`, `readback_unreadable`), and either way the batch is `incomplete` - a mesh only the editor holds is never called delivered. Saving a loaded master cannot happen in place: the editor opens a **Select Target Plugin** file dialog, which this tool answers by typing `save_as=<file>` into it and pressing Save. The tool answers that dialog itself: the dialog's name box is a DirectUI element with no window handle, so `ui.type`'s key events leave it empty, and `tools/uia-target-plugin.ps1` runs UI Automation outside the editor - where it can reach the dialog the editor cannot reach from inside its own process - and selects the target plugin in the dialog's list, with the plugin's `uia.save` (value pattern plus `Invoke`) as the in-process fallback. No one types the name by hand. **A save is then verified rather than assumed.** `mode=apply`, `mode=cover` and `mode=batch kind=mesh` snap the `Data` directory before posting File/Save, wait for the plugin that changed, and read that file back without the editor: the record tree is walked, each record's zlib payload is decompressed, and the `NVNM` geometry is decoded at its version-15 layout (version, pathing-cell CRC, world link, parent word, then the counted vertex, triangle, edge-link and door-link arrays). Vertices are 12 bytes each; triangles are 21 bytes - three `u16` vertex indices, three `i16` edge links, `f32` clearance, one traversal byte and a `u32` flags word - which is what the committed fixture's bytes show: at that stride all 529 triangle indices of `CKGardenNav.esp` fall inside their record's 455 vertices, and the edge-link and door-link arrays that follow parse to zero with a cover/grid tail left over. **The comparison is the geometry, not a count.** `nav.state` digests each readable form's vertex array and its triangle indices (FNV-1a 64, `verticesHash` and `trianglesHash`), and the file side hashes the same bytes with the same function; `plugin/NavmeshDigest.h` holds that function for the plugin and asserts the published `abc` vector at compile time, so a drift between the two sides fails a build instead of a save verdict. The reply's `persistence` block carries `expected` (the editor's per-record digests), `readBack` (the file's) and one code per offending record: `persisted` when every record matches; `vertex_mismatch` or `triangle_index_mismatch` when a record's positions or indices differ, naming its NAVM form id; `geometry_short` when a record is missing or its counts differ, also named; `record_mismatch` when the file carries a NAVM the editor did not build; `save_not_written` when no plugin changed after File/Save was posted; `cell_missing` when the file has no `NAVM` for the cell; and `readback_unreadable` when a record or its payload could not be read. Only `persisted` answers `saved`; every other code leaves the cell unfinished. A single flipped triangle index, and a half-unit vertex shift, both keep every count intact and both fail this read-back - they are covered by the suite against a tampered copy of the real fixture. `mode=cover` reaches `saved` through the same step, so a cover pass that finished is written and verified rather than left in memory; `mode=batch kind=mesh` runs the same read-back per cell and counts a cell as meshed only when it matches. What is still not compared: the cover, waypoint and grid sections that follow the geometry, and the mesh grid. No editor restart is needed, because the comparison reads the file the kit wrote.

Rows are opened by their own list index (`ui.notify <listview> dblclk <row>`), not by EditorID, because exterior cells share EditorIDs: `DiamondCity` holds several cells named `Wilderness`, and `ui.cellload Wilderness` always opens the first of them. `mode=load` takes the same addressing for a single cell (`row=<index>` or `form_id=<FormID>`); a row that cannot land answers `load_timeout`, measured with `row=9999` on `Wilderness` in `DiamondCity`. A build is limited by its wait window alone: the pipe can go silent for tens of seconds while the editor builds, so a quiet pipe no longer ends the wait early - `DiamondCityUpperStands` hit exactly that, a momentary `CKPEUnavailable` at 102 s while the build was still running. The per-row check still proves the load by form id against `nav.state` before any geometry is read, so a row that lands another cell is `form_mismatch` and a row that never lands is `load_timeout`, both named with the cell and form id. The envelope carries that audit per cell (`audit`: cell, formId, interior, references, forms, vertices, triangles, loaded), so one invocation proves payload, state and audit agree - no `mode=audit` call afterwards. Measured: `kind=mesh filter=Wilderness limit=2` on `DiamondCity` meshed both `Wilderness` rows, `00240045` (0/0 after strip, 452/529) and `01000F9D` (0/0, 453/530), `agree=true`, `loaded=true`, `interior=false`, each with its own form id.

Measured: `kind=mesh filter=DiamondCityOutfield limit=2` meshed `DiamondCityOutfield01` (form `00000FE9`, 0/0 → 456 vertices / 462 triangles) and `DiamondCityOutfield02` (`00000FEA`, 0/0 → 711 / 751), both `agree`; `filter=Wilderness limit=2` meshed the first row (`00240045`) and answered `load_timeout` for the second (`001893CD`), because several Commonwealth cells share the EditorID `Wilderness` and a name cannot address one of them - the failure was named and the first cell kept its mesh. A city exterior build is slower than the interiors: `DiamondCityUpperStands` (785 references, form `00000FDA`) took 69 s from stripped 0/0 to 152 vertices / 135 triangles, and the wait now survives a busy editor instead of reporting `generation_incomplete` while it is still building. `mode=load` takes `cell=<EditorID>` and, for an exterior, `worldspace=<name>`: the Cell View lists one worldspace at a time through its combo (`Interiors`, `Commonwealth`, `SanctuaryHillsWorld`, ...), so the tool selects that worldspace, opens the cell, then polls `nav.state` until the loaded form id is the requested one, answering `no_worldspace` when the view has no such worldspace and `load_timeout` when the cell never becomes the loaded one. `worldspace` also rides along on `mode=audit` and `mode=batch`, which report `interior` and `references` per cell so exterior rows are distinguishable in the same table; measured on `PrewarSanctuaryExt01` (form `000A801D`, 307 references, exterior, landscape): `mode=strip` cleared 2 forms / 400 vertices, `mode=state` then read 0 vertices and 0 triangles, and one `mode=generate confirm` call built 1067 vertices / 1168 triangles in 15 s, with `mode=state` and `mode=audit` reporting the same 3→4 forms and counts for that cell. `mode=strip` zeroes the loaded cell's navmesh arrays through the plugin (measured: `{"stripped":2,"verticesCleared":8}`, then `mode=state` reads 0 triangles), which is the only way to empty a cell on purpose because no NAVMESH caption does it outside the edit session; a cell emptied that way then goes to a real mesh in one `mode=generate` call (measured on BostonAirportAliasCell: before 2 forms / 0 vertices / 0 triangles, after 3 forms / 42 vertices / 34 triangles in 15 s, no dialog), and `mode=state` and `mode=audit` then report those same 42 vertices and 34 triangles on the same forms; `mode=finalize`, `mode=check` run one step; `mode=run` executes one named navmesh command (command-level edits); `mode=state` reports presence and coverage with each form's real vertex and triangle counts (`coverage.geometry` = `form_arrays`); `mode=commands` lists the live NAVMESH menu with ids and enabled state. Every mutating mode needs `confirm=true`; `mode=check` and `mode=apply` answer with a structured per-cell verdict parsed from the check's log lines instead of those lines, and both resolve that report through one gate: a check that wrote no findings counts as `clean` only when the plugin's own verdict is `clean` and `nav.state` reports the cell's geometry digests, and either answer carries the `check` block (`clean`, `silent`, `corroborated`, `reason`) that says so. `mode=apply` drops the navmesh form the generation emptied (`nav.prune`, same confirm token) before it runs the check, so repeated applies in one editor session stop at the cell's own records plus the mesh instead of leaving one placeholder per run. A cell is identified by its form id, not its Cell View row: the view lists a dirty cell twice (`BostonAirportAliasCell` and `BostonAirportAliasCell *`, both form `00172297`), so `mode=audit` and `mode=batch` merge those rows, report the name without the marker, and refuse to read geometry from a row that did not become the loaded cell (`load_timeout`), because `ui.cellload` answers before the render window settles - a swept cell that silently failed to open used to be reported under the previous cell's geometry. Geometry is always read from the cell's own `coverage` block, the one `mode=state` reports, so the three answers cannot disagree about one cell. Measured on three stripped interiors in one `mode=generate` call each: `DmndValentines01` (346 refs) 0/0 -> 99 vertices / 101 triangles, `InstituteBioScience` (1566 refs) 0/0 -> 1046 / 1126, `InstituteReactor` (2364 refs) 0/0 -> 971 / 1029, with `mode=state` and `mode=audit` reporting the same forms and counts for each. Resolving by caption is the point: the recorded ids belong to 1.11.246.0 alone. No NAVM payload is read or edited |
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
| `ui.cells [filter]` / `ui.cellload <name>` | name-addressed cell list, and open one |
| `ui.dataload <plugin>` | tick a master file in `File > Data` (two calls: the command, then the dialog); `ui.dataload <plugin> active` selects that plugin and presses `Set as Active File`, which is what makes a master cell writable |
| `nav.state` | the loaded cell's navmesh presence and coverage from its own forms: form count, each form's id, navmesh id, parent cell and vertex/triangle counts, plus a coverage block that sums those counts and carries the measured triangle area (`area`, `areaTriangles`). Every form whose arrays could be read also carries `verticesHash` and `trianglesHash`, the FNV-1a 64 digests the save read-back compares |
| `nav.commands` | every navmesh entry in the live menu, with its path, id and enabled state |
| `nav.check` / `nav.finalize confirm` | post Check NavMeshes / Finalize Cell NavMeshes for the loaded cell; check answers with the per-cell verdict, finalize is refused without `confirm` |
| `nav.check` report | staged like `nav.apply`: the first call posts the command and records the report offset **before** the post, later calls read what the check appended and answer `pending` until the report goes quiet, then `done: true` with `lines`, `report.source` and `report.startOffset`, plus `verdict`, `counts` and `findings` (below). The verdict is computed from those lines, so it no longer depends on a log slice read before the check ran |
| `nav.apply` verdict | the `done` payload carries `verdict`, `counts` and `findings` at its top level, classified from the same captured lines `nav.check` reports, so a caller reads the result of the run without parsing anything. `ck.navmesh mode=apply` keeps its own structured `verdict` and adds `pluginVerdict` beside it, and refuses to save when the two disagree (`verdict_mismatch`), because a cell whose result only one classifier believes is not a result |
| `nav.apply abort` | drops a staged run and answers `dropped`, `dismissedBy` (`cancel`, `accept` or `null`), the dialog it found and the steps that ran. Recovery, not a step: it needs no `confirm`, and it exists because a run that stalls mid-stage used to leave the editor modal until it was answered by hand. Every failure path in the pipeline abandons the same way — dismissing the generation dialog it opened (cancel preferred, accept when there is no cancel button) and dropping a check capture it opened but never posted — so the next `nav.apply` starts at the generation instead of resuming the dead run. `ck.navmesh mode=abort` exposes it, and `mode=apply` calls it itself when a run times out at its tick limit or the pipe call fails |
| `nav.check` findings | the reply classifies on the plugin side as well, so a raw pipe client sees the findings without the server: `verdict` (`clean`, `warnings`, `errors`, `unknown`), `counts` (`errors`, `warnings`, `recognized`, `reported`) and `findings[]` of `{severity, cell, navmesh, message, line}`. `cell` and `navmesh` are `null` when the message carries neither. A report with nothing recognisable in it stays `unknown` rather than `clean`. The fragment table in `plugin/AgentNavmeshFindings.cpp` is checked against `NavmeshVerdict.cs` by `tools/navmesh-command.test.ps1`: a server rule the plugin does not recognise, a fragment no server message writes, or a fragment severity that disagrees with the server's all fail the static pass |
| `nav.generate <kind> confirm` | post `Generation/<kind>` (recast, havok or advanced) for the loaded cell, then press the dialog that command opens and wait for it to close, because the post alone generates nothing |
| `nav.apply <kind> confirm` | post generation, accept its dialog, wait for the close, then Finalize Cell NavMeshes, then Check NavMeshes, in that order, stopping at the first refusal; fails with `no_geometry_change` when the cell's own vertex and triangle counts did not move. It is a **staged** pipeline: each call advances one stage (`generate`, `accept`, `settle`, `finalize`, `check`) and answers `pending: true` with the steps done so far, and the last call answers `done: true` with the before and after totals. No stage sleeps, because the handler runs on the editor thread. Its check stage opens the same capture `nav.check` uses before it posts, so the final reply carries `report` with the check's own `lines`, and both modes classify the same lines instead of apply re-reading the log |
| `nav.plan <kind>` | resolve those three commands plus `File/Save` and report each path, id and enabled state, without posting anything |
| `nav.save confirm` | post the editor's own `File > Save`, resolved by menu path, so the cell is written by the editor; refused without `confirm` |
| `nav.run <caption> confirm` | post one named navmesh command from the live menu, resolved by caption, which is the command-level edit surface |
| `nav.state` cover fields | `coverage.edgeCover` counts cover per edge from the triangle flags word: `edge01`/`edge12`/`edge20` are triangles whose mask bit `16`/`17`/`18` is set, each named after the link it sits on (`EdgeLink_0_1`, `EdgeLink_1_2`, `EdgeLink_2_0`) so the key needs no bit-to-edge mapping, `any` is a triangle with at least one, `all` is a triangle with all three, `openEdges` counts masks sitting on an edge whose link is `FFFF`, and `heightSet` counts triangles whose `+0x0C` cover height is not the `FLT_MAX` sentinel. Counted only over the triangles the area walk accepted, so `edgeCover.any` never exceeds `areaTriangles` |
| `nav.state` per-form cover | each readable form carries `cover` with those same seven fields, measured from its own triangles, so a cell holding several navmeshes can be compared mesh by mesh instead of only in total. It is `null` for a form whose geometry is `empty` or `unreadable`, where no triangle was counted. The forms must sum to `coverage.edgeCover` field for field, because both walks accept exactly the same triangles |
| `nav.state` geometry fields | every form carries `"geometry": "read" \| "empty" \| "unreadable"`; a Recast pass empties the forms it replaces, so those report real zeros (`vertices`, `triangles`, `area`) with `geometry: "empty"` instead of `null`, and coverage counts them in `readForms` / `emptyForms` / `unreadableForms`. A readable form also carries `verticesHash` and `trianglesHash` (16 lowercase hex digits); both are `null` for an emptied form, whose arrays hold nothing to compare |
| `nav.accept` | find the generation dialog (visible or hidden), press its `OK`, and return at once; the pipe handler runs on the editor's UI thread, so no call may sleep or the dialog can never open |
| `place.commands` | the Edit menu's placement commands this build offers (`Duplicate`, `Paste Render`, `Paste in Place`, `Copy Render`) with ids and enabled state |
| `place.duplicate confirm` | run `Edit > Duplicate` on the current render-window selection and report the cell's reference count before and after; a greyed item is `command_disabled`, an unmoved count is `no_change` |

Measured flow (editor 1.11.246.0, master `Fallout4.esm`): `ui.cellload AndrewStation01` opens
Andrew Station, `cell` reports `loaded:true` with the cell pointer, and the camera then resolves where
it previously refused with `camera_transform_unverified`.

## A navmesh pass with no manual steps

A whole plugin's navmesh work is four answers, none of which needs the Cell View clicked by hand:

| Step | Answer | What it replaces |
|---|---|---|
| find the work | `ck.navmesh mode=audit filter=<name> limit=20` | double-clicking every cell in the Cell View to see which ones lack navmesh |
| do the work | `ck.navmesh mode=batch kind=recast confirm=true` then the same call with `after=<nextAfter>` | loading a cell, running Recast, finalizing, checking and saving it, one cell at a time |
| cover | `ck.navmesh mode=cover cover=generate confirm=true` (or `clear`, `clear_generated`; a pass that outlasted its budget is finished with `cover=status`, which reads the counts and saves the cell) | finding the cover group's captions, running the whole-load-order one by accident, and trusting that they did something |
| prove it | `ck.navmesh mode=state`, `mode=check`, or `mode=audit` again | reading the render window |

The batch reuses `mode=apply`, so the save gate is the same one: a cell is written only when its own check came back clean *and* the plugin's verdict agrees with the parsed one, and a failure is reported for that cell without abandoning the rest of the sweep. `mode=batch` deliberately walks one cell per call by default (`limit` raises it to 20): an apply can take minutes, and a call that holds the editor through a dozen of them is a call that times out and leaves the run behind. `nextAfter` names the last cell it touched, so the sweep resumes exactly where it stopped without any state in the server.

What is left for a human is only what the editor has no command for: painting or moving individual triangles in the render window. Everything else in the `NAVMESH` menu is reachable — `mode=run` posts any of its 49 captions by name, and the modes above are the ones a pass needs in order.

**Measured end to end on a live editor (2026-11-15, 1.11.246.0).** `mode=load cell=DmndRadio01`, then `mode=generate confirm=true`, took the cell from 2 forms / 67 vertices / 49 triangles to 3 forms / 16 / 14 in 15 s, the generation dialog answered and closed by the tool; `mode=finalize confirm=true` ran next, and the editor's own File/Save (`ck.save plugin=CKAgentNavE2E.esp`, that plugin active) wrote it in place: 45,653 → 48,728 bytes with the previous file kept as a backup. `mode=state` reported the new form `01001740` at 21 vertices / 14 triangles with `verticesHash 8d508bb6b6ccec53` and `trianglesHash 9e98cb180872c4aa`. Decoding the written `CKAgentNavE2E.esp` (sha256 `1d623d31f29d567ca9d8610146fcf27d0db5da6aa10d5babfe4180ba30658cad`) returns that same record with the same two digests, while the four pre-existing `NAVM` records of cell `00240045` keep the digests they had in the fixture. The editor was then killed and restarted through `ckpe_loader.exe`, the plugin set active again and the cell loaded: `mode=state` returned form `01001740`, 21 vertices / 14 triangles and those two digests again. Generate, save, restart, read the same mesh back — no step was typed by hand.

`mode=apply` then ran the same loop on its own. The CK's `Check NavMeshes` writes nothing for a cell with no findings, so the report window held only this tool's own `CALL`/`RPLY` lines and the parse said `unknown`. Apply now resolves that silence as clean only when the plugin's own classifier says `clean` **and** `nav.state` reports the cell's per-record geometry digests to corroborate it; it then saved through its own `nav.save` and the read-back answered `persisted` — 3 forms, 21 vertices, 14 triangles, form `01001EDC` with `verticesHash 8d508bb6b6ccec53` and `trianglesHash 9e98cb180872c4aa`. The written plugin (48,747 bytes, sha256 `b06fd86e…`) carries that record with those digests, and after another restart through `ckpe_loader.exe` the same cell returned the same form and the same digests. Silence is not a pass by itself: a report carrying a finding is refused (`dirty`), a plugin verdict that disagrees with the parsed report is refused (`mismatch`), a silent report whose cell reports no digests is refused (`unknown`), and a read-back that differs is refused with the record's own code — each of those is a test in `SavePersistTests`, so the acceptance cannot silently become "always allow". `mode=check` resolves the same silence with the same gate, so the answer a user reads before saving and the answer that decides the save cannot disagree; a dirty report still reads `dirty`, a plugin verdict that disagrees with the parse still reads `mismatch`, and silence with no digests still reads `unknown`, in both modes. The apply run also drops the form the generation emptied, identified as an empty form in the plugin that holds the replacement, so N applies leave the cell's own records plus the one mesh rather than N-1 extra placeholders; a form that still carries geometry is refused by `nav.prune` rather than removed. What remains open is environmental: `Recast-Based Generation` is greyed for some cells (`DiamondCityExt01`) and was greyed mid-session until the editor restarted, and `File/Data` sometimes opens hidden, so `ui.modals reveal` has to run before `ui.dataload` can tick a plugin.

**Adopting a build, one cell per call, and the prune's form table.** `tools/install-ckagent.ps1 -Game <path> [-PluginDll <file>] [-RestartEditor]` places the built `CKAgent.dll` in `CKPEPlugins`, proves the placed bytes by hash (it answers `plugin`, `sha256`, `bytes` and whether it replaced a previous copy), and with `-RestartEditor` hands the restart to `tools/editor-restart.ps1`. The DLL a running editor holds open is a named failure telling the operator to close `CreationKit.exe`, so a stale install cannot pass as current; `tools/install-ckagent.test.ps1` drives a first install, a replacement, the locked file and a missing `-PluginDll` in CI.

`tools/editor-restart.ps1` is the restart order itself: prove `ckpe_loader.exe` exists, stop the editor, start the loader, then poll for the editor frame and fail with the poll count if it never appears. A restart that stopped the editor before proving the loader would leave the operator with neither, and a wait that read the process once would call a slow editor a failed restart, so both are properties of the script and both are driven in CI: `tools/editor-restart.test.ps1` starts real stand-in processes in place of `CreationKit.exe` and the loader and requires a real stop of the old one, a real start of a loader that brings up the new one after a delay (so the first frame poll misses and a later one matches), then the timeout case (a loader that starts nothing), the wrong-process case (a loader that starts another program, so the frame name never matches) and the missing-loader case, where the running stand-in must still be alive afterwards because nothing may be stopped before the loader is confirmed. The frame itself is the editor's window, which a stand-in cannot own headlessly, so the test supplies `-ReadyCheck`; the loader proof, the stop, the start, the poll count and the deadline all run for real. Every step is written to `-Log` and as a JSON `steps` list, so a wrong order is visible in the job output rather than inferred. `mode=apply cell=<EditorID>` loads that cell itself (also `worldspace`, `row`, `form_id`), so one cell is one call rather than a load followed by an apply. The run then prunes, checks and saves, and writes its own record under `<state>/navmesh/<formId>-<utc>.json` - the check block, the save code, both sides' per-form digests, the pruned ids and the message - answering that path in `evidence`. Every path that writes a cell leaves that record: `mode=apply` with its check block, `mode=cover` through `cover=status` and a finished pass, and each cell `mode=batch kind=mesh` saves (whose record has no check block, because no check gate ran); `mode=batch` reaches it by running the same `mode=apply` pipeline per cell. A sweep that delivers a plugin therefore leaves one record per cell, not only a headline count. The prune's form table is proved against saved bytes rather than an offset: `CKAgent.Mcp.Tests/Fixtures/CKAgentNavE2E.esp` is the plugin a live apply wrote (sha256 `b06fd86e…`) and `nav-state-DmndRadio01.json` is the `nav.state` reply the editor gave for that same cell, and `TheStateTableAndTheSavedPluginNameTheSameForms` requires both to name the same three forms with the same counts and digests, so a wrong count, stride or form-id order fails CI. Inside the plugin the list is a `BSTArray`, and `FindNavmeshList` proves the head against the cell's own accessors before writing: only a head whose buffer and size equal `GetNavMeshes()` and `GetNavMeshesCount()` is used, the measured `0x78` is merely examined first, and no match or two matches answer `navmesh_array_not_found`; `static_assert`s pin the pointer stride and the array header, so a CKPE layout change fails the plugin build instead of shortening another structure. The prune itself goes through the array's own `Remove`, so the size field is never written by this code. When a run should have dropped a form and the plugin answered that it could not (a `CKAgent.dll` older than `nav.prune` answers `unknown_tool`), the apply's message says so and names `tools/install-ckagent.ps1`, so a stale install is a fixable fault instead of an extra form nobody notices.

### Generation is asynchronous, and the tool waits for it

Measured on a running editor, with the cell counts read back after every step:

- `Generation/Advanced` (40561) is **always** enabled and opens a dialog carrying the pipeline as buttons:
  `OK`, `Cancel`, `Generate Objects`, `Land and Objects`, `Finalize`, `Find Cover`. `Generation/Recast-Based
  Generation` and `Generation/Havok-Based Generation` report disabled until the cell has a navmesh edit
  session, and posting them then opens the `Recast Navmesh Generation` dialog (`OK`, `Cancel`, `Recast
  Options`, `Default Settings`, `Include Landscape`, `Generate Float Navmesh`).
- A build **clears the form's vertex and triangle arrays and writes the new geometry minutes later**. A
  single reading taken after the dialog closes shows `0`, which reads like a failure and is not one. The
  first clean measurement of a finished build was a BeaconHillPub navmesh of 465 vertices and 470
  triangles, arriving well after the press.
- `NAVMESH/Remove Cell NavMeshes` (40562) posts, is accepted, and changes nothing from outside the edit
  session, and `Land and Objects` is not a strip either: it starts a build, and the `0` it produces is that
  build's own first phase. So there is no supported way to empty a cell's navmesh from a caption alone.

The tool therefore treats generation as a wait, not a post: `mode=generate` sends the generator's caption
with `nav.run`, waits for the dialog the editor opens *after* the reply, presses its `OK`, and then polls the
cell until the count stops moving (`wait_seconds`, default 240, limit 900). Two live calls: BeaconHillPub
384 → 470 triangles, BeaconHillApartments 199 → 219 triangles and 248 → 221 vertices, both reported with the
settled count and the time it took. A call that sees `0` for the whole window answers
`generation_incomplete` instead of claiming success.

Still out of reach: anything that needs a triangle *selection* - `Remove Cell NavMeshes`, `Move Selection To
Separate NavMesh`, the precut and cover groups on a chosen region. `NAVMESH/Select Triangle By Index` (40594)
plus the `Navmesh` panel's own trait buttons (`NoLargeCreature`, `Preferred`, `Water`, `Door`, `Float`,
`Normal`) are the addressable handles that would close that, and both need a `checked` state on
`nav.commands` and a rebuild of the DLL.

## Navmesh, measured (editor 1.11.246.0)

Navmesh is a real top-level menu, not a hidden API. `ui.menu` on the frame returns a `NAVMESH` menu with
49 entries; the two this tool family runs are `NAVMESH/Check NavMeshes` (id 40568) and
`NAVMESH/Finalize Cell NavMeshes` (id 40608), and both were enabled with `AndrewStation01` loaded. Those
numbers are exactly why the tools resolve by caption: an id belongs to one build, so a hard-coded one
posts a command that means something else after a CK update.

There is **no Warnings window on this build**. `ui.command Warnings` answers `no_command` (no menu item
carries the word), `ui.windows` lists no such window, and posting `Check NavMeshes` opened nothing and
left the editor enabled (`blockedBy` empty). What a pass writes goes to CKPE's log: the editor's Log
Window is an `RTEDITLOG` with a `RICHEDIT50W` child, and CKPE mirrors the same stream to
`Logs/CKPE/CreationKitPlatformExtended.log`. So `ck.navmesh mode=check|finalize` posts the command and
then returns the log lines that appeared after it — the only verdict the editor actually leaves behind.
The plugin's own `CALL`/`RPLY` records are filtered out of that list, because the pipe echo is not a
verdict. For a clean cell the list is empty, and the reply says so instead of inventing a pass.

Presence comes from the form model, not from the log. `TESObjectCELL::IsNavMesh()` says whether the cell
carries a navmesh array at all, `GetNavMeshesCount()`/`GetNavMeshes()` list it, and each `NavMesh` form
answers `QNavmeshID()` and `GetSaveParentCell()` (CKPE `EditorAPI/T/TESObjectCELL.h`, `N/NavMesh.h`,
`T/TESChildCell.h`). Those are the confirmed members; `nav.state` reports exactly them, so a cell whose
forms point at another cell is visible as `parentMismatch` instead of looking covered.

Coverage reports counts measured from the form body, because the headers expose no accessor for them.
Two `BSTArray` heads sit at the measured offsets `0x40` and `0x58`; the first carries 12-byte float
points, the second carries triangles, and each head's element count is the vertex or triangle count.
Both were confirmed against the master file: `NAVM 0009C702` in `Fallout4.esm` declares 1809 vertices
and 2012 triangles and the live form reports the same numbers, with its vertex bytes identical to that
record's vertex block, while `NAVM 002496FF` matches at 4 vertices and 2 triangles. `coverage.geometry`
is `form_arrays` and the counts are summed over the listed forms.

The triangle element is 24 bytes in memory and 21 in the NAVM record, and its three vertex indices sit
at `+0x00`. The stride was swept against every triangle of four navmeshes (`0009C702` 2012, `002496FF`
2, `000C6F16` 26, `00002307` 23): 24 holds for all 2063 of them at offset 0 while the neighbouring
strides reach only half, and parsing the record of `0009C702` at 21 holds for all 2012 triangles with
no vertex index out of range and no link pointing past the triangle count. The layout, confirmed with
`llvm-objdump` on the creation path (`0x140EFFA59` `or dword ptr [rax+0x10], 0x800` and the `index*3`
scaling into the array at `navmesh+0x58`):

| offset | size | field |
|---|---|---|
| `+0x00` | 3 x u16 | vertex indices into the form's `0x40` array |
| `+0x06` | 3 x u16 | edge links: adjacent triangle index, `FFFF` = no neighbour |
| `+0x0C` | f32 | cover height; `7F7FFFFF` (FLT_MAX) where unset |
| `+0x10` | u32 | flags word |
| `+0x14` | 4 bytes | zero in every triangle measured |

The word is two halves: the low `u16` is the triangle's `Flags` and the high `u16` is its `CoverFlags`,
which is how the on-disk `NavmeshTriangle` stores them — `Vertices` (3 x i16), `EdgeLink_0_1`, `EdgeLink_1_2`,
`EdgeLink_2_0`, `Height` (f32), `Unknown` (u8), `Flags` (u16), `CoverFlags` (u16), 21 bytes per triangle in
`NVNM` (Mutagen's `NavmeshTriangle_Generated.cs`, mirroring xEdit's `wbNVNM`). So a "flag bit 18"
is `CoverFlags` bit 2, and the link names give the edges it indexes: bit 16 = edge (`v0`,`v1`), bit 17 =
(`v1`,`v2`), bit 18 = (`v2`,`v0`) - reported as `edge01`, `edge12` and `edge20`. Parsed against a CK-saved `NVNM` (version 15, cell `00074D28`, 1809
vertices, 2012 triangles): bits 0/1/2 of `CoverFlags` are set on 538/694/21 triangles, and in every one of
those the edge with the same index is a boundary edge — a set bit on a linked edge never occurs.

Bit 18 is rare because the edge it names is rarely open, not because it means something else. The same
payload gives boundary rates of 35.7% for (`v0`,`v1`), 43.2% for (`v1`,`v2`) and 5.6% for (`v2`,`v0`): the
CK's triangulation shares (`v2`,`v0`) with the neighbouring triangle in about 94% of triangles, making it
the pair's internal edge, while the other two edges are the ones exposed to walls. Cover only exists on an
open edge — a wall has no neighbouring triangle to carry the other side — so a 5.6% boundary rate caps
how often bit 18 can appear. The masks seen there agree: 0, 1, 2, 3 (two edges covered, 148 triangles),
4 (10) and 6 (11, both edges at `v2` open); 7 never, which is what an internal (`v2`,`v0`) edge implies,
and 5 never in that cell.

The editor settles the part offsets alone cannot. Posting the CK's own `NAVMESH/Clear Cover` on the loaded
cell `DmndValentines01` (one form, 71 triangles) took `edgeCover` from 22/26/0 (`any` 38, `all` 0, `openEdges`
48) to 0/0/0 with the same 71 vertices and 71 triangles, so the high half of the flags word is exactly the
cover the editor edits, and bits 0/1/2 name its edges. The display-only siblings - `Find Cover Edges`, `Draw
Cover`, `Cover Test`, `Audit Navmesh Report` - left every `nav.state` count identical and report nothing the pipe can
read back, which is why the measurement is a clear: it needs a cell whose cover may be dropped, and a reload
(`File > Data`, same load order) restores it from disk because nothing here is saved.

Inside the flags word, bit 11 (`0x0800`) is the base flag the generator sets on each triangle it emits,
and the creation path skips triangles whose `(flags >> 11) & 1` is already set. Bits 0-2 (`0x1`/`0x2`/
`0x4`) and bits 16-18 (`0x10000`/`0x20000`/`0x40000`) are per-edge masks, walked with a rotating single
bit; the high three are set in master data only on edges whose link is `FFFF` - zero of 1094 triangles
break that - so they are the per-edge cover flags, which is also why `0x7F7F` shows up next to them: it
is the high half of the float `0x7F7FFFFF`, not a cover value. Bits 6 (`0x40`, 54 triangles of 2012) and
10 (`0x0400`, 2) are editor flags whose meaning is not recovered.

The `+0x0C` float is not the cover height, and the mesh says so three ways. It is present on 2010 of the
2012 triangles, including the 917 that carry no cover bit at all, and the editor's `Clear Cover` leaves it
set on all 71 triangles of `DmndValentines01` - a triangle without cover has no cover edge for it to
describe. Measured against the vertices of the edges those bits mark, across the whole of cell `00074D28`
(1809 vertices, 2012 triangles, 1252 covered edges): it never equals a covered edge's height (0 of 1252
matches at a 0.001-unit tolerance), it falls inside the triangle's own z span on 3 of 2010 triangles, and
it sits a median 322 units above the top of the edge it would otherwise describe. It is not a coordinate on
any axis (r = 0.14 with mean x, -0.12 with y, -0.31 with z), not a shape measure (area 0.04, perimeter 0.04,
longest edge 0.04, triangle altitude 0.01), not the covered edge's length and not the distance from the
edge to the nearest other vertex (0 exact matches in 150 sampled edges). What it is instead is a smooth
local field: 42.9% of the 2155 edge-sharing triangle pairs carry the same value, the median difference is
2.43 units and 65% differ by under 10, while the busiest value (exactly 170.0 on 572 triangles) appears at
world heights from -238 to 1043, so it is not an elevation either. A scene-derived clearance - the ceiling
height above the triangle - fits every one of those measurements and is not proven; what is proven is that
it is not cover data and must not be reported as a cover height.

`coverage.area` sums each triangle's half cross-product magnitude and `areaTriangles` says how many
triangles contributed; both are null when any form cannot be read in full, because a partial read would
understate the total. Measured live: `000C6F16` 72626.77 units over 26 triangles and `00002307`
471288.00 over 23, matching an independent memory reader exactly. The two CKPE-declared `BSTArray<uint16>`
at `0x260`/`0x278` are used by the edit path as u16 sets (search, then insert - `0x140EF1421` and
`0x140EF13FC`) and measured empty on every loaded master form, so they are neither the triangle storage
nor a cover count of a loaded mesh. No percentage is computed from any of these numbers.

Generation is dialog-gated, and the pipeline answers the dialog. Posting `NAVMESH/Generation/Recast-Based
Generation` opens a modal titled `Recast Navmesh Generation` (buttons `OK`, `Cancel`, `Recast Options`,
`Default Settings`, `Include Landscape`, `Generate Float Navmesh`) and greys the editor frame until it is
answered; nothing is built while it sits there. So `nav.generate` and the first step of `nav.apply` press
that dialog's own `OK`, then wait - bounded at 120 s - for the window to disappear, and a dialog that
never closes stops the sequence with `generation_incomplete` instead of letting finalize race a modal.
Because the pipe handler runs on the editor's own UI thread, `nav.apply` cannot wait for anything: a `Sleep`
there froze the editor, so the generation dialog never opened and the call never returned. The sequence is
therefore a stage machine that keeps its progress between calls and answers `pending` until the last one.
After the sequence, `nav.apply` re-reads the cell's own arrays and refuses to answer `ok` when neither
the vertex nor the triangle count moved: that is `no_geometry_change`, and its message names the counts,
the forms and what the dialog did, because a silent no-op used to read as a successful run.

The pipeline saves on evidence, not on hope. `ck.navmesh mode=apply` posts the three steps, reads the
CKPE log lines that appeared after them, and computes a verdict: any line naming an error, warning,
failure or invalid result, or a modal dialog left blocking the editor, makes it `not_clean`, and a
`not_clean` verdict stops before the save. Only a `clean` verdict posts `File > Save`, and the reply
carries `verdict.verdict`, the line count it read and the dialog it found, so the reason a cell was or
was not written is visible rather than assumed. `dry_run=true` is the inspection half: it resolves the
same four commands through `nav.plan`, reports their paths, ids and enabled state, and posts nothing at
all, so neither a generation nor a save can happen.

`mode=check` and `mode=apply` answer with `verdict` and the `check` evidence, not with the log text it was read from. The parser
matches the editor's own message templates, which is why it names `Navmesh %08x Cell %s, Triangle %u is
degenerate`, `(Warning %d Unconnected NavMeshes)`, `Potentially overlapping navmesh detected while
saving Navmesh (%08x) in cell %s` and the rest of that family rather than looking for the word "error" in
prose. Each finding carries a severity, the navmesh form id when the template names one, a collapsed
count for repeats, and the line it came from. Cells are grouped by the cell name the template carries, so
the reply reads `{"verdict":"warnings","cells":[{"cell":"AndrewStation01","errors":0,"warnings":2,...}]}`.
A check that writes nothing this parser recognises is `unknown` to the parser, because silence is not a
finding. What that silence means is the gate's job, and it is the same gate for reading and for saving: a
report holding only this tool's own pipe echo is `clean` when the plugin's own verdict is `clean` and
`nav.state` reports the cell's geometry digests, and stays `unknown` when either is missing - so nothing
saves on such a build, which is the intended failure side. The tool's own code is the gate's answer
(`clean`, `dirty`, `warnings`, `mismatch`, `unknown`, `blocked`) and it travels in the `check` block with
`silent`, `corroborated` and the reason, while `verdict` keeps the parser's vocabulary (`clean`,
`warnings`, `errors`, `unknown`).

Creation and application are the editor's own generation commands, not a navmesh writer. The measured
`NAVMESH` menu carries `Generation/Havok-Based Generation`, `Generation/Recast-Based Generation`,
`Generation/Advanced`, `Finalize Cell NavMeshes`, `Check NavMeshes`, the precut group, the cover and
waypoint groups, `Move Selection To Separate NavMesh`, `Remove Cell NavMeshes`, the worldspace finalize
variants and `Audit Navmesh Report`. `nav.apply` posts the three that make a cell usable — generation,
finalize, check — in that order and reports each posted id; saving stays a separate `ui.command Save`,
because a save reaches every file the CK has open. `nav.run` posts any other navmesh caption, which is
the whole command-level edit surface: precuts, cover clearing, waypoints, removal, separation, audit,
OBJ export. Inside the cover group only `Clear Cover` and `Clear Generated Cover` change the record;
`Find Cover Edges`, `Draw Cover` and `Cover Test` are display-only, which identical `nav.state` counts
before and after each of them show.

The limit is the same one the coverage read runs into: the CK's navmesh edit module is interactive, and
no header exposes its geometry, so painting or moving individual triangles cannot be automated here.
Edits stop at the granularity the CK itself gives a command, and the tool description says so.

`NAVMESH/Audit Navmesh Report` is not a verdict channel today, and the tools do not treat it as one. The
executable names its output: `NavMeshAudit%02u_%02u_(%02u.%02u.%02u).csv`, so the design is a per-cell
CSV. Posted against a loaded cell it produced nothing at all - no CSV in the game folder, `Data`, `Logs`,
the user profile or Temp, no new window, no modal, and no navmesh or audit line in the CKPE log. That was
true for an interior cell and for an exterior cell (grid 5, -11), waiting 15-20 s each time, with the menu
item enabled throughout. Until a session is observed where it does write, `mode=run command="Audit
Navmesh Report"` means only that the command was posted.

The workflow is deliberate about what it can prove:

- `nav.commands` walks the whole menu bar and returns every entry whose path mentions navmesh, with the
  id and whether the editor has it enabled. A repeated caption is only distinguishable there, and an
  empty list answers "does this build carry the command at all".
The check's own report is the verdict's only input. The plugin finds it from its own module path
(`<game>\CKPEPlugins\CKAgent.dll` → `<game>\Logs\CKPE\CreationKitPlatformExtended.log`), remembers the size
before posting, and reads only what the check appended. A partly written last line is dropped, because a
fragment of a message would otherwise read as a finding.

- `nav.check` posts the resolved id. A greyed item is refused as `command_disabled` before anything is
  posted, so the editor's reason is reported instead of a quiet no-op.
- `nav.finalize` changes the loaded cell's navmesh data, so it is refused without `confirm` — in the
  plugin as well as in the server, because the server is not the only possible caller.

`tools/navmesh-probe.ps1` asserts the parts that would otherwise fail silently against a live editor:
that the posted id is the one discovery just reported (not a constant), that the reply names a real
CKPE log and carries a `report`, that an unconfirmed generate, apply or finalize returns no payload, and
that a generation strategy or caption this build does not carry is refused by name. It never runs
generation, finalize or `run`, because those change the loaded cell.

`tools/navmesh-command.test.ps1 -Live` is the real-data pass. It loads `-Cell` (default `DmndRadio01`),
reads `nav.state`, and fails if any form's `geometry` is none of `read` / `empty` / `unreadable`, if a form
marked `read` or `empty` reports `null` vertex, triangle or area counts, if an `empty` form reports anything
but zeros, if the coverage counters disagree with the forms, or if the area total is null while every form
is readable. With `-Generate` it first posts `nav.generate recast confirm`, answers the dialog through
`nav.accept` and waits for `navmeshForms` to grow, so the forms it reads are the ones a Recast pass just
replaced — the exact case that used to answer `null`. Without `-Live` the script is the static pass and
touches no editor. The live modes themselves live in `tools/navmesh-command.live.ps1`, which the suite
dot-sources, so each file stays inside the repository's line budget.

A mutating run cleans up after itself: on a cell that carried no navmesh, the forms the run created are
removed with the editor's own `Remove Cell NavMeshes` command (resolved from the live menu, not hardcoded)
and `nav.state` is polled until `navmeshForms` is zero, so `-Generate` leaves no generated mesh behind and
the next run starts from the same state. The evidence says which happened — `restore` is `removed`,
`already_empty`, `not_needed` (a read-only run) or `not_possible` (the cell carried navmesh before the run,
and removing it would delete the original rather than restore it); a removal that posts but never empties
the cell fails the run. A read-only run still requires a cell with navmesh; the mutating modes accept an
empty cell, because generating into one is what they do.

`-Check` goes one step further and asserts the verdict itself. It drives `ck.navmesh mode=check` through the
real MCP server with `tools/mcp-call.mjs` (needs `node`, `-McpDll` or a Release build under
`CKAgent.Mcp\bin`, and `-GamePath` or `CKAGENT_GAME`), then fails when the verdict is `unknown`, when it
reports warnings or errors but names no finding, when its per-severity totals disagree with the findings it
named, when a finding carries no message or no source line, or when no finding matches an `-Expect` pattern
(a regex over each finding's message or its source line). Use it on a cell whose findings are known:

```
tools/navmesh-command.test.ps1 -Live -Check -Cell DmndRadio01 -Expect 'degenerate','missing a link'
```

`-Apply` drives a whole staged apply over the pipe, one stage per call, against the loaded cell — so the cell
must be in the writable active file and its plugin must be loaded, or the run fails naming that. It records
every stage and its latency and fails when a stage answer exceeds `-StageBudgetMs` (default 2000 ms), which
is the regression the staging exists for: a stage that waited on the editor thread instead of answering
`pending` would hold it for its whole deadline. While a stage is pending it also probes `nav.state` and
requires at least one pending stage to answer within `-ProbeBudgetMs` (default 2000 ms); a slower probe is
not a failure on its own, because the editor may legitimately be computing during a generation stage, but a
run where no stage answered a probe at all fails. The run must also observe at least one `pending` answer,
so a pipeline that finished in a single call cannot pass as responsive. The probe count and slowest times
land in the evidence as `applySteps` (each stage with its latency), `applyProbeMs`, `applySlowestStageMs`,
`applyFastProbes`, `applyVerdict` and `applyPluginVerdict`; a finished run that carries no verdict of its own
fails, because that is what would push the answer back onto the server's parse.

`-Check` likewise compares the verdict the plugin reported with the one the server parsed from the same
lines (`pluginVerdict` in the evidence) and fails when they disagree, so the two classifiers cannot drift
apart on real data.

What is not claimed: the reader reports vertices, triangles, measured area and per-edge cover from the
forms' own arrays, but it never writes geometry and never bakes, and a cell the log says nothing about is
not thereby proved correct.

## Camera transform, measured (editor 1.11.246.0, cell open)

`tools/camera-pose-probe.ps1` reads the `NiCamera` behind
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

### Framing a reference, measured

`ck.frame` finds the object by iterating the loaded cell's own `TESObjectCELL::Items` (`GetItemCount`,
`GetItems`, `TESObjectREFR::GetPosition`, `GetFormEditorID`, base object via `GetParent`), so nothing is
assumed about where a reference sits. It then stands the camera at `target - distance * (cos yaw, sin yaw)`
at the object's own height and writes the verified `SetPosition` + yaw matrix pair.

With `AndrewStation01` loaded (3616 references), on `Loot_Prewar_Safe` (`0023F200` at
`39.5606, 9492.1836, 272.3472`):

| Request | Camera | Span | `aim` |
|---|---|---|---|
| `frame Loot_Prewar_Safe` (256, yaw 0) | `-216.4394, 9492.1836, 272.3472` | 256.00 | 1.0000 |
| `frame Loot_Prewar_Safe 512 1.5708` | `39.5606, 8980.1836, 272.3472` | 512.00 | 1.0000 |

The `aim` the tool reports is not graded on the matrix it just wrote. It is read from the **scene camera**
— the `NiCamera` at `camera+0x10`, which the editor copies the node into every frame and which is what
actually renders — so the number means the editor took the pose. After the yaw-90° frame that row is
`(0, 1, 0)` while the unit direction to the object is `(0, 1, 0)`, dot `1.000000`; the memory probe reads
the same row from outside the process and agrees.

An unmatched name comes back with candidates from the loaded cell (`no reference in the loaded cell
matches X among 3616 references; for example: Loot_Prewar_Safe, MS07cCompanionMarker, ...`), because a
name the caller cannot discover is not a usable tool.

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

The tool and the probe read different nodes, which is why their matrices are not equal. `camera get`
reports `rotate` from the camera's own node — the matrix `MoveLocal` moves along, confirmed by
`camera move 0 256 0` stepping to world `0 -256 0` at that orientation. The probe reads the slave
`NiCamera` at `camera+0x10`. Measured on 1.11.246.0, the slave's rows are the node's rows shifted by one
(node `r1, r2, r0`), and the tool's `rotationOffset` is the sum of the differences that shift implies —
6.0 at the axis-aligned poses first measured, 6.4472 at a diagonal one, so the shift is the invariant and
the number is not. `tools/camera-tool-cross-check.ps1` asserts that
relation, the rotation offset the tool reports and the translation both nodes share, so a decode error in
the plugin fails a
check instead of passing as a plausible pose. It needs the editor and the plugin running, so it stays a
local check rather than a CI one; `-Pose` replays a saved probe read instead of taking a new one.

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
