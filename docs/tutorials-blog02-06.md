# Native C# tutorial ports: Blogs 2–6

All 18 independent entry points compile to Z80, assemble with pyz80 and package
as actual 80-track, two-sided EDSK disks. No CLR runs on the SAM. The reference
checkout is read-only; no assembly, WordPress exports or blog drafts were edited.

## Run and local assets

Set `pyz80`, `samdisk`, `samdos` and `simcoupe` in ignored
`tools.local.json`, following `tools.example.json`. Set `referenceRepo` when
regenerating or extracting missing sprite data. Open a tutorial's `Program.cs`
and press F5. Each folder has an editor/type-checking project. The prelaunch
wrapper extracts **missing** sprite data for that known folder before compiling.
Existing generated C# files are preserved during ordinary builds.
The 16 asset-dependent editor projects are registered in the solution but are
excluded from its host build configurations, so building/testing the compiler
does not build the tutorial editor projects. Build an individual project explicitly
after extraction if desired; native F5 still selects the folder's C# files.

```powershell
# Explicitly regenerate all local initializers from the reference checkout:
python .\tools\generate_tutorial_assets.py --all
python .\.vscode\sam_run.py .\tutorials\Blog06\08-3-CroppedSidewaysJump\Program.cs --run
```

`TutorialData.g.cs` contains normal, inspectable C# `static readonly byte[]`
initializers included with the repository owner's permission to redistribute
the sprite data. A fresh checkout does not need the reference repository to
obtain these initializers. No sprite payload is bundled in the SDK or runtime.
Frames are separated into named `#region` blocks preserving the original source
labels, with their zero-based animation index, dimensions and decoded byte count.
Opaque/separate planes use 16 bytes per row; interleaved frames use 32 bytes
(16 mask/pixel pairs), or 26 bytes for cropped 13-byte rows. Each row is numbered.
Compressed frames show separate decoded mask and pixel reference rows of the
correct width, followed by named packet streams; directory entries are labelled
by frame. Reference rows are comments, so readability adds no resident bytes and
does not change the compressed representation.
Keep local edits to these files separately before explicitly regenerating them;
`--all` replaces them. Earlier ignored `sam-tutorial-data.asm` files are obsolete
and are not build inputs.

The compiler emits each array's uint16 byte-length prefix and resident payload.
There is no allocation, managed array object or startup initializer on the SAM.
The program explicitly passes the data into the drawing routines:

```csharp
SpriteFrame frame = Sam.SelectSpriteFrame(TutorialData.SpriteFrames, sprite);
Sam.DrawRleMaskedSprite(frame, 641, 13, 22);
```

`SpriteFrame` carries a data pointer and index, not a hidden asset ID. Authored
byte-array parameters and frame values also work across source-method calls.
Direct array indexing/mutation and runtime allocation remain explicit errors.

## Exact progression

Paths below are relative to `assembly` in the reference and `tutorials` here.
All ordinary sprites are 16 bytes × 32 rows (32×32 pixels), with a 128-byte
screen row stride. Blank backgrounds stay black, except the intentional striped
lessons and user-selected Blog 6 background colours.

| Reference | C# folder | Lesson preserved |
|---|---|---|
| `blog2\1.asm` | `Blog02\01-PrintingText` | Codes 32–95, original literal, ROM key wait. |
| `blog2\2.asm` | `Blog02\02-ScreenWrites` | Eight original packed-byte writes, plus inspection key wait. |
| `blog2\3.asm` | `Blog02\03-OpaqueSprites` | Original opaque 512-byte sprite, explicit row copying. |
| `blog2\3-2.asm` | `Blog02\03-2-OpaqueSpriteCopy` | The second source's opaque payload and row-copy result. |
| `blog3\4.asm` | `Blog03\04-BuildMaskedSprite` | Separate pixel/mask arrays, build an interleaved resident buffer. |
| `blog3\4-2.asm` | `Blog03\04-2-StripedBackground` | Same building step over alternating `22`/`44` rows. |
| `blog3\5.asm` | `Blog03\05-InterleavedSprite` | Direct supplied interleaved mask/pixel data. |
| `blog3\5-2.asm` | `Blog03\05-2-StripedBackground` | Interleaved drawing over the original stripes. |
| `blog4\6-1.asm` | `Blog04\06-1-TwoFrames` | Two embedded source frames, alternating. |
| `blog4\6-2.asm` | `Blog04\06-2-ExternalFrames` | First two frames from the external system-palette include. |
| `blog4\6-3.asm` | `Blog04\06-3-AnimationTable` | All nine frames, in original table order. |
| `blog4\6-4.asm` | `Blog04\06-4-DoubleBuffer` | Clear/draw hidden page before flipping completed frames. |
| `blog5\7-1.asm` | `Blog05\07-1-KeyboardJump` | Any keyboard bit starts the complete eight-frame sequence. |
| `blog5\7-2.asm` | `Blog05\07-2-SpaceJump` | Space only; return to idle frame zero after completion. |
| `blog5\7-3.asm` | `Blog05\07-3-WalkAndJump` | Idle, Space roll/jump, Z/X walks; non-looping sequence cannot be interrupted. |
| `blog6\8-1.asm` | `Blog06\08-1-OriginalPalette` | Original-colour data and supplied 16-entry editor palette. |
| `blog6\8-2.asm` | `Blog06\08-2-ChangeBackground` | Keys 1–5 select entry-zero values `00,11,22,44,7F`, persistently. |
| `blog6\8-3.asm` | `Blog06\08-3-CroppedSidewaysJump` | All 38 cropped frames, 13 bytes × 22 rows at `5*128+1`; Space+Z/X selects full sideways jumps. |

Blog 5 retains its system-palette data; Blog 6 retains the original-colour and
cropped data. The cropped include skips image 25: zero-based offsets 24 and 31
select source images 26 and 33. Space alone plays the original eight-frame roll,
not a sideways jump. Sideways displacement is already drawn into those frames;
the port does not add an unrelated moving X coordinate.

Array data starts with a frame count. Uncompressed data then stores successive
interleaved frames. Blog 5/6 use lossless packet compression to fit the resident
budget without changing a pixel or palette index:

- Count byte, then four bytes per frame: little-endian mask-stream offset and
  pixel-stream offset, both relative to the array payload.
- Packet `1..127`: repeat the following value that many times.
- Packet `81..FF`: copy the following `packet & 7F` literal values.
- Zero terminates a plane; `80` is invalid.
- Each plane must decode to exactly `widthBytes * height` bytes. The resident
  1024-byte interleaved buffer is reused; malformed/truncated streams trap.

Animation clears only the sprite rectangle, matching the source lessons, not
24 KB on every frame. The startup and double-buffer setup clear both pages once.
Frame waits request five ROM interrupts; compiler/runtime execution and SAM
contention mean this is not a promise of handwritten-code timing equivalence.

## Black/cyan flashing: reproduced and fixed

The editor's palette entry zero is `5D` (RGB `24FFFF`, cyan), not black. Loading
that original palette must be followed by an intentional entry-zero override
for a blank screen. More importantly, writing only the hardware CLUT leaves
the ROM interrupt's two palette phases at `55D8` and `55EC` stale. ROM v3.0's
interrupt selects a phase and uses `OTDR` to write its 16 entries. This can
undo a black override or flash a user-selected background.

The isolated `palette_repro.py` fixture deliberately leaves black/cyan ROM
phases divergent and removes software restoration from a **staged copy** of
the runtime. Actual SimCoupe sampling reproduced both RGB `(0,0,0)` and
`(36,255,255)`. It is a mechanism reproduction, not a retained copy of the
earlier disappeared agent's executable.

The final runtime retains the selected palette, mirrors it into both ROM
phases before enabling interrupts, and reapplies it after each `HALT`.
Blog 6 overrides entry zero to black once on startup; only keys 1–5 change it
afterwards. BASIC's original 40-byte palette table is saved/restored on exit.
The default system palette also now matches ROM v3.0 rather than a fabricated
linear sequence, preserving the Blog 5 sprite's original system colours.

```powershell
python .\tests\SamRuntime\palette_repro.py
python .\tests\SamRuntime\tutorial_simcoupe.py --repro .\out\verification\palette-repro\program.dsk
python .\tests\SamRuntime\tutorial_simcoupe.py
```

## Assembled size comparison

Bytes below are actual assembled measurements. “Other” includes compiler
trampolines, guards, literals and static slots. “Runtime” is its loaded binary
portion; add **808 reserved bytes** for the saved BASIC palette/variables at
image end. These are not disk sizes. Handwritten code includes its own hardware
helpers, whereas C# calls the separately listed runtime: smaller application
columns alone do not establish a smaller executable.

| Source | Original code | Original data | Original binary | C# app | C# asset data | Other | Runtime | C# binary |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 2/1 | 28 | 20 | 48 | 48 | 0 | 81 | 756 | 885 |
| 2/2 | 53 | 0 | 53 | 68 | 0 | 53 | 669 | 790 |
| 2/3 | 159 | 512 | 671 | 54 | 514 | 61 | 844 | 1473 |
| 2/3-2 | 159 | 512 | 671 | 54 | 514 | 61 | 844 | 1473 |
| 3/4 | 109 | 2049 | 2158 | 108 | 1028 | 69 | 2063 | 3268 |
| 3/4-2 | 131 | 2049 | 2180 | 143 | 1028 | 73 | 2138 | 3382 |
| 3/5 | 72 | 1025 | 1097 | 138 | 1027 | 73 | 973 | 2211 |
| 3/5-2 | 94 | 1025 | 1119 | 173 | 1027 | 77 | 1048 | 2325 |
| 4/6-1 | 129 | 2051 | 2180 | 354 | 2051 | 91 | 1041 | 3537 |
| 4/6-2 | 132 | 9216 | 9348 | 354 | 2051 | 91 | 1041 | 3537 |
| 4/6-3 | 192 | 9216 | 9408 | 342 | 9219 | 89 | 1041 | 10691 |
| 4/6-4 | 255 | 9216 | 9471 | 348 | 9219 | 89 | 1118 | 10774 |
| 5/7-1 | 297 | 24576 | 24873 | 838 | 2555 | 134 | 2330 | 5857 |
| 5/7-2 | 296 | 24576 | 24872 | 838 | 2555 | 134 | 2330 | 5857 |
| 5/7-3 | 361 | 24576 | 24937 | 2868 | 7193 | 315 | 2330 | 12706 |
| 6/8-1 | 369 | 24618 | 24987 | 2927 | 8511 | 323 | 2404 | 14165 |
| 6/8-2 | 399 | 24618 | 25017 | 3978 | 8511 | 422 | 2404 | 15315 |
| 6/8-3 | 451 | 21778 | 22229 | 5513 | 12653 | 557 | 2404 | 21127 |

Original data includes everything after the first sprite label, including any
later palette loader. Several originals include unused frames; 6-2 and 7-1/7-2
retain only the frames their executable sequences need. Packet compression also
contributes to later binary reductions. The largest C# resident reservation is
21,935 bytes, leaving 2,641 bytes below the `E000` stack floor. C# app bodies for
the first two lessons remain within the original 48/68-byte regression budgets.

```powershell
python .\tools\verify_tutorials.py --build --compare
python .\tools\measure_kernels.py
```

The masked-byte core still performs the original `screen & mask | pixel`
operation. Whole helper costs include pointer/rectangle/packet guards; packet
decoding trades execution time for resident memory. The measurements in ignored
`out\verification\kernel-costs.json` execute real assembled kernels on a Z80 CPU,
not on host C#; they exclude ROM waits and SAM contention. The historical
text/write-kernel costs remain in `tutorial-progression.md`.

| Frame-zero drawing kernel | Original T-states | C# runtime T-states | C# rectangle clear |
|---|---:|---:|---:|
| Blog 3/5, 16×32 interleaved | 36,236 | 40,828 | N/A |
| Blog 4/6-3, 16×32 interleaved | 36,236 | 40,828 | 17,738 |
| Blog 5/7-3, 16×32 compressed | 36,236 | 296,634 | 17,738 |
| Blog 6/8-3, 13×22 compressed | 20,494 | 203,109 | 10,454 |

Thus compression is measurably slower, not an execution-speed optimization.
The extra packet and decoded-length guards are observable safety checks.

## Validation

- Full solution build, formatting check and **52 passing compiler/tooling tests**
  (43 compiler, 9 tooling), including native Z80 execution
  of data arguments, typed frames, string-index compatibility and numeric widths.
- `python .\tests\test_tutorial_asset_layout.py` checks named frame boundaries,
  row widths, invalid lengths and byte-for-byte packet preservation using
  synthetic data. All 16 local sprite files regenerate with identical payloads.
- All 18 entry points assembled and packaged; EDSK header/track lengths and
  complete resident reservations checked against `E000`.
- `tutorial_test.py`: actual compiled application instructions and actual
  drawing helpers with banked RAM, compared byte-for-byte with original assembly
  assets. Checks frame order, idle/roll/walk/sideways completion, early key
  release, both page buffers, and persistent palette selection. ROM entry and
  frame timing are mocked in this CPU test.
- Actual SimCoupe, 256 KB internal and zero external RAM: double buffering,
  any-key and Space input, Z/X walks, roll/jump release, original palette,
  all five persistent background colours and return to black. Automated samples
  require a black blank-screen pixel throughout ordinary animation and a single
  stable selected colour during palette tests.
- Actual VS Code F5 also built and launched the final cropped Blog 6 program;
  `out\verification\blog06-f5-simcoupe.png` records that running instance.
- Separate runtime guards cover invalid rectangles, short resident arrays,
  malformed compressed packets, zero frame waits and any-key active-low masks.
  Existing ROM bridge, string guards, negative-budget and portability fixtures
  continue to pass.

Screenshots and measurements live in ignored `out\verification`; GUI helpers
close only their owned PID and refuse input if its window is not foreground.
No physical hardware, alternate ROM revision, integrated C# stepping, GIF,
commit or push was performed.
