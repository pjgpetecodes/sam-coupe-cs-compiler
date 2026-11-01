; ROM wrappers with caller IY unlike BASIC and runtime forced above C000.
                ORG     0x8000
                DUMP    1,0
                AUTOEXEC
                JP      sam_start
cs_main:
                LD      HL,before_text
                CALL    sam_print
                CALL    set_registers
                CALL    sam_rom_wait_for_key
                CALL    check_registers
                CALL    set_registers
                CALL    sam_rom_clear_screen
                CALL    check_registers
                LD      HL,after_text
                CALL    sam_print
                ; Back-to-back waits must not exit on the held first key.
                CALL    set_registers
                CALL    sam_rom_wait_for_key
                CALL    check_registers
                RET
set_registers:
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
                LD      HL,0x9ABC
                LD      IX,0xABCD
                LD      IY,0xDEF0
                LD      A,0xA5
                RET
check_registers:
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
                RET
check_failed:
                LD      A,0x89
                JP      sam_trap
expected:
                DEFW    0x1234,0x5678,0x9ABC,0xABCD,0xDEF0,0x00A5
                DEFW    0x2233,0x4455,0x6677,0x0011
actual:         DEFS    20
before_text:
                DEFW    13
                DM      "ROM WAIT TEST"
after_text:
                DEFW    21
                DM      "ROM CLEAR/WAIT ABI OK"
                DEFB    13
                DEFS    0xC100-$
                INCLUDE "../../runtime/sam/hardware.asm"
