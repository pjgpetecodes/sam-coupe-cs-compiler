# Tutorial ports and code-generation comparisons

Work through the reference tutorials in executable source order. Each program
has its own folder and editor project under `tutorials`, and runs with F5.
Keep the existing `samples` as compiler demonstrations, rather than changing
them to stand in for exact tutorial ports.

## First batch: Blog 2, text and screen writes

| Reference source | C# program folder | Behavior |
|---|---|---|
| `assembly\blog2\1.asm` | `tutorials\Blog02\01-PrintingText` | Print character codes 32..95, the 20-character literal `Howsaboutis, then!!!`, then wait for a key. |
| `assembly\blog2\2.asm` | `tutorials\Blog02\02-ScreenWrites` | Write the original eight packed bytes in their original order. |

`blog1` has no assembly. The text source's comment says 96 characters, but its
actual loop prints 64; the C# port follows the executable range. Its string
counter of 20 is correct.

The screen-write port adds a key wait so the square can be inspected before
returning. Both programs use the runtime's black MODE 4 screen, safe ROM stack
bridge, saved paging state and return-to-BASIC handling. These intentionally
avoid reproducing the early examples' hard-coded restore state.

```powershell
python .\.vscode\sam_run.py .\tutorials\Blog02\01-PrintingText\Program.cs --build
python .\.vscode\sam_run.py .\tutorials\Blog02\02-ScreenWrites\Program.cs --run
```

## Historical first-optimization native sizes

The following table predates runtime tree shaking and the sprite/interrupt
support. Current assembled measurements for all 18 ports, with runtime overhead
separated, are in [tutorials-blog02-06.md](tutorials-blog02-06.md).

These are assembled bytes, not assembly file lengths or line counts. The
baseline is the same C# ports before the first optimization batch. The
handwritten originals were independently assembled with pyz80.

| Region | Handwritten text | C# text before | C# text now | Handwritten screen writes | C# screen before | C# screen now |
|---|---:|---:|---:|---:|---:|---:|
| Executable application body | 28 | 231 | 48 | 53 | 340 | 68 |
| Complete compiler region before runtime | N/A | 344 | 134 | N/A | 460 | 131 |
| Shared native runtime region | N/A | 726 | 726 | N/A | 726 | 726 |
| Complete loaded binary | 48 | 1070 | 860 | 53 | 1186 | 857 |

The C# application body is `cs_method_001` through (but not including)
`cs_trap_string_bounds` in these single-method programs. The compiler region is
`0x8000` through (but not including) `sam_start`, including the entry trampoline,
guards, literals and scratch storage. The image length comes from `program.bin`.
The handwritten text binary contains 28 instruction bytes and 20 literal bytes.
Disk sizes are not a code-size metric.

The application-body reduction is approximately 79% for text and 80% for
screen writes. **The handwritten originals are still smaller.** Their inline
ROM/paging operations are not equivalent in safety or layout to runtime calls;
the screen C# body also contains the extra wait.

For a direct comparison of the drawing kernel, one original write is:

```asm
        LD HL,0x0080
        LD (HL),0x44
```

That is 5 bytes and 20 nominal Z80 T-states. The compiler now emits:

```asm
        LD HL,128
        LD A,68
        CALL sam_write_screen_byte
```

That is 8 bytes and 34 nominal T-states **before the called runtime body**.
Previously the argument materialization and call took 42 bytes and 184 nominal
T-states. The eight-write kernels are therefore 40, 336 and 64 bytes respectively.

The original repeating ASCII-loop kernel is 8 bytes and 42 nominal T-states
per taken iteration, including the RST instruction but not ROM execution.
The generated test/character-call/increment kernel is now 33 bytes and 131
nominal T-states per entered iteration, including CALL but not runtime execution.
This is not a claim of equal end-to-end speed: the C# ASCII guard and safe
ROM bridge add work, and SAM contention/wait states are not included.

To reproduce the original sizes, use a local reference checkout; write the
comparison artifacts into an ignored output folder, not over the originals:

```powershell
$reference = 'C:\repos\petecodes\samcoupegraphics'
$out = '.\tutorials\Blog02\01-PrintingText\out'
pyz80 "--obj=$out\original.bin" "--mapfile=$out\original.map" "$reference\assembly\blog2\1.asm"
Get-Item "$out\original.bin" | Select-Object Length
```

Repeat for `2.asm` using the second program's output folder. Compare generated
addresses in `out\program.map`; aliases are retained in `program.symbols` when
multiple source labels share an address. Native execution tests enforce
application-body budgets of at most 48 and 68 bytes while checking actual output.

## Optimizations and correctness

- Bound constant intrinsic arguments load the ABI registers directly, avoiding
  temporary and argument slots. Named arguments and compile-time conversions
  retain their C# meanings; invalid values retain observable traps.
- Constant local initializers write the destination's declared width directly.
- A promoted `byte` local/parameter compared with an `int` constant in 0..255
  using `<` branches directly on `CP`. Wider integers and limits outside that
  range retain full-width comparisons.
- Printing a `byte` converted to `char` loads the registers directly while
  retaining the ASCII guard; discarded increment results need no copies.
- General numeric comparisons now use the operand width, not the one-byte bool
  result width. Numeric `!=` correctly accepts either unequal ordering.

The current compiler/tooling suite passes 52 tests. Native tests execute the exact
tutorial sources and cover conversion, comparison boundaries, wrapping
increments, ASCII traps, named arguments and the code-size budgets.

Both new disks were run in SimCoupe with 256 KB internal RAM and no external
RAM. The text and square were visually inspected, and key press/release returned
to BASIC. These checks use the CLI launch path; the established F5 configuration
is unchanged.

## Next lessons and optimization priorities

Runtime tree shaking is now enabled. With the same first two C# sources:

| Loaded region | Text | Screen writes |
|---|---:|---:|
| Compiler region | 129 bytes | 121 bytes |
| Selected runtime image bytes | 688 bytes | 601 bytes |
| Complete binary | 817 bytes | 722 bytes |
| Previous complete binary | 860 bytes | 857 bytes |

These results also omit unreferenced compiler trap bodies. Application-body
budgets remain 48/68 bytes. The runtime's reserved BASIC-variable buffer still
occupies 768 additional resident bytes even when not emitted at the image end.
The selected runtimes were assembled and checked in SimCoupe for text, screen
writes and key-driven return to BASIC. See `sam-runtime.md` for dependency rules.

The remaining opaque-sprite, transparency, animation, double-buffering, keyboard
and palette lessons through Blog 6 are now ported. Resident C# byte-array fields
and typed frame references replace hidden native assets. See
[tutorials-blog02-06.md](tutorials-blog02-06.md) for the complete mapping, local
extraction workflow, current size measurements and actual native validations.

Remaining efficiency work includes compiler helper-data elimination,
register-held loop state, safe range-based guard elimination and efficient
bulk-copy kernels. Optimize measured hot paths rather than silently reducing
32-bit C# arithmetic or removing checks without proof.
