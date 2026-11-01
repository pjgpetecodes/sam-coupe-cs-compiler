# SAM Coupe C# compiler

A host-side Roslyn compiler for a restricted C# subset, producing native Z80
assembly for a 256 KB SAM Coupe. It does not run .NET on the SAM.

## Write C# and press F5

Open this repository folder in VS Code, then open
`samples\Square\Program.cs` or `samples\Text\Program.cs`. Press **F5** and select
**SAM C#: Compile active program and run in SimCoupe** if prompted.

The launch compiles all C# files in the active file's folder (including child
folders, excluding generated directories), assembles the native program with
pyz80, packages a bootable disk and starts SimCoupe. There must be exactly one
`static void Main()` in the program. Use separate folders for separate programs.
The pre-launch build task publishes diagnostics to VS Code's Problems panel;
the launch validates source/artifact hashes rather than compiling a second time.
The sample `.csproj` files supply editing/type-checking references only; the SAM
compiler selects C# sources directly rather than running these projects on .NET.

Prerequisites:

- .NET SDK specified by `global.json`.
- Python and the VS Code Python/Python Debugger extensions; the C# extension is
  recommended for editing.
- Installed `pyz80` on PATH and local SAMdisk, SAMDOS and SimCoupe files.
- Copy `tools.example.json` to ignored `tools.local.json` and configure paths.
  These third-party files are not redistributed by this repository.

Build the compiler with `dotnet build` and run tests with `dotnet test`.
Native execution tests also require the Python `z80` package; the exercised
version is 1.2.0 (`python -m pip install z80==1.2.0`).
**Ctrl+Shift+B** builds the active program without starting SimCoupe.

The equivalent commands are:

```powershell
python .\.vscode\sam_run.py .\samples\Square\Program.cs --run
python .\.vscode\sam_run.py .\samples\Text\Program.cs --build
python .\.vscode\sam_run.py .\tutorials\Blog02\01-PrintingText\Program.cs --source-comments
```

Generated program files go into `out` inside the selected program folder.
Pass `--source-comments` to include source file, line, and method comments in
the generated program assembly, and source line comments for retained runtime
code in `sam-runtime.asm`; comments are disabled by default.
Compilation failures prevent launch. Close the emulator started by F5 before
rebuilding that program; its output lock prevents accidentally replacing a disk
still in use. The wrapper remains running until that emulator exits.

## Language and debugging

See `docs\language-subset.md`, `docs\target-abi.md` and `docs\sam-runtime.md`
for supported C#, native representation and hardware contracts.

`Sam` supplies typed SAM helpers. `Sam.Rom` exposes verified ROM-backed routines,
such as character/string printing, clearing and keyboard waiting, through safe
paging/stack wrappers rather than arbitrary raw ROM addresses. The Text sample
demonstrates these routines; only documented bindings are supported.

F5 initially provides build-and-run, **not C# stepping in VS Code**. Generated
source maps support manual source-line breakpoints in SimCoupe; see
`docs\debugging.md`. Integrated C# source debugging is a separate phase requiring
a verified emulator-control bridge.

The compiler/tooling suite includes assembled Z80 execution tests.
Both samples were built and run through VS Code F5 in SimCoupe with 256 KB RAM
and no external memory. Native checks also verified keyboard/BASIC return and a
source-mapped breakpoint; unsupported C# prevents F5 launch.

## Tutorial progression

All 46 standalone executable tutorial entry points through Blog 28 are under
`tutorials\Blog02` through `tutorials\Blog28`. Blogs 7–9 add banked sprite data,
byte-step movement and pixel positioning; Blogs 10–27 incrementally build the
room-based graphics game, and Blog 28 adds three sound-only programs.
Open any program folder's `Program.cs` and press F5.
All required sprite, scenery, collision, room, item, actor, and sound lesson
data is included with permission; tutorial payloads are not ignored. Configure
`referenceRepo` in ignored `tools.local.json` only for regeneration or
read-only source comparison. Sprite bytes are defined in C# and passed to
typed drawing routines, not hidden inside runtime assets or selected by
opaque IDs.
See [tutorials-blog02-06.md](docs/tutorials-blog02-06.md) for the Blog 2–6
mapping, asset workflow, validation and size comparisons.
See [tutorials-blog07-28.md](docs/tutorials-blog07-28.md) for the complete
Blog 7–28 mapping, progression, included-data policy, and full validation
commands.
See [tutorial-progression.md](docs/tutorial-progression.md) for source mappings,
the first optimization's historical comparisons and instruction costs.
Application bodies are currently 48 and 68 bytes; the shared runtime is measured
separately, and the handwritten originals remain smaller.

## Copilot development guidance

`.github\copilot-instructions.md` supplies repository-specific GitHub Copilot
instructions. `.github\agents` contains compiler, SAM runtime and tooling
specialists; `.github\skills` contains their focused implementation workflows.
These files guide development and are not compiled into the SAM disk.

The tutorial repository is an external reference. Tutorial sprite data is
included with permission; tutorial disks, ROMs and emulator executables are not
bundled. Review component-specific
licenses before copying or redistributing external code or tools.
