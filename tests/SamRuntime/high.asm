; Runtime in bank D: ROM1 paging must restore code AND bridge the FF00 stack.
                ORG     0x8000
                DUMP    1,0
                AUTOEXEC
                JP      sam_start
cs_main:
                LD      HL,high_text
                CALL    sam_print
                LD      HL,0x5FFF
                LD      A,0xFF
                CALL    sam_write_screen_byte
                CALL    sam_wait_for_key
high_after_key:
                RET
high_text:
                DEFW    15
                DM      "HIGH RUNTIME OK"
                DEFS    0xC100-$
                INCLUDE "../../runtime/sam/hardware.asm"
