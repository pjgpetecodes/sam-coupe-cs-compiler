---
name: csharp-z80-compiler
description: Use when implementing or testing C# subset validation, Roslyn lowering, ABI or native Z80 emission.
---

# Compiler changes

1. Read the supported subset and ABI; locate the affected Roslyn operation.
2. Verify host C# semantics for types, promotions, flags, overflow and evaluation.
3. Modify validation and emission together; unknown operations remain errors.
4. Preserve executable sequence-point labels and source identities.
5. Add a focused failing regression case and run the relevant .NET tests.
6. Assemble generated output; execute arithmetic/control-flow tests on a Z80
   engine and hardware-dependent examples in SimCoupe.
7. Report exact implemented semantics and any checks that remain unavailable.
