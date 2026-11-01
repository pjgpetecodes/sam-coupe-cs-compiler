# Source debugging

## First release: F5 and manual source breakpoints

The F5 configuration invokes the native compiler and opens the built disk in
SimCoupe. The Python debug adapter runs only the host launch wrapper: it does
not execute C# on .NET or understand Z80 breakpoints. A red C# breakpoint in VS
Code is not connected to the target in this mode.

After assembly, `program.source-map.json` records executable C# source spans,
unique sequence-point labels, logical addresses and physical RAM pages/offsets.
It also records source and assembly SHA-256 identities. `program.map` is the
pyz80 symbol file loaded by SimCoupe.
Resolution uses `program.symbols`, the assembler's complete symbol export,
because its address map keeps only one name when several labels share an
address. The CLI parses only the flat numeric export; it does not deserialize
executable pickle objects.

Find the desired file/line in the source map and use the corresponding label or
hexadecimal address with `bpx` in SimCoupe's debugger command mode. Set the
breakpoint after the program has loaded; during ROM/SAMDOS boot, the same logical
address might refer to another physical page. The resident program uses pages
1 and 2 at `0x8000` and `0xC000`. Inspect HMPR/current paging in the debugger.
Several sequence points can belong to one C# line; set all relevant locations.
Lines without executable sequence points cannot host a meaningful breakpoint.

Rebuild the disk and map together after edits. Do not reuse addresses from a
previous build. The build ID and source hashes allow tools to detect stale maps.
F5's launch stage verifies sources, assembly, disk and runtime hashes after its
build task, rejecting modified or incomplete artifacts.
No optimizing register allocator is used in the initial compiler.

A native check resolved Square's C# line 14 to `81ED` on physical RAM page 1
and hit that breakpoint in SimCoupe after returning to BASIC and restarting
with `CALL 32768`. This address is an example from that build, not a permanent
API: always consult the current source map.

## Phase 2: integrated VS Code debugging

Integrated source breakpoints need both compiler sequence-point metadata and a
bridge controlling SimCoupe pause/resume, memory and breakpoints. The bundled
manual documents native debugger commands, but does not establish a remote
debugging protocol. Investigate upstream facilities first. If a maintained fork
or instrumentation is required, obtain approval before modifying the emulator.

The initial investigation examined upstream SimCoupe commit
`1f966036543991c05022ce4f95cfbfbb0014b187`. `Base\Debug.h` exposes in-process
`Debug::Start`, `Debug::Stop`, `Debug::IsActive` and `Debugger::Execute` APIs;
`Base\Debug.cpp` invokes native breakpoint and GUI controls. These inspected
files do not expose a remote transport. This is not proof that every upstream
component lacks one, but there is no established bridge to connect VS Code to
the bundled executable. The debugger source carries GPL-2.0-or-later terms.

References:

- https://github.com/simonowen/simcoupe/blob/1f966036543991c05022ce4f95cfbfbb0014b187/Base/Debug.h
- https://github.com/simonowen/simcoupe/blob/1f966036543991c05022ce4f95cfbfbb0014b187/Base/Debug.cpp
- https://github.com/simonowen/simcoupe/blob/1f966036543991c05022ce4f95cfbfbb0014b187/License.txt

The bundled manual's `-mainmemory` option name is stale for the inspected
executable: the working name is `-mainmem`. The CLI uses a positional disk
argument to trigger autoboot and passes `-mainmem 256`; setting only `-disk1`
did not boot the disk in the observed version.

A future Debug Adapter Protocol extension will resolve verified C# breakpoints
to native locations, report stops and provide continue/pause/disconnect. Source
stepping and accurate stack/locals depend on ABI-aware metadata and are later
extensions. Do not approximate these with brittle UI automation.
