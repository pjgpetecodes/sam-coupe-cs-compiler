                ORG     0x8000
                DUMP    1,0
                AUTOEXEC
                JP      sam_start
cs_main:
                LD      HL,0x6000
                LD      A,0xFF
                CALL    sam_write_screen_byte
                RET
                INCLUDE "../../runtime/sam/hardware.asm"
