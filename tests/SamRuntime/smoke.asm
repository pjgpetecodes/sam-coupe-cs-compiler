                ORG     0x8000
                DUMP    1,0
                AUTOEXEC
                JP      sam_start
cs_main:
                LD      HL,smoke_text
                CALL    sam_print
                LD      HL,65
                CALL    sam_print_char
                CALL    sam_wait_for_key
                CALL    sam_clear_screen
                LD      HL,0
                LD      A,0xFF
                CALL    sam_write_screen_byte
                INC     HL
                CALL    sam_write_screen_byte
                LD      HL,128
                LD      A,0x44
                CALL    sam_write_screen_byte
                INC     HL
                CALL    sam_write_screen_byte
                LD      HL,256
                LD      A,0xFF
                CALL    sam_write_screen_byte
                INC     HL
                CALL    sam_write_screen_byte
                LD      HL,384
                LD      A,0x44
                CALL    sam_write_screen_byte
                INC     HL
                CALL    sam_write_screen_byte
                ; First and final valid offsets, then return BASIC on a fresh key.
                LD      HL,0x5FFF
                LD      A,0xFF
                CALL    sam_write_screen_byte
                CALL    sam_wait_for_key
                RET
smoke_text:
                DEFW    18
                DM      "RUNTIME "
                DEFB    0
                DM      " ASCII OK"
                INCLUDE "../../runtime/sam/hardware.asm"
