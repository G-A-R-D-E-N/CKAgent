# CKPE anchors for the render control

Source: `Perchik71/Creation-Kit-Platform-Extended` (master, read 2026-11-13). CKAgent links CKPE
dynamically, so these are API references rather than values re-derived from the editor binary.

| Fact | Where it comes from |
|---|---|
| `TESRenderControl` declares its own class-table ID and RTTI ID | `CKPE.Fallout4/Include/EditorAPI/T/TESRenderControl.h` |
| `sizeof(TESRenderControl)` @0x0B0; `DialogWindow` @0x008, `DisplayControlID` @0x010, `SceneRoot` @0x020, `CameraRoot` @0x050, `Camera` @0x058, `RenderWindow` @0x060 | the same header, via its `static_assert`s |
| `GetDisplayWindow()` is `GetDlgItem(DialogWindow, DisplayControlID)` | the same header |
| `TESPreviewControl : TESRenderControl`, size @0x108, with its own ID | `EditorAPI/T/TESPreviewControl.h` |
| `Common::ID::Address()` is the module base plus `AddressLibrary::ResolveOffset(Id())` | `CKPE.Common/Include/CKPE.Common.ID.h` |
| The COL pointer sits immediately before the class table and holds the type descriptor's position relative to the base | `CKPE.Common/Include/CKPE.Common.RTTICast.h` |

## What the 1.11.246.0 image holds

Read from `CreationKit.exe` (image base `0x140000000`; function bounds from `.pdata`, class names from
RTTI, global targets from the displacement of the storing instruction):

| Fact | Address in the image |
|---|---|
| `TESRenderControl` class table / COL | `0x2FF4C30` / `0x35A5078` |
| its constructor / destructor | `0x72EAD0` (0x785 bytes) / `0x72F260` |
| `TESPreviewControl` class table / COL | `0x2FF4340` / `0x35A4E18` |
| its constructor, reached through the thunk at `0x479B5` | `0x7274B0` |
| `BGSRenderWindow` class table / COL | `0x2FD40B8` / `0x359BDC8` |
| its constructor, and the store of the finished object | `0x56D250`; `mov [0x3D491E8], rax` at `0x69A8CF` |
| `BGSRenderWindowCamera` class table / COL | `0x2FD4828` / `0x359C208` |
| its constructor, and the store into the window | `0x57BBC0`; `mov [rdi+0xC0], rax` at `0x573BB3` |

- `TESRenderControl`'s constructor has exactly one caller: `TESPreviewControl`'s. Its RTTI base list is
  `TESPreviewControl` then `TESRenderControl`, and every allocation site passes `0x108`. This build
  therefore never creates a plain `TESRenderControl`, and an exact-class-table scan cannot match the
  editor's view — which is why the earlier walk never resolved anything.
- `BGSRenderWindow` has six bases and none of them is `TESRenderControl`. It is `0x180` bytes, built
  once, and published to the image slot at `0x3D491E8` — 0x98 bytes after the `TES_Singleton` address
  CKPE records (`0x143D49150`), with two more copies in the slots just before it.
- The window keeps its camera at `+0xC0`: a `BGSRenderWindowCamera`, `0x88` bytes. That class has
  **no** base class, and its constructor initialises inline poses at `+0x20` and `+0x3C` (0x1C bytes
  each) plus floats at `+0x60`..`+0x70`. It is not an `NiAVObject`, so the `NiTransform` offsets
  (`_World` at `+0x70`, translate at `+0xA0`) that CKPE's `TESRenderControl` comment describes must
  not be applied to it.

## The live view camera (read from a running editor)

`tools/ck-surface-probe/camera-pose-probe.ps1` attaches to the running editor, validates every hop by
class table and dumps the objects. On 1.11.246.0 with no cell loaded it reported:

| Hop | Value |
|---|---|
| image slot `0x3D491E8` | window object, class table `0x2FD40B8` (`BGSRenderWindow`) |
| window `+0xC0` | camera object, class table `0x2FD4828` (`BGSRenderWindowCamera`) |
| camera object `+0x10` | scene camera, class table `0x3430CC0` (`NiCamera`) |
| camera object `+0x08` | an unclassified heap object |
| camera object `+0x58` | the window itself |
| camera object `+0x60` / `+0x64` | `1.0f` / `0x7FFFFFFF` |

`BGSRenderWindowCamera` holds no transform of its own: its float block read as zero with no cell
loaded. The view is the `NiCamera`, which is an `NiAVObject`, so CKPE's transform layout applies:
`_World` at `0x70` (rotation as a 3x3 there), `m_Translate` at `0xA0`, `m_fScale` at `0xAC`.

TODO-verify: the transform values themselves are unconfirmed. A run with `Fallout4.esm` loaded and no
cell still found no transform — the orthonormality search over the `NiCamera` returned only permutation
blocks with scale `0` — so the world transform is created when a cell is opened, and only a
cell-loaded run of the probe can confirm the offsets.

## What CKAgent does with this

The plugin reads the window pointer from the image slot at `0x143D491E8` and validates each class
table in turn: `BGSRenderWindow`, then `BGSRenderWindowCamera` at `+0xC0`, then `NiCamera` at `+0x10`.
Every read runs at load and again on a bounded timer, and `ck.resolve` repeats it on demand.

Every one of those addresses is an **image offset**, rebased onto the base the plugin reads at load.
The editor is relocated (a live run loaded it at `0x7FF723830000`), so using the `0x140000000` values
raw dereferences unmapped memory: that was a real access violation in `CKAgent.dll` at
`0x143D49150` before the rebase was added.

Before any transform field is read or written, the rotation at `NiCamera+0x70` must be orthonormal
with a positive scale; an empty editor fails that check and `ck.camera` reports
`camera_transform_unverified` instead of touching memory. Position writes go to `+0xA0` and rotation
writes to `+0x70`, both inside the validated object.
