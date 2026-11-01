---
name: sam-target-runtime
description: Use when changing SAM startup, memory paging, interrupts, ROM calls or screen support.
---

# SAM support changes

1. Read `runtime\sam\hardware.asm` and its documented compiler ABI.
2. Verify ports, paging bits and ROM calling conventions using authoritative SAM
   references or a focused SimCoupe experiment; do not borrow PC assumptions.
3. Keep code/data/stack resident, DI while ROM is replaced, and ROM present for
   interrupt waits and ROM calls. Preserve BASIC's required exit state.
4. Check the final assembled memory budget, including nested calls and ROM stack.
5. Build a minimal changed-helper test with configured pyz80/SAMDOS/SAMdisk.
6. Run it in SimCoupe with 256 KB, inspect screen and debugger registers, then
   close only the emulator instance started for this check.
