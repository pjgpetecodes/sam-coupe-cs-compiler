; Negative assembler fixture: runtime state must not overlap the E000 stack.
; Expected result is an ASSERT failure, not a bootable disk.
                ORG     0x8000
                DUMP    1,0
                AUTOEXEC
                JP      sam_start
cs_main:
                RET
                DEFS    0xDE00-$
                INCLUDE "../../runtime/sam/hardware.asm"
