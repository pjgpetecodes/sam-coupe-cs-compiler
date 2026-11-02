# Native SAM runtime

`runtime/sam/hardware.asm` is an end-of-image pyz80 include, not a standalone
entry point. The CLI stages it as `sam-runtime.asm` beside generated assembly.
The staged copy is tree-shaken, not a verbatim copy of the template.
It contains no `ORG`, `DUMP` or disk directives. Include exactly once, after
compiler code, strings and static frame slots.

## Runtime tree shaking

`RuntimeTreeShaker.Shake` links label blocks from the shipped template.
Startup and the resident-end assertion are unconditional roots. Generated
assembly references select additional roots; branches, calls, symbol operands,
address-taken data and instruction fallthrough retain their transitive
dependencies. Comments and string literals do not create references. Equates
and private data are retained only when referenced. The original template is
unchanged, so direct runtime fixtures still assemble the complete runtime.

This is conservative linking, not constant propagation inside assembly. For
example the shared ROM dispatcher retains both internal operation paths when
either wrapper is used. Observable traps retain their text, keyboard and
ROM-bridge dependencies. Startup still establishes MODE 4, clears its screen
and preserves BASIC state; these are not optional features.

Duplicate labels, missing entry/end labels and indirect JP/CALL transfers are
explicit linker errors. Future indirect dispatch must provide a supported
address-taken root mechanism before it can be used safely in the runtime template.
Prepared sprite dispatch instead lives in the compiler-generated program: all
address-taken kernels and pointer tables are emitted together, outside the
tree-shaken template. It does not weaken the runtime's indirect-transfer check.

The CLI hashes the selected runtime and validates it against a freshly selected
copy of the installed template before launch. Assembly-only output also includes
the reduced runtime. Rebuild old full-runtime artifacts before launching them.
The final assembled layout and ASSERT still cover reserved state, including the
768-byte BASIC-variable buffer which pyz80 need not physically emit at image end.

## Prepared fixed-sprite ABI

The CLI's staged `program.performance.json` lists actual retained runtime
symbols and resolved runtime reservation alongside application, data and slot
regions. This is separate from target CPU timing; see the build-report contract
in [target-abi.md](target-abi.md#build-performance-report). Runtime guards, ROM
mapping, IFF preservation and the stack ABI are not disabled by optimization.

`PrepareFixedSpriteRenderer` generates a setup routine, a frame-pointer table
and a fixed-geometry clear/masked-draw kernel in the resident program. Constant
geometry and exact raw-frame extent are checked by the compiler; setup uses
`sam_validate_data` and checks the frame-count header before committing its
compiler-owned configuration with interrupts disabled. Reconfiguration restores
the entry IFF state and leaves the previous configuration untouched on failure.

`cs_draw_prepared_sprite` takes the frame index in A. It checks initialization
and the index (fault 07), reads the frame address from its protected table,
maps the drawing page once, runs the specialized clear and mask/pixel loops,
then restores ROM or the caller's banked-code mapping. AF, BC, DE, HL, IX/IY
and alternate registers are preserved; unlike ordinary drawing helpers, this
wrapper also restores entry IFF. Flags are not a public C# contract. The kernel
does not flip, wait, sample input or advance animation. No generic decoder
workspace is retained solely for prepared drawing.

`ReadKeyboardRow` emits `LD BC,port / IN A,(C)` and returns a byte, with
compile-time validation of the constant FE port. `IsKeyPressedInRow` emits a
captured-byte load, `CPL`, constant `AND` and a branch or normalized Boolean
result; it performs no second input operation. These primitives preserve C#
evaluation order and short circuits, not scratch-register/flag liveness.

## Entry and memory contract

```asm
cs_initial_screen_page: EQU 4 ; Compiler-selected unoccupied physical pair
        ORG     0x8000
        DUMP    1,0
        AUTOEXEC
        JP      sam_start
cs_main:
        CALL    compiler_user_main
        RET
        ; compiler methods / immutable strings / static slots ...
        INCLUDE "sam-runtime.asm"
```

The loader must provide ordinary SAMDOS/BASIC native entry: ROM0 enabled,
system RAM page 0 in bank B, resident RAM pages 1/2 in banks C/D (`HMPR=01`),
BASIC's return address on its low-memory stack, and normal BASIC IY.
256 KB is sufficient; no 512 KB or external RAM is used. This is **not** a
bare-reset entry or an arbitrary banked machine-code entry.

`[BankedData(page)]` emits the array's two-byte little-endian length and payload
at lower-window address 2 on the selected physical page. pyz80's `DUMP` offset
is a file/page offset independent of the logical `ORG`, so both must be 2:
`ORG 2` with `DUMP page,2`. Using `DUMP page,0` puts the descriptor at physical
offset zero while its C# label still resolves to address 2; mapping the page
then reads payload bytes as the length and fails range checks. Address zero is
reserved for null references. Banked pages must remain mapped while reading
their arrays, and the caller must restore ROM/system mapping before ROM calls,
interrupt waits or screen access.

**Do not CALL `sam_start`.** It saves both register sets, IX/IY, the BASIC
stack pointer, IFF2, LMPR/HMPR/VMPR and BASIC variables `5A00..5CFF`, replaces
SP with `FF00`, calls compiler-provided `cs_main`, then jumps to `sam_exit`.
It also saves the ROM's 40-byte two-phase palette table at `55D8..55FF`;
exit restores this table and the hardware CLUT.
Exit restores the original stack and saved registers and returns to BASIC.
The runtime does not change I or the interrupt mode; it restores interrupt
enable state. R, elapsed time and pending device interrupts are not restored.

The full resident image, **including this runtime's state buffer**, must end
strictly below `E000`. Its assembler `ASSERT` rejects overlap. Reserve
`E000..FEFF` for a downward-growing stack; `FF00..FFFF` is not emitted as
code/data and must not contain compiler slots. `sam_fault_code EQU FF00` is
a fixed debugger-readable byte, initially zero. First stack push is at FEFE,
so it cannot overwrite that byte. No `ORG`/`DEFS` extends the load image into
the reserved stack. Stack-depth proof, including nested compiler calls and
runtime push/return overhead, belongs to the compiler/CLI; there is no target
stack-overflow detector. Allow at least 128 bytes for runtime resident
overhead in addition to compiler temporaries and return addresses.

ROM far calls temporarily enable ROM1 over `C000..FFFF`. Therefore **a ROM
call must not use the resident FF00 stack**. Text output switches SP inline
to the saved low BASIC stack, supplies saved BASIC IY, invokes ROM output,
then restores resident SP and all registers. Startup/exit mode calls also
use BASIC's stack. No helper changes SP and attempts to RET to a return
address left on the other stack. The low BASIC stack needs ordinary ROM
call headroom and must remain untouched by compiled programs.

## Public ABI

Except for `sam_is_key_pressed` (Boolean result in A), normally returning helpers preserve A, BC, DE, HL, IX, IY and both
alternate register sets. Flags are unspecified. Helpers leave interrupts
disabled and ROM0 mapped; callers must not depend on IFF or flags surviving
calls. Compiler code must not change paging, streams, palettes or interrupt
mode, or call ROM directly.

| Label | Input | Behavior |
|---|---|---|
| `sam_clear_screen` | none | Zero all 24 KB of the MODE 4 screen; does not reset ROM text cursor. |
| `sam_write_screen_byte` | HL offset, A packed byte | Write two pixels; reject HL >= 6000 with fault 05. |
| `sam_print` | HL string reference | uint16 little-endian **byte** length, followed by exactly that many immutable ASCII bytes. |
| `sam_print_char` | HL uint16 C# char | Values 0000..007F only; others fault 06 rather than truncate. |
| `sam_wait_for_key` | none | Wait initial release, fresh press, then release; includes SAM extended matrix keys. |
| `sam_draw_opaque_sprite` | DE supplied data, HL offset | Copy a checked 16×32 opaque sprite. |
| `sam_build_interleaved_sprite` | HL pixels, DE masks | Convert supplied 512-byte planes into 1024 interleaved bytes. |
| `sam_draw_built_masked_sprite` | HL offset, B width, C height | Draw the buffer built from C# data. |
| `sam_draw_masked_sprite` | DE data, HL offset, A index, B width, C height | Draw one checked supplied mask/pixel frame. |
| `sam_draw_rle_masked_sprite` | DE data, HL offset, A index, B width, C height | Decode supplied repeat/literal mask and pixel streams, then draw. |
| `sam_clear_sprite` | HL offset, B width, C height | Zero only the animation rectangle on the drawing page. |
| `sam_fill_screen` | A packed byte | Fill 0x6000 bytes on the currently selected drawing page. |
| `sam_fill_striped_background` | A first row byte, B second row byte | Fill 192 alternating 128-byte rows. |
| `sam_set_palette_entry` | A CLUT index, B colour | Update the retained palette and hardware CLUT. |
| `sam_load_tutorial_palette` | HL supplied palette | Copy 16 supplied entries into retained state, hardware CLUT and both ROM phases. |
| `sam_wait_frames` | A frame count | HALT with ROM0 mapped, then reapply retained palette after every interrupt; zero returns. |
| `sam_is_key_pressed` | BC port, A active-low mask | Return A=1 when any selected key bit is pressed, else A=0. |
| `sam_shift_masked_sprite_one_pixel` | Runtime scratch holds distinct source/destination array headers | Validate 573-/617-byte buffers and shift a 13×22 interleaved sprite by one pixel, retaining transparent edge nibbles. |
| `sam_begin_double_buffer` / `sam_begin_double_buffer_pages` / `sam_flip_screen` | none, or A=first page and B=alternate page | Draw on the selected hidden page, change VMPR only after drawing, and synchronize BASIC's screen descriptor. The no-argument form keeps the default 4/6 pair. |
| `sam_rom_clear_screen` | none | Typed `Sam.Rom.ClearScreen`: ROM JCLSBL 014E (334 decimal), A=0; resets ROM screen/cursor. |
| `sam_rom_wait_for_key` | none | Typed `Sam.Rom.WaitForKey`: ROM JREADKEY 0169 polls translated press; no character return. |
| `sam_trap` | A code | Nonreturning: display `SAM TRAP XX`, wait fresh key/release, exit to BASIC. CALL or JP accepted. |
| `sam_exit` | none | Nonreturning native exit; abandon current compiler frame and return to BASIC. |

Strings are not NUL-terminated. Printing checks null, resident pointer,
prefix and whole byte span (including carry/wrap) before accessing text.
The span must end below E000. Empty strings return without printing.
Every byte must be < 80. Embedded NUL occupies a space and never truncates
the remaining text. LF and CR become SAM newlines; other ASCII controls
occupy a space; DEL becomes `?`. This intentionally prevents ASCII controls
from becoming SAM palette, positioning or token commands. Printable text
uses ROM RST 16 on explicitly selected screen stream 2. ROM scrolling and
its possible interactive scroll prompt remain ROM behavior, not a host
console or a promise of unattended arbitrarily long output.

Fault codes agreed with the compiler:

| Hex | Meaning |
|---|---|
| 01 | Null string |
| 02 | Compiler string-index bounds failure |
| 03 | Compiler division by zero |
| 04 | Compiler signed division overflow |
| 05 | Screen byte offset out of range |
| 06 | Non-ASCII char or string byte |
| 07 | Invalid resident string span / arithmetic wrap |
| 08 | Array-copy bounds / aliased shift buffers |

Codes 02..04 are emitted by compiler checks; hardware runtime does not perform
string indexing or arithmetic. Trap resets the text output cursor with ROM
clear, produces visible hex status and does not resume failed code. The fault
byte remains observable while stopped in the trap; no persistence after
return to BASIC is promised.

Startup sets MODE 4 and clears `cs_initial_screen_page`, which the compiler
chooses to avoid banked code/data. It retains pages 4/5 for programs without
conflicting assets. Later explicit double-buffer configuration does not protect
assets from an earlier startup clear; the reservation must precede JMODE.
The native clear also honors `sam_draw_page`, so clears after buffer selection
do not overwrite the original default pair. ROM calls still use BASIC's low
stack, and the resident stack and palette/interrupt contracts are unchanged.

`python tests\SamRuntime\startup_banked_test.py` executes the full startup on a
physical 16 KB bank model, including ROM-call low-stack checks, three alternative
screen pairs, a page-8 control, and a negative control reproducing erased page-4
array headers. Only ROM far calls are mocked; it does not substitute a call
directly to `cs_main` for startup. Actual SimCoupe evidence is checked separately.

Array-copy failures consistently report `08`, including source/destination
ranges and aliased shift buffers; the source length is not a diagnostic code.
`python tests\SamRuntime\sprite_guards_test.py` checks these before any write.

`python tests\SamRuntime\loader_test.py` extracts the actual MGT AUTO CODE
sector chain and executes its loaded bytes through startup, checking full
bank-12 byte/word initializers, a bank-14 zero buffer, and unchanged neighbors
and bank-3 code. High-page data is initialized before the authored Main, with
original length prefixes and values. The private initializer uses scratch
registers, leaves interrupts disabled, and restores the ROM mapping.

The CLI validates both the AUTO directory and its first sector's CODE header
before publishing/converting a disk. In the verified 256 KB boot contract,
the contiguous physical load must end before page 12. This is separate from
the `E000` resident stack budget: a sparse `program.bin` size is not proof of
the actual disk load extent. All required C# data remains tracked and in its
authored runtime page; no asset is removed to shrink the disk.

### Typed ROM surface

`Sam.Rom.Print` / `PrintChar` alias the existing safe `sam_print` /
`sam_print_char` bridges. `Sam.Rom.ClearScreen` / `WaitForKey` use the new
`sam_rom_clear_screen` / `sam_rom_wait_for_key` labels. These are narrowly
typed wrappers, **not an arbitrary ROM-address call API**. They retain the
same normal native-entry restrictions and register-preservation ABI.

Both no-argument ROM helpers explicitly map ROM0, save the resident stack
pointer, switch inline to BASIC's low stack and supply BASIC IY. Clear uses
JCLSBL with A=0, as the tutorial examples specify. Wait enables interrupts
and uses HALT only while ROM0 and the low BASIC stack are active. It polls
JREADKEY's **Z flag** (no translated character), not A as a Boolean, for the
press. It deliberately exposes no returned character.

An empty translated-key buffer is **not proof of physical release**: Z can
occur while a key is held between repeat events. Initial and final release
therefore use the physical SAM matrix scanner, preventing a held press from
satisfying two consecutive waits. Modifiers alone need not produce a ROM
translated character and thus are not promised to satisfy this ROM wrapper.
ROM frame interrupts can update system keyboard variables and the CLUT;
return restores DI, ROM0, the resident stack and all saved registers, and
reapplies the runtime palette. Direct `Sam.WaitForKey` still uses physical
polling with no interrupts or HALT.

## Hardware behavior

MODE 4 is explicitly established with ROM JMODE (`015A`, A=3, zero-based),
ROM screen descriptor `5A78` and VMPR `64`. Screen pages 4/5 are distinct from
system RAM page 0 and resident pages 1/2. Pixels use 128-byte rows, 192 rows,
two 4-bit palette indices per byte. Startup blanks all `6000` screen bytes.
The default runtime palette has black entry zero and white entry fifteen.
Tutorials can load the generated Blog 6 palette and then explicitly set entry
zero black or change it in response to the documented background keys. Screen
contents are not saved.

Low screen paging uses LMPR page 4 with ROM0 disabled and ROM1 disabled,
retaining the unrelated write-protect bit. Normal BASIC entry requires that
write protection be off. Interrupts are disabled before this mapping and
ROM0 is restored before returning. `Sam.WaitForFrames` intentionally enables
the ROM interrupt handler and uses HALT only while ROM0 is mapped. It reapplies
the retained CLUT immediately after each interrupt, preventing the ROM's
palette reset from flashing through animated frames. The ROM may enable
interrupts internally while called; DI is re-established on return. Exit
restores original paging and saved ROM palette/display/stream variables;
hardware palette restoration is through normal ROM mode/interrupt behavior,
not a readback of an arbitrary custom ASIC CLUT.

Keyboard polling follows the installed ROM v3.0 D5BC scan: eight active-low
row selections, five FE bits plus three F9 high bits, and the ninth FF row's
four FE bits. Low F9 interrupt status bits are ignored. No dependence on
the ROM interrupt-maintained key buffer, no interrupt/HALT deadlock, and
a held key cannot immediately satisfy the next wait.

## Reproducible verification

The reference repository is read-only. Its `sam_build.py` rejects sources
outside that workspace. `build_runtime_tests.py` uses the same assembler,
SAMDOS and SAMdisk contract through repo `tools.local.json`, or an explicit
`--tools CONFIG`. The JSON keys are `pyz80`, `samdisk`, `samdos`, `simcoupe`.
Executable names resolve on PATH; relative tool paths resolve against the
config directory, not the shell working directory.

All generated sources/disks/maps/binaries/screenshots go under ignored
`tests/SamRuntime/out` by default. `--out DIRECTORY` is available for session
artifacts. The build helper copies the runtime into that output directory and
rewrites the staged fixture include to `sam-runtime.asm`, without modifying
original fixtures. It builds five positive fixtures and requires the negative
fixture to fail specifically on the resident-budget ASSERT.

```powershell
Set-Location tests/SamRuntime
python build_runtime_tests.py
python bounds_test.py
python rom_bridge_test.py
python portability_test.py
# Optional GUI checks, require Windows desktop, mss and Pillow:
foreach ($name in @('smoke','trap','high','registers','rom')) {
    python verify_simcoupe.py $name
}
```

For a non-default config, pass `--tools PATH` to the build, portability and
emulator scripts. Bounds checks consume built files only and accept `--out`.
`portability_test.py` rebuilds from an unrelated working directory using an
explicit config with relative paths and verifies staged include resolution,
nonempty disk artifacts and absence of generated files beside source fixtures.

Actual SimCoupe checks performed (memory size was **not verified as 256 KB**):

The original GUI checks used `-mainmemory 256`, following stale manual text.
The installed executable ignores that option and defaults to 512 KB. Their
screenshots prove the behaviors below, not execution with 256 KB configured.
The verification script now uses the actual option `-mainmem 256`, with the
disk as the first positional argument to trigger autoboot. The corrected
GUI checks have not been repeated as part of this fixture update.

- `smoke`: length-counted ROM text including embedded NUL and separate char;
  held Space leaves text unchanged, release advances to the tutorial packed
  square; final valid offset 5FFF renders at the lower-right; fresh Z/release
  returns to BASIC `0 OK, 0:1`.
- `trap`: offset 6000 visibly produces `SAM TRAP 05`; fresh key/release
  returns to BASIC, never writes an out-of-bounds screen byte.
- `high`: DEFS padding places runtime at C100, proving ROM output works
  with runtime code in bank D and FF00 resident stack. After loading,
  F9/Enter plus `bpx 8014` sets the breakpoint corresponding to
  `high_after_key`; Escape resumes, Space/release hits it at RAM01:0014.
  Debugger evidence shows ROM0, B RAM00, C RAM01, D RAM02, LMPR 1F,
  HMPR 01, VMPR 64, MODE 4, DI and SP FEFE.
- `registers`: native checks compare primary A/BC/DE/HL, IX/IY and alternate
  A/BC/DE/HL after ROM-backed character output, deliberately using non-BASIC
  IY DEF0. The screen reports `A REGISTER ABI OK`, not trap 88.

New ROM fixture evidence is separate from those original GUI checks.
`rom.asm` forces the runtime above C100 and checks the full register ABI
with caller IY DEF0 around both ROM wrappers. One owned run using corrected
`-mainmem 256` showed ROM wait text, clearing/reprinting
`ROM CLEAR/WAIT ABI OK`, and BASIC return. It also exposed the
translated-buffer/physical-release distinction described above: the first
implementation advanced while the key was held. The final matrix-release
fix was assembled and CPU-regression-tested, **not GUI-rerun**, to avoid
racing the parent's compiler/F5 desktop validation. Parent subsequently verified the actual compiled Text sample using all four
`Sam.Rom` wrappers: clear, print `C# on SAM Coupe!` twice via string output
and an indexed character loop, wait for Space, then safely return to BASIC
`0 OK`. The earlier ROM-fixture screenshots remain pre-fix evidence; parent
retained separate final-sample screenshots in session artifacts. A dedicated
held-key/repeat regression GUI sequence was not reported; the final CPU
release regression test covers that control flow.

Parent also verified the compiled Square sample's black background and tiny
top-left square, key-triggered BASIC return, and a mapped C# line 14 breakpoint
at physical RAM01:01ED / PC 81ED after `CALL 32768` restart. Its F10 Options
screenshot confirms **Internal RAM 256K selected**, independently of command
arguments. External RAM was still inherited as 1 MB during those checks;
the runtime does not use external pages, but those runs are not proof with
external RAM disabled. Parent has changed the CLI launch to explicitly use
`-externalmem 0` for subsequent F5 runs; no additional run is claimed here.

PNG evidence is local under `tests/SamRuntime/out` and ignored with generated
disks/maps/binaries. `verify_simcoupe.py` uses PID-scoped Win32
window discovery; it refuses key injection without its owned foreground
window, checks foreground before screenshots too, and terminates only its
launched process. Focus acquisition attaches the current thread to the
foreground thread using `AttachThreadInput`, invokes `ShowWindow(hwnd, 9)`
and `SetForegroundWindow(hwnd)`, then detaches in `finally`. A failed
foreground check aborts instead of capturing the IDE or injecting elsewhere.
Do not run this GUI helper concurrently with another desktop test.
These screenshots are
manually inspected evidence, not an automated pixel oracle.

`bounds_test.py` separately executes the assembled Z80 guard instructions
with the locally installed `z80` CPU package: invalid screen offsets, four
non-ASCII char values, null and nonresident strings, overflowing/oversized
lengths, and a non-ASCII byte. It stops at `sam_trap` and asserts A.
This CPU check does **not** substitute for SAM hardware/ROM validation.
`over_budget.asm` separately verifies that an oversized resident image is
rejected by the runtime's assembler ASSERT.
`rom_bridge_test.py` executes both final wrappers with mocked ROM calls and
frame waits: it asserts low-stack calls, BASIC IY, A=0 for JCLSBL, complete
register ABI preservation, resident stack restoration and DI. Its ROM
JREADKEY returns a single nonzero translated key followed by Z buffer gaps
while the physical scanner remains held for two polls; the wrapper must
remain in release waiting. This check proves the bridge/release control flow,
not actual ROM or interrupt hardware behavior.

256 KB is the runtime's intended internal-memory contract. The original
fixture GUI evidence does not validate that capacity; the later parent
compiled-sample checks above explicitly confirm 256 KB internal RAM.

Not verified on physical SAM hardware, alternate ROM revisions, arbitrary
BASIC screen allocations, nonstandard native entry mappings, custom CLUT
restoration, or maximum-depth compiler stacks. No integrated source debugger,
GIF, reference-repository edit or commit is part of this runtime work.

## Current tutorial validation (supersedes historical sample limitations)

`Sam.BeginDoubleBuffer(firstScreenPage, secondScreenPage)` selects two explicit
MODE 4 screen pages; the second page is initialized as the first hidden drawing
target. `Sam.FlipScreen` alternates between that pair. The no-argument overload
retains the original 4/6 defaults.

`Sam.CopyBytesToScreen` copies a checked resident byte range to the active
hidden MODE 4 page. It rejects screen offsets outside `0x6000` and copies that
cross a 128-byte row boundary; the routine saves the caller's interrupt state
while screen RAM is mapped. Its source must remain resident while the lower
window is switched. `Sam.SetBorderColor` writes the hardware border index and
does not alter the CLUT.

`Sam.WriteSaaRegister(register, value)` is the typed Blog 28 sound intrinsic.
It emits `OUT (C),A` to select the SAA1099 register at full port address
`0x01FF`, then writes its byte value to `0x00FF`; it is not a general-purpose
raw-port API. The sound-only programs keep ROM mapped for their interrupt-paced
`Sam.WaitForFrames` loops. The native CPU verifier checks these two port
transactions, chip reset, fresh Space edges, and independent melody/effect
registers; it does not claim to verify audible output in SimCoupe or on hardware.

The Blog 2–6 work validates all 18 packaged programs and all 16 sprite programs
on a banked-RAM Z80 CPU fixture against the original source bytes. Actual
SimCoupe checks explicitly use 256 KB internal memory and zero external memory,
and exercise animation, double buffers, keyboard controls and stable user-selected
palettes. The black/cyan mechanism is reproduced using a deliberately divergent
ROM palette table, then prevented by synchronizing both phases and retained CLUT.
See [tutorials-blog02-06.md](tutorials-blog02-06.md) for current measurements,
evidence paths, exact checks and remaining limitations.
