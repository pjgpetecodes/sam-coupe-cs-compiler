                ORG     0x8000
                DUMP    1,0
                AUTOEXEC
                JP      sam_start
cs_main:
                LD      HL,0x1100
                PUSH    HL
                POP     AF
                EX      AF,AF'
                EXX
                LD      BC,0x2233
                LD      DE,0x4455
                LD      HL,0x6677
                EXX
                LD      BC,0x1234
                LD      DE,0x5678
                LD      HL,65
                LD      IX,0x9ABC
                LD      IY,0xDEF0
                LD      A,0xA5
                CALL    sam_print_char
                LD      (actual),BC
                LD      (actual+2),DE
                LD      (actual+4),HL
                LD      (actual+6),IX
                LD      (actual+8),IY
                LD      (actual+10),A
                EXX
                LD      (actual+12),BC
                LD      (actual+14),DE
                LD      (actual+16),HL
                EXX
                EX      AF,AF'
                LD      (actual+18),A
                EX      AF,AF'
                LD      HL,actual
                LD      DE,expected
                LD      B,20
check_loop:
                LD      A,(DE)
                CP      (HL)
                JR      NZ,check_failed
                INC     HL
                INC     DE
                DJNZ    check_loop
                LD      HL,check_text
                CALL    sam_print
                CALL    sam_wait_for_key
                RET
check_failed:
                LD      A,0x88
                JP      sam_trap
expected:
                DEFW    0x1234,0x5678,65,0x9ABC,0xDEF0,0x00A5
                DEFW    0x2233,0x4455,0x6677,0x0011
actual:         DEFS    20
check_text:
                DEFW    17
                DM      " REGISTER ABI OK"
                DEFB    13
                INCLUDE "../../runtime/sam/hardware.asm"
