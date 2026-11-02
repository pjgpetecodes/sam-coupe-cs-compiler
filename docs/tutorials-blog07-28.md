# Tutorial ports: Blogs 7–28

These are standalone C# programs compiled by the native Roslyn-to-Z80
toolchain. The game rules, state, movement, collision, item handling, actors,
sound sequencing, and update order are readable C#; typed `Sam` intrinsics
perform the bounded hardware operations. Open any listed `Program.cs` in VS
Code and use F5, or build it directly:

```powershell
python .\.vscode\sam_run.py .\tutorials\Blog22\24-1-AnimatedWater\Program.cs --build
```

Each program folder contains its own `.csproj` for editor type-checking, the
complete C# source data it needs, and any source binary assets used to derive
that data. Sprite, scenery, collision, item, and actor data are included with
permission; a fresh checkout does not need the reference repository to build.
The reference checkout is read-only and is used only by the explicit
asset-generation and source-comparison tools. Generated `out` directories are
build products; tutorial input data and `*.g.cs` payloads are not ignored.

## Entry points and progression

| Blog | Program folder | New lesson |
|---|---|---|
| 7 | `Blog07\09-1-BankedSpriteData` | Banked sprite frames and switching the lower RAM window while preserving code/data |
| 8 | `Blog08\10-1-ByteStepMovement` | Byte-aligned movement and per-page previous positions |
| 9 | `Blog09\11-1-PixelPositioning` | Pixel positions, odd/even sprite alignment, and one-pixel shifting |
| 10 | `Blog10\12-1-StaticPalm` | Place one static object into a scene |
| 10 | `Blog10\12-2-StartingBeach` | Build and draw a banked starting background |
| 11 | `Blog11\13-1-BackgroundRestoration` | Restore old sprite rectangles from clean scenery |
| 12 | `Blog12\14-1-RoomComposition` | Data-driven clipped room records and transparent composition |
| 13 | `Blog13\15-1-HorizontalCollision` | Inset body boxes and pixel-by-pixel horizontal collision |
| 14 | `Blog14\16-1-GroundFollowing` | Find nearby supported rows while walking |
| 14 | `Blog14\16-2-LedgeSupport` | Distinguish a wall from the loss of ground support |
| 15 | `Blog15\17-1-GravityAndLanding` | Signed vertical velocity, per-pixel falling, and landing |
| 16 | `Blog16\18-1-PhysicalJump` | Fresh grounded Space presses and a physical jump arc |
| 16 | `Blog16\18-2-CeilingCollision` | Stop upward movement at a solid ceiling |
| 17 | `Blog17\19-1-ConnectedRooms` | Explicit grounded exits between matching room/collision pairs |
| 18 | `Blog18\20-1-CollectingChest` | Grounded, stopped, overlapping Enter pickup |
| 19 | `Blog19\21-1-VisibleInventory` | Three-slot LIFO inventory and visible item icons |
| 19 | `Blog19\21-2-UnsafeDrop` | Validate a drop before committing item ownership |
| 20 | `Blog20\22-1-PhysicalChest` | Drop the chest as a support surface and use it to reach an upper room |
| 21 | `Blog21\23-1-FourScreenRoute` | Add the fourth screen and explicit upper/lower room links |
| 22 | `Blog22\24-1-AnimatedWater` | Stage four masked wave frames, restore the water strip, and redraw the hidden page |
| 23 | `Blog23\25-1-HazardDetection` | Detect inclusive hazard rectangles without making them solid walls |
| 24 | `Blog24\26-1-DeathRestart` | Snapshot persistent state, flash during a one-life pause, and restore the world |
| 25 | `Blog25\27-1-MovingBee` | Add a byte-aligned actor with independent direction, flap, and timer state |
| 26 | `Blog26\28-1-BeeHazard` | Move a hazard rectangle with the bee and reuse the death consequence |
| 27 | `Blog27\29-1-CompleteRoute` | The complete four-screen engine; intentionally no new behavior |
| 28 | `Blog28\30-1-SustainedTone` | Configure one continuous SAA1099 tone |
| 28 | `Blog28\30-2-TriggeredEffect` | Trigger a non-blocking, edge-retriggerable three-note effect |
| 28 | `Blog28\30-3-MelodyAndEffect` | Run an independent melody and effect on separate chip channels |

Blogs 7–27 preserve their incremental game progression: later folders copy the
source data needed to build independently, rather than depending on another
tutorial's checkout at compile time. Blog 27 is intentionally byte-identical
to Blog 26 at the native program-binary level because its reference lesson
assembles the finished engine without changing any feature code.

Blog 28 is sound-only and has three separate entry points. `Sam.WriteSaaRegister`
is a typed intrinsic that emits the SAA1099's two real `OUT (C),A` transactions:
register select at `0x01FF`, then register data at `0x00FF`. These examples
leave ROM mapping available for frame waits and do not alter the graphics
engine, paging, display, or palette.

## Assets and reproducible checks

The generated sprite/frame sources retain named `#region` blocks, source
dimensions, mask/pixel order, and frame order. The Blog 25 bee's original
11-byte-by-22-row images are included in each later program that uses them;
`tools\generate_tutorial_assets.py --bee-frames` converts zero colour nibbles
into transparent mask bits while preserving the original pixels.

Build and validate all registered early ports plus every discovered standalone
tutorial program, check each EDSK and resident end, and save measurements:

```powershell
python .\tools\verify_tutorials.py --build
```

Run the source-scene, collision, inventory, animation, and route CPU checks
with the reference repository configured in ignored `tools.local.json`:

```powershell
python .\tools\verify_tutorial_scene.py
python .\tools\verify_extended_tutorials.py
```

The CPU harness executes emitted Z80, models SAM memory paging and display
pages, and supplies ROM-frame pacing and keyboard samples. A few tests seed a
room/player state directly to make a particular collision branch deterministic;
they are not complete keyboard-driven route playthroughs. It is not a substitute
for an audible or GUI SimCoupe recording. The builds enforce a resident end
below `0xE000`, leaving the `0xFF00` stack clear. At the latest verified build,
Blogs 20–27 ended at
`0xC043`, `0xC11B`, `0xC7A5`, `0xC988`, `0xD038`, `0xDD0A`, `0xDDF9`, and
`0xDDF9`, respectively. The largest resident image therefore leaves 519 bytes
below the `0xE000` allocation limit, independently of the higher stack address.

These sizes include the current staged-initializer loader. The disk verifier
now checks the actual MGT AUTO sector chain and CODE headers, source/assembly
map hashes, and all required input files against Git ignore rules. Its
`runtime_loaded` measurement counts only the resident runtime, not banked
assets or gaps in a contiguous disk image. Per-program resident occupancy,
bank-3 code/staging extent, emitted method count, stack headroom and disk SHA256
are saved in `out\verification\tutorial-sizes.json`. The maximum actual AUTO
load across the 46 programs is 159,748 bytes.

## 13-1 background-restoration performance

Tutorial **13-1** is in `Blog11\13-1-BackgroundRestoration`, not Blog13.
Its full-background setup and old-sprite restoration now use the existing
guarded `Sam.CopyBankedBytesToScreen` rectangle copier instead of C# row loops
that repeatedly map banks, copy, restore mapping and validate individual rows.
The copier still stages each row in resident memory before mapping the hidden
screen; scenery and screen RAM are not assumed simultaneously mapped.

The compiler also now recognizes fixed-row screen expressions such as
`115 * 128 + x / 2` for byte X, avoiding general 32-bit division without
narrowing the observable C# result. Eligible programs gain this optimization
automatically on rebuild; the report calls it `fixed-row-screen-offset`.
The rectangle-copy migration is specific to this tutorial, not an automatic
replacement of arbitrary C# loops.

Native measurements use the same initial state, 24 idle updates and first
24 rightward walking updates:

| Metric | Before | After |
| --- | ---: | ---: |
| Startup nominal T-states | 4,614,003 | 2,878,976 |
| Mean rightward update nominal T-states | 190,042 | 70,170 |
| Maximum rightward update in the sample | 259,563 | 139,510 |
| Resident reservation, bytes | 8,657 | 6,809 |

This is about 63% less mean walking CPU work. Counts exclude ROM interrupts,
HALT time and SAM contention; they are not wall-clock timings, worst-case
bounds or proof of parity with handwritten assembly. Controls, frame delays,
sprite data and presentation scheduling are unchanged. Frame changes still
cost more than ordinary movement because they load and shift sprite data.

Native regressions compare complete scenery through both edge clamps,
odd/even positions, left-key priority and released rolls/sideways jumps, with
startup and walking CPU ceilings. All 114 compiler tests passed. The rebuilt
disk also passed 147 exact displayed-page checks in an owned SimCoupe across
both walks and released actions. Those deterministic checks use explicit
debugger-supplied input samples, as described below. A separate unmodified
host-keyboard check moved X from 0 to 57 with X and back to 25 with Z; its SDK
input code was unchanged. The owned emulator was closed.

## Blog12 startup follow-up

`Blog12\14-1-RoomComposition` previously appeared stuck on a blank cyan screen
while its C# per-pixel scenery loop ran. Native execution eventually produced
the correct room, but startup required 102,657,806 nominal T-states. It now
uses the existing guarded `Sam.ComposeTransparentImage` for each room record,
plus `Sam.CopyBankedBytesToScreen` for initialization and sprite restoration.
Record parsing/order, transparent zero nibbles, source cropping/stride, input,
animation and presentation scheduling remain unchanged.

Startup now requires 12,362,850 nominal T-states (about 88% less CPU work);
resident reservation fell from 11,863 to 10,305 bytes. These counts exclude
ROM interrupts, HALT time and SAM contention, not elapsed startup seconds.
The native regression enforces a 13-million-T-state startup ceiling and exact
source-derived scenery through both walking clamps. The rebuilt disk passed
147 exact SimCoupe scene checks for walks and released actions; separate
unmodified host input moved X from 0 to 56 and back to 24. The editor project
built without warnings or errors, and the owned emulator was closed.

## Tutorial-wide responsiveness audit

The follow-up to Blog13's apparent failure rebuilt all **47** tutorial disks
and checked their load chains, resident budgets and source-map identities.
Blog13 had the same expensive room-composition loop as Blog12, plus a C#
per-row collision-column scan. Blogs14–16 and18–27 also retained a costly
per-row body-clearance loop.

Blog13 now uses the existing guarded transparent compositor, staged screen
copier and packed collision-column probe. Blogs14–16 also use the staged
copier. All affected body-clearance helpers now call the existing packed
rectangle probe, still checking every one of the ten-by-seventeen body pixels.
Slope searches, per-pixel movement, support, gravity, jump rules, items, hazards,
animation timing and room transitions remain ordinary C#. Blog17 already
used these paths and did not need another migration.

| Port | Startup T-states before / after | Mean right-update T-states before / after |
| --- | ---: | ---: |
| Blog13 horizontal collision | 123,123,515 / 14,545,320 | 1,074,993 / 78,659 |
| Blog14 ground following | 5,496,790 / 3,708,628 | 1,324,909 / 114,833 |
| Blog15 gravity | 5,902,757 / 3,642,969 | 1,929,767 / 135,098 |
| Blog16 physical jump | 5,439,326 / 3,626,698 | 1,352,171 / 117,049 |
| Blog18 chest collection | 9,851,196 / 7,588,769 | 1,311,877 / 117,275 |
| Blog27 complete route | 12,376,974 / 9,242,324 | 1,374,805 / 156,896 |

These are nominal CPU measurements, not wall-clock timings or parity claims.
The walking sample is the first 24 right updates from each program's initial
state. Room transitions, item actions and restarts can still require much more
work than ordinary walking.

The complete native scene/extended suites pass scenery, wall/ceiling collision,
support, ledges, gravity, jumps, transitions, inventory, water, hazards, death,
restart, bee and sound-register/timer checks. A separate 286-update control
trace for every Blog7–27 entrypoint matched all **7,150** pre-fix displayed-page
hashes exactly. Early sprite/palette tests, the first two executable tutorial
tests and all 16 tooling tests passed too. The early harness now explicitly
uses the paged machine for banked Blogs7–9 instead of attempting to load their
large sparse disk images into the resident CPU window.

`tools\verify_tutorial_scene.py` now enforces startup and sampled walking CPU
ceilings across every Blog7–27 entrypoint and writes
`out\verification\tutorial-responsiveness.json`. Run the following after
building the disks:

```powershell
python .\tests\SamRuntime\tutorial_test.py
python .\tools\verify_tutorial_scene.py
python .\tools\verify_extended_tutorials.py
```

These are regression ceilings for measured samples, not worst-case deadlines.
The native harness excludes ROM interrupts, HALT time and SAM contention.
Fresh owned SimCoupe checks covered eight repaired entrypoints: Blog13,
both Blog14 examples, Blog15, both Blog16 examples, Blog18 and Blog27.
All **592** displayed-page checks matched independently executed native state
after synchronizing idle animation and buffer parity. Inputs were explicit
debugger-supplied row samples; published disks were unchanged. Each check used
the real `--launch-built` workflow, and every owned process was closed.
Results are in `out\verification\tutorial-audit-actual.json`.
No new audio capture or complete GUI playthrough of every tutorial is implied.

## Real-emulator feature fixtures

The Windows-only `tools\verify_actual_tutorials.py` reuses the precise
source-derived CPU cases against owned SimCoupe instances. It identifies only
its child's SAM RAM using immutable native-code signatures, runs the real
disk/ROM startup, stops at authored frame waits, and compares the displayed
24 KB page byte-for-byte and source-mapped scalar fields after each update.
A physical resident-page breakpoint is installed before boot finishes. A debugger-only
frame counter preserves incoming AF, executes the original wait's
`PUSH AF`/`PUSH BC`/`OR A`, and stops before its continuation; completed updates
are therefore not inferred from a cached Windows bitmap. Its processes are closed by owned
PID, and input/screenshots require the owned foreground window.

```powershell
python .\tools\verify_actual_tutorials.py --suite 13 14 15 16 17 18 19 21 22 23-24 25 26-27 27
python .\tools\verify_actual_tutorials.py --audio
python .\tools\verify_actual_tutorials.py --launch-check
python .\tools\verify_actual_tutorials.py --host-check
```

**Input scope:** the separate unmodified host check confirmed Windows Z input:
Blog 14's player moved from X=120 to X=103 while its input SDK remained unchanged.
This confirms that sample, not every control or an entire route playthrough.
The deterministic feature fixtures
explicitly replace the SDK's `IN A,(C)`/`CPL` boundary in emulator RAM with
debugger-supplied SAM row samples. This does not replace game rules, key latches,
collision, drawing, paging, or ROM interrupts; published disks are not patched.
Room-boundary/actor tests also record their deliberately seeded scalar state.
These are actual emulator feature tests with controlled inputs, **not**
unmodified end-to-end host-keyboard playthroughs or physical SAM verification.
Results, input sequences, pixel-check counts, disk hashes, evidence and cleanup
are recorded in `out\verification\actual-acceptance.json`.
Cases that retain two machines keep both real instances paused and switch only
between their owned windows; they do not transplant computed CPU-oracle state.
The separate host check leaves the input SDK unchanged, and the launcher check
uses the real `--launch-built` command and verifies unchanged published hashes.

Audio verification separately captures the emulator's PCM output through the
default Windows render-device loopback, requiring a silent baseline before
attributing it to SimCoupe. Tone frequency and effect silence/enable periods
are measured, while the exact original SDK SAA register transactions and
4/4/8-tick effect and 8/12-tick melody timers are checked by the native CPU
trace. Triggered audio fixtures use the same explicit row-sample boundary.
PCM evidence is not a claim of hearing a physical speaker or a real SAA1099.

## Final bounded acceptance

All 46 disks, source-map identities and required-input checks passed; the
managed suites passed 87 tests and formatting passed. The new feature fixtures
cover 17 entrypoints with 2,045 exact displayed-page checks. Every owned process
was closed. `--launch-built` also reached initialized native RAM without
changing its assembly, disk, map or generated runtime hashes.

“Prior GUI” below identifies the existing acceptance evidence, not a repeated
feature playthrough in this final pass. Counts include multiple focused cases
for one entrypoint where appropriate. Controlled row inputs and deliberate
room/actor fixtures retain the limitations described above.

| Blog | Entry points | Actual acceptance | Exact frames this pass |
| --- | ---: | --- | ---: |
| 2 | 4 | Prior GUI: text, screen writes, opaque sprites | — |
| 3 | 4 | Prior GUI: masks, stripes, interleaving | — |
| 4 | 4 | Prior GUI: frames, tables, double buffering | — |
| 5 | 3 | Prior GUI: keyboard/walk/jump examples | — |
| 6 | 3 | Prior GUI: palette and cropped jump variants | — |
| 7 | 1 | Prior GUI: banked frames | — |
| 8 | 1 | Prior GUI: byte-step movement | — |
| 9 | 1 | Prior GUI: pixel positioning | — |
| 10 | 2 | Prior GUI: palm/beach | — |
| 11 | 1 | Prior GUI: background restoration | — |
| 12 | 1 | Prior GUI: room composition | — |
| 13 | 1 | Horizontal collision, source-derived X=38 wall | 51 |
| 14 | 2 | Ground following and ledge support/loss | 132 |
| 15 | 1 | Gravity, landing, post-landing movement | 56 |
| 16 | 2 | Fresh-ground jump, held/no airborne retrigger, ceiling | 254 |
| 17 | 1 | Two-way room transitions | 123 |
| 18 | 1 | Gated collection, airborne rejection, persistent return | 234 |
| 19 | 2 | Three-slot HUD/LIFO/rejection, unsafe ownership-preserving drop | 167 |
| 20 | 1 | Prior GUI: physical chest scene; native source checks retained | — |
| 21 | 1 | Grounded room 3 ↔ 4 lower-route links | 3 |
| 22 | 1 | Source-exact water and three-update cadence | 16 |
| 23 | 1 | Traversable sea contact and leaving hazard | 191 |
| 24 | 1 | Death flash, blocked input, initial-world restart | 191 |
| 25 | 1 | Exact bee image/cadence and full patrol bounds | 404 |
| 26 | 1 | Deterministic bee-body fatal-contact fixture | 2 |
| 27 | 1 | Collection/HUD, death/input block, snapshot restart, room 4/bee | 221 |
| 28 | 3 | Actual PCM: 220 Hz tone, one-shot silence/tail, melody plus effect | PCM |

Machine-readable results are in `out\verification\final-acceptance.json`,
alongside individual GUI, host-input, launch/hash and audio evidence.
