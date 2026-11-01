# SAM Coupe C# compiler

This is a host-side Roslyn compiler producing native SAM Coupe Z80 assembly.
It is not a CLR implementation. Keep unsupported C# features explicit errors;
never silently narrow C# `int` to 16 bits or replace target execution with host
.NET execution.

## Structure and workflow

- `src\SamCoupe.Compiler`: language validation, typed lowering and Z80 emission.
- `src\SamCoupe.Sdk`: declarations for recognized target intrinsics.
- `runtime\sam`: SAM hardware support, not a managed runtime.
- `src\SamCoupe.Compiler.Cli`: source discovery, artifacts and tool orchestration.
- `.vscode\sam_run.py`: F5 launches the CLI for the active C# program folder.
- `samples`: independently compiled source folders.
- Inspect git status before editing and preserve unrelated user changes.
- Run `dotnet build` and the smallest applicable `dotnet test` selector.
- Validate generated assembly with configured pyz80; hardware changes also need
  SimCoupe verification. Never report emulator checks that were not performed.
- Do not redistribute pyz80, SAMDOS, SAMdisk, SimCoupe or tutorial assets without
  checking their component-specific licenses. Tool paths belong in ignored
  `tools.local.json`, not committed machine-specific configuration.

## Compiler invariants

- Preserve Roslyn-bound types, promotions, evaluation order and short circuits.
- Reject recursion, heap allocation and operations outside the documented subset.
- Strings are allocation-free ASCII constants/references. Preserve 16-bit `char`
  and 32-bit `Length`/index expression types; invalid accesses fail visibly.
- Every new supported behavior needs a focused regression test. Assembly text
  snapshots alone do not establish target execution correctness.
- Convert tutorials in source order under independent `tutorials` folders.
  Compare assembled application, data and runtime bytes against the handwritten
  source separately, and measure hot-loop instruction costs. Preserve C#
  operand widths and observable guards; optimize only with a semantics proof.
- Lay sprite initializers out in named frame blocks and numbered rows matching
  their width. Keep mask/pixel pairing visible; compressed data must include
  decoded reference rows and labelled streams without duplicating resident data.
- All tutorial data has redistribution permission. Keep required sprite,
  scenery, collision, room, item and sound data eligible for version control;
  ignore build outputs and machine-local configuration, not tutorial inputs.
- Source maps resolve labels after assembly, carry build/source identity and
  physical page/address information, and never claim non-executable lines as
  verified breakpoints. Standard .NET debugging does not debug native Z80 code.

## Hardware invariants

- LMPR/HMPR/VMPR/CLUT ports are 250/251/252/248. Preserve unrelated paging bits.
- MODE 4 has a 128-byte row stride and a 24 KB (`0x6000`) screen.
- Keep program data and the reserved stack in resident high memory without overlap.
- Disable interrupts while low screen RAM replaces ROM; restore ROM before ROM
  calls or `HALT`. Respect ROM clobbers and the runtime's documented ABI.
- Startup/exit preserve BASIC's required stack/paging state. Do not assume an
  early tutorial's hard-coded restore values are universal.
- Build/run failures stop launch. Never kill unrelated emulator processes.

Use the compiler-specific agents in `.github\agents` and skills in
`.github\skills` for substantial matching tasks. They are development guidance,
not files compiled into a SAM disk.
