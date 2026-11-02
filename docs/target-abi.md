# Target ABI and resident layout

The compiler emits Z80 assembly for pyz80, not a .NET image. Runtime-specific
hardware behavior is implemented by `runtime/sam/hardware.asm`; the CLI stages
that file beside the generated assembly as `sam-runtime.asm`.

## Image entry and layout

Generated files use the runtime's native load contract:

```asm
cs_initial_screen_page: EQU 4
        ORG 0x8000
        DUMP 1,0
        AUTOEXEC
        JP sam_start

cs_main:
        CALL cs_method_main
        RET

        ; reachable compiler methods, pooled strings, and static slots
        INCLUDE "sam-runtime.asm"
cs_resident_end:
```

`sam_start` is the entry point; it saves the required BASIC state, establishes
the resident stack, calls `cs_main`, and exits through `sam_exit`. The compiler
must not call `sam_start` or ROM routines directly. The image including the
runtime and its state must end strictly below `0xE000`; the CLI checks the
assembled symbol map for its final image-end label.

The compiler selects `cs_initial_screen_page` from even pages 4..14, excluding
every physical page occupied by reachable banked code or array data (including
arrays spanning two 16 KB pages). Page 4 remains the default when free. Both
the startup ROM mode call and native clear use this reserved pair; otherwise
startup would erase banked assets before C# configures its drawing pages.
No free pair is a compile-time error. Native runtime fixtures must supply this
equate. `Sam.ClearScreen()` clears the current drawing page, not always page 4.

Banked all-zero arrays are initialized in their authored physical pages before
the C# entry method, rather than loading empty working buffers from disk.
Banked groups whose allocated span reaches physical page 12 (including a large
array starting on page 11) retain their original addresses and values but
are initialized from compiler-owned count/value runs staged after banked code
in page 3. This keeps the contiguous SAMDOS CODE load below physical page 12;
it does not move the application's collision buffers or change their contents.
The staging plus code must fit one 16 KB page; the assembler checks its end.
Deferred array labels are equates, not high-page `DUMP` sections: even an empty
`DUMP` can extend pyz80's disk file header beyond its emitted object bytes.

`0xE000..0xFEFF` is reserved for the downward-growing stack. Its top is
`0xFF00`; the debugger-readable `sam_fault_code` byte is at `0xFF00` outside
that stack range. The compiler emits `cs_resident_end` after the runtime but
does not reserve stack bytes in the file.

## Calling convention and storage

Compiler methods have unique static storage slots for every parameter, local,
and materialized intermediate expression value. Calls evaluate arguments in
source order into caller temporaries, then copy values to callee parameter
slots in parameter order before `CALL`. This preserves argument evaluation
order even when an argument calls another method. Source-method calls use
ordinary Z80 `CALL`/`RET`; no register or flags value is live across a call.

Each method's slots are distinct. This is safe because recursion and reachable
call cycles are rejected. Shared numeric/string helper scratch and the
four-byte `cs_return_value` slot are reused sequentially; a caller copies a
return value into its own expression slot immediately after the call. There
is no general runtime allocator, frame pointer, closure, or stack-local frame.

Proven constant intrinsic arguments can load ABI registers directly, without
materializing slots. A single byte-to-char local/parameter argument can also
load registers directly with its ASCII guard. No live source value is kept in
registers across either kind of call. Constant local initializers write their
declared destination width directly; discarded increment results are not saved.
Eligible explicitly narrowed byte/ushort expressions with pure scalar leaves
also compute directly into their destination, using registers only within that
expression. Ordinary byte-mask zero comparisons can branch directly on flags.
Neither optimization keeps register values live across a call or statement.

Storage is little-endian:

| C# type | Size | Representation |
|---|---:|---|
| `bool`, `byte`, `sbyte` | 1 | Boolean 0/1; integer low byte |
| `short`, `ushort`, `char` | 2 | Two's-complement or unsigned 16-bit value |
| `int`, `uint` | 4 | Two's-complement or unsigned 32-bit value |
| `string` | 2 | Resident pointer; zero is null |
| string literal data | 2 + N | 16-bit little-endian byte length followed by N ASCII bytes |
| `byte[]` | 2 | Resident address of the 16-bit length prefix; zero is null. Payload starts at pointer + 2. |
| byte-array field data | 2 + N | uint16 length followed by N authored bytes; allocated once in the load image. |
| `SpriteFrame` | 3 | Resident byte-array reference followed by one-byte frame index. |

`char` occupies 16 bits even though current text output accepts only values
0..127. Numeric operations use Roslyn's promoted types and explicit conversion
nodes; 8/16-bit storage never changes the C# expression width.

## Stack proof

`CompileResult.MaxCallDepth` is the longest reachable acyclic source-method
chain, including `Main`. The compiler conservatively reports and checks:

```text
StackBytesRequired = 2 * (MaxCallDepth + 4) + 128
```

Two bytes account for each CALL return address. Four additional call frames
cover the entry wrapper, runtime entry, and the deepest nested compiler helper
(including signed division); 128 more bytes reserve runtime/ROM helper push
and return overhead. The default stack budget is `0x1F00` bytes, the space from
`0xE000` to `0xFF00`. `CompileOptions.StackLimitBytes` may lower the budget,
but cannot enlarge the runtime's physical reservation. Recursive call graphs
are rejected before stack analysis. The separate CLI resident-image check
accounts for code, slots, strings and the complete runtime.

The compiler uses register A/B/C/HL and flags as scratch while emitting
operations. Arithmetic carry is propagated through `LD` instructions only,
which preserve Z80 flags; branches never depend on flags across calls.
Numeric helper subroutines may clobber working registers and flags, but all
live C# values are in static slots. Runtime helpers preserve the registers
specified in `sam-runtime.md`; callers still must not depend on flags surviving
an intrinsic.

## Intrinsic call ABI

| C# intrinsic | Target input | Target call |
|---|---|---|
| `Sam.ClearScreen()` | none | `CALL sam_clear_screen` |
| `Sam.WriteScreenByte(ushort, byte)` | `HL` offset, `A` packed byte | `CALL sam_write_screen_byte` |
| `Sam.Print(string)` | `HL` resident length-prefixed string pointer | `CALL sam_print` |
| `Sam.PrintChar(char)` | `HL` 16-bit character | `CALL sam_print_char` |
| `Sam.WaitForKey()` | none | `CALL sam_wait_for_key` |
| `Sam.DrawOpaqueSprite(byte[], ushort)` | `DE` data, `HL` offset | `CALL sam_draw_opaque_sprite`; caller supplies 512 bytes. |
| `Sam.BuildInterleavedSprite(byte[], byte[])` | `HL` pixels, `DE` masks | `CALL sam_build_interleaved_sprite`; 512-byte planes become 1024 interleaved bytes. |
| `Sam.SelectSpriteFrame(byte[], byte)` | data reference and index | Compiler copies a three-byte `SpriteFrame`; no target call or allocation. |
| `Sam.DrawBuiltMaskedSprite(ushort, byte, byte)` | `HL` offset, `B` width, `C` height | `CALL sam_draw_built_masked_sprite`; draw the previously built buffer. |
| `Sam.DrawMaskedSprite(SpriteFrame, ushort, byte, byte)` | `DE` data, `HL` offset, `A` index, `B` width, `C` height | `CALL sam_draw_masked_sprite`; frame container supplied by C#. |
| `Sam.DrawRleMaskedSprite(SpriteFrame, ushort, byte, byte)` | `DE` data, `HL` offset, `A` index, `B` width, `C` height | `CALL sam_draw_rle_masked_sprite`; checked packet decoding into resident storage. |
| `Sam.ClearSprite(ushort, byte, byte)` | `HL` offset, `B` width, `C` height | `CALL sam_clear_sprite`; clear only that rectangle on the drawing page. |
| `Sam.FillScreen(byte)` | `A` packed byte | `CALL sam_fill_screen`; fills the selected drawing page's first 0x6000 bytes. |
| `Sam.FillStripedBackground(byte, byte)` | `A` first row, `B` second row | `CALL sam_fill_striped_background`; alternates full-width MODE 4 rows. |
| `Sam.SetPaletteEntry(byte, byte)` | `A` CLUT index, `B` colour | `CALL sam_set_palette_entry`; updates retained runtime palette and hardware CLUT. |
| `Sam.LoadTutorialPalette(byte[])` | `HL` data payload | `CALL sam_load_tutorial_palette`; copy supplied 16 entries into retained runtime state. |
| `Sam.WaitForFrames(byte)` | `A` count | `CALL sam_wait_frames`; HALT only with ROM0 mapped, then restores retained CLUT values. |
| `Sam.IsKeyPressed(ushort, byte)` | `BC` port, `A` active-low bit mask | `CALL sam_is_key_pressed`; stores Boolean 0/1 into `A`. |
| `Sam.DecodeRleBackground(byte[], byte, byte[], byte)` | encoded stream/page and destination array/page | `CALL sam_decode_rle_background`; validates and decodes exactly 0x6000 MODE 4 bytes, mapping each bank separately. |
| `Sam.ExpandByteRuns(byte[], byte, byte[], byte)` | count/value stream/page and destination array/page | `CALL sam_expand_byte_runs`; validates a terminated run stream and fills exactly 0x6000 bytes. |
| `Sam.CopyBankedBytesToScreen(byte[], byte, ushort, ushort, byte, byte)` | source array/page, source and screen offsets, width and row count | `CALL sam_copy_banked_rows_to_screen`; stages each row before mapping the hidden display page. |
| `Sam.BeginDoubleBuffer()` / `Sam.FlipScreen()` | none | Start drawing on page 6, then update VMPR and ROM descriptor when swapping pages 4/5 and 6/7. |
| `Sam.Rom.ClearScreen()` | none | `CALL sam_rom_clear_screen` |
| `Sam.Rom.WaitForKey()` | none | `CALL sam_rom_wait_for_key` |
| `Sam.Rom.Print(string)` | `HL` resident string pointer | `CALL sam_print` |
| `Sam.Rom.PrintChar(char)` | `HL` 16-bit character | `CALL sam_print_char` |

The compiler evaluates and stores nonconstant arguments before loading target
registers, except for the proven single-byte character fast path described above.
Runtime helpers return normally except `sam_trap`, which is non-returning. Fault
codes 01, 02, 03, 04 and 06 are emitted by compiler guards; the runtime handles
faults 05 and 07 in hardware/string helpers. See the runtime document for ROM
mapping, interrupt, stack-bridge, screen, keyboard and exit details.

## Build performance report

Normal F5/CLI builds print a concise performance/layout summary and publish
`program.performance.json` with the same build/source identity as the source
map. Compiler-owned boundary labels resolve exact regions after assembly
without inserting instructions. Regions distinguish application, compiler
helpers, prepared code/tables, array headers/payloads, strings, static
slots/scratch, bank initialization and retained runtime.

Each region records physical page, page offset and byte count. Ranges crossing
16 KB pages are split; identical logical addresses on different banks are not
combined. Banked all-zero/deferred arrays count as allocated RAM even when
their payload is not in the file. The resident total includes saved BASIC
state, unlike the physical binary size. Remaining headroom is measured against
the unchanged E000 stack floor; it is not additional stack capacity.

`optimizations` currently records the `byte-mask-predicate` and
`narrow-scalar-destination` rewrites, with source locations. Counts are static
emission sites, not execution frequency, estimated savings or a complete list
of all earlier optimizations. `preparedKernels` and `retainedRuntimeSymbols`
show which explicit paths survived compilation/tree shaking.

Assembly-only compilation publishes rewrite/kernel facts but null sizes and
no resolved regions. A failed build does not publish a new report; any prior
report still belongs to the previous successful generation. An assembly-only
success replaces a prior measured report rather than retaining stale sizes.
Reports are informational, not inputs to program execution.

CPU work is measured separately in native execution regressions, stopping
before HALT rather than counting idle ticks. These nominal T-states exclude
SAM contention and ROM interrupt work. Static reports cannot establish a
whole-program worst case, hot path, FPS or input-to-display latency.

## Debug sequence points

Each executable C# statement may have a `cs_seq_XXXXX` label at the first
instruction emitted for that statement. Blocks and empty statements do not
claim breakpoint locations. The public `SequencePoint` record retains file,
start/end positions and containing method; pyz80's symbol export resolves the
label to the final resident address. A source line may have multiple labels;
the compiler does not claim that unsupported/non-executable lines are valid
breakpoints.
