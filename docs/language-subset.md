# Supported C# subset

The compiler translates Roslyn-bound C# into standalone pyz80 assembly. It
does not execute C# on the SAM Coupé and does not provide a CLR or managed
runtime. Compile an explicit source set with `CSharpCompiler.Compile`; the
assembly references the declarations in `SamCoupe.Sdk`, whose methods are
compiler-recognized intrinsics rather than target-side .NET code.

```csharp
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        Sam.ClearScreen();
        for (ushort offset = 0; offset < 8; offset++)
            Sam.WriteScreenByte(offset, 0x11);
        Sam.Rom.Print("SAM Coupe");
    }
}
```

Unsupported constructs produce source-located diagnostics and no assembly. They
are never approximated.

## Program and methods

- One or more source files may declare static classes and static methods.
- Exactly one `static void Main()` with no parameters is required.
- Methods may have supported value parameters and supported return values.
- Calls to source methods are allowed when the reachable call graph is acyclic.
  Recursive and mutually recursive calls are errors.
- Locals and parameters are held in statically allocated method slots. There is
  no heap, instance state, or dynamic allocation. Supported mutable static
  scalar fields provide program state across calls.
- Supported statements are blocks, local declarations, expression statements,
  `if`/`else`, `while`, `for`, `break`, `continue`, and `return`.
- `unchecked` execution is used. Explicit `checked` blocks/expressions, constant
  overflow errors from Roslyn, and unsupported conversions remain diagnostics.

## Types and numeric behavior

Supported types are `bool`, `byte`, `sbyte`, `short`, `ushort`, `char`, `int`,
`uint`, `string`, `byte[]`/`ushort[]` references, `SpriteFrame`, and `void` for method returns. Values use 1-byte bool/byte/
sbyte, 2-byte short/ushort/char/string references, and 4-byte int/uint storage.
`char` is a 16-bit C# value; only ASCII characters may be sent to the text
intrinsics.

Roslyn supplies the bound operand types and implicit conversions. C# integral
promotions are retained: arithmetic involving byte, sbyte, short, ushort or
char normally evaluates as 32-bit `int`; `int` and `uint` arithmetic is
32-bit and wraps in unchecked contexts. `long`/`ulong` (including implicit
64-bit promotions), floating point, decimal, enums, pointers and nullable
value types are rejected.

Supported operations include unary `+`, `-`, `!`, `~`; binary `+`, `-`, `*`,
`/`, `%`, `&`, `|`, `^`, `<<`, `>>`, equality and relational comparisons;
simple and supported compound assignment; prefix/postfix increment and
decrement; explicit casts; and short-circuit `&&` / `||`. Shift counts are
masked according to C# 32-bit rules. Signed division by zero traps with fault
03; `int.MinValue / -1` traps with fault 04. Remainder follows C#'s dividend
sign rule. Conditions must have type `bool`.

## Strings

Strings are immutable, length-prefixed ASCII data interned in the resident
image. The prefix is a 16-bit little-endian byte length; data is not NUL
terminated, so embedded U+0000 is preserved. `string` references are 16-bit
resident addresses and `null` is represented by address zero.

Supported string features are literals, null, locals/parameters, constant
literal concatenation, `Length`, indexing, content equality/inequality, and
printing. Equality compares contents and length (including null handling), not
reference identity. Runtime concatenation, non-ASCII literals, other string
members, and allocation are errors. Null print/length/index operations
trap with fault 01; out-of-range or negative indexing traps with fault 02.

The C# `Sam.Rom.Print` and `Sam.Rom.PrintChar` aliases use the verified
ROM-backed text bridge. `Sam.Print` and `Sam.PrintChar` remain available and
use the same safe runtime path. Character values outside ASCII trap with fault
06 instead of truncating.

## SAM APIs

### Resident sprite data

Declare data in C#, then pass it through typed parameters:

```csharp
static readonly byte[] Frames = new byte[] { 1, 0x00, 0xFF };

static void Draw(byte[] data)
{
    SpriteFrame frame = Sam.SelectSpriteFrame(data, 0);
    Sam.DrawMaskedSprite(frame, 0, 1, 1);
}
```

Only non-empty `static readonly byte[]` and `ushort[]` fields with compile-time
in-range constants are supported (`new T[] { ... }`, inferred arrays, or
collection initializers). Their references are fixed, but elements can be
mutated; indexing is null- and bounds-checked (faults 01 and 08). `ushort[]`
elements are little-endian. Arrays have a 16-bit element-count prefix and no
heap allocation. A `[BankedData(page)]` field is emitted into that physical
page's 32 KB pair and must be mapped before it is read; mapped data reserves
address zero, leaving 32766 bytes per pair. Reachable methods and data fields
only are emitted. Uncompressed frame data starts with a byte count followed by
interleaved mask/pixel bytes. The local extraction workflow is documented in
[tutorials-blog02-06.md](tutorials-blog02-06.md).

The SDK declares only compiler intrinsics:

| API | Target behavior |
|---|---|
| `Sam.ClearScreen()` | Clear the MODE 4 screen through the resident runtime. |
| `Sam.WriteScreenByte(ushort offset, byte value)` | Write a packed screen byte; runtime rejects offsets `>= 0x6000` (fault 05). |
| `Sam.Print(string)` / `Sam.PrintChar(char)` | Print length-counted ASCII / one ASCII character. |
| `Sam.WaitForKey()` | Wait for a fresh physical press and release. |
| `Sam.DrawOpaqueSprite(byte[] data, ushort offset)` | Copy the supplied 16×32 opaque sprite. |
| `Sam.BuildInterleavedSprite(byte[] pixels, byte[] masks)` / `Sam.DrawBuiltMaskedSprite(ushort offset, byte widthBytes, byte height)` | Build and draw the resident 16×32 mask/pixel buffer from supplied planes. |
| `Sam.SelectSpriteFrame(byte[] data, byte index)` | Return a typed data reference plus frame index, without allocation. |
| `Sam.DrawMaskedSprite(SpriteFrame frame, ushort offset, byte widthBytes, byte height)` | Draw supplied interleaved data, retaining background through its mask. |
| `Sam.DrawRleMaskedSprite(SpriteFrame frame, ushort offset, byte widthBytes, byte height)` | Decode supplied mask/pixel packets and draw a frame. |
| `Sam.ClearSprite(ushort offset, byte widthBytes, byte height)` | Clear only the selected drawing-page rectangle (at most 16 bytes × 32 rows). |
| `Sam.FillScreen(byte value)` / `Sam.FillStripedBackground(byte first, byte second)` | Fill the current drawing page with packed MODE 4 bytes or alternating rows. |
| `Sam.SetPaletteEntry(byte index, byte value)` / `Sam.LoadTutorialPalette(byte[] palette)` | Update the retained CLUT and both ROM interrupt palette phases. Colours are seven-bit SAM hardware values. |
| `Sam.WriteSaaRegister(byte register, byte value)` | Select one SAA1099 register at full port `0x01FF`, then write its value at `0x00FF` using typed `OUT (C),A` code; not a raw-port escape hatch. |
| `Sam.WaitForFrames(byte count)` | Wait on ROM frame interrupts with ROM0 mapped, then reapply the current palette; zero returns immediately. |
| `Sam.IsKeyPressed(ushort port, byte mask)` | Return whether any selected active-low matrix bit is pressed. |
| `Sam.AnySolidPixelInPackedRow(byte[] mask, ushort offset, byte firstPixel, byte pixelCount)` | Test selected high/low MODE 4 nibbles in one packed mask row without inspecting neighboring pixels; caller maps banked data first. |
| `Sam.AnySolidPixelInPackedScreenColumn(byte[] mask, byte x, byte firstY, byte pixelCount)` | Test one pixel column in a mask of at least 24 KB, within the 192 screen rows; caller maps banked data first. Null/range failures trap with faults 01/14; zero count returns false. |
| `Sam.AnySolidPixelInPackedRectangle(byte[] mask, ushort offset, byte firstPixel, byte pixelWidth, byte rows)` | Repeat the checked packed-row probe at a 128-byte stride, returning true at the first solid pixel. `firstPixel` is 0 for the first high nibble or 1 for the low nibble; zero rows returns false. Caller maps banked data first. |
| `Sam.MapMemoryPage(byte page)` / `Sam.RestoreMemoryPage()` | Map a physical 32 KB page pair at the lower window with interrupts disabled, then restore the ROM mapping and saved interrupt state. |
| `Sam.CopyBytes(byte[] source, ushort sourceOffset, byte[] destination, ushort destinationOffset, ushort count)` | Copy a checked payload range between visible arrays; the caller maps a banked source and restores it before ROM/interrupt work. |
| `Sam.DecodeRleBackground(byte[] source, byte sourcePage, byte[] destination, byte destinationPage)` | Decode checked count/value and literal packets into an exactly 24 KB MODE 4 background, mapping each bank separately. |
| `Sam.ExpandByteRuns(byte[] source, byte sourcePage, byte[] destination, byte destinationPage)` | Expand little-endian 16-bit count/value records into an exactly 24 KB collision plane; a zero count must terminate after the full plane. |
| `Sam.CopyBankedBytesToScreen(byte[] source, byte sourcePage, ushort sourceOffset, ushort screenOffset, byte widthBytes, byte rowCount)` | Validate the complete source/destination rectangle, then copy MODE 4 rows to the hidden display page through a resident staging row. Keep interrupts disabled across the copy and restore the ROM/code mapping and entry interrupt state on return; zero width/rows does no copying. |
| `Sam.ShiftMaskedSpriteOnePixel(byte[] source, byte[] destination)` | Transform one 13×22 interleaved sprite into a 14-byte-per-row, one-pixel-shifted frame; validates distinct resident buffers. |
| `Sam.BeginDoubleBuffer()` / `Sam.BeginDoubleBuffer(byte firstScreenPage, byte secondScreenPage)` / `Sam.FlipScreen()` | Select the hidden page for drawing, then display it and update the ROM screen descriptor. The no-argument overload uses pages 4/6. |
| `Sam.Rom.ClearScreen()` | Use the verified ROM clear-screen routine. |
| `Sam.Rom.WaitForKey()` | Use the verified ROM key routine with physical release tracking. |
| `Sam.Rom.Print(string)` / `Sam.Rom.PrintChar(char)` | Aliases for the safe ROM-backed text bridge. |

Methods resolve through Roslyn symbols for the SDK types. There is no arbitrary
ROM-call API. See [target-abi.md](target-abi.md) and
[sam-runtime.md](sam-runtime.md) for machine and runtime contracts.

## Diagnostics and source maps

Roslyn parse/binding errors are preserved. Compiler diagnostics use stable
`SAM` codes, the original source path and 1-based line/column. A failed
`CompileResult` has `Success == false` and empty `AssemblyText`.

Successful assembly includes executable sequence-point labels, not markers
for braces or empty statements. `CompileResult.SequencePoints` (also exposed
as `SourceMap`) provides each label's source span and containing method. The
CLI resolves those labels against pyz80's exported symbols.

## Execution tests

`tests/SamCoupe.Compiler.Tests` assembles emitted programs with pyz80 and runs
their instructions on the test-only MIT-licensed `z80` Python package. This
checks observable arithmetic, comparisons, loops, calls, short-circuiting,
string output and string indexing; it is not a SAM hardware emulator.

```powershell
python -m pip install -r tests\SamCoupe.Compiler.Tests\requirements-z80.txt
dotnet test tests\SamCoupe.Compiler.Tests\SamCoupe.Compiler.Tests.csproj
```

The tests also require Python and the installed `pyz80` executable. Actual
paging, ROM, display, keyboard, disk boot and return-to-BASIC behavior must be
validated with the project runtime and SimCoupe separately.
