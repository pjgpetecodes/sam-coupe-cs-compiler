; Native SAM Coupe / 256 KB runtime, included AFTER compiler code and data.
; Entry: ORG 0x8000 / DUMP 1,0 / AUTOEXEC / JP sam_start.
; cs_main is supplied by the compiler and returns normally.
; Normal SAMDOS/BASIC entry is required (ROM0, system RAM in bank B,
; HMPR=page 1, no external RAM). Never CALL sam_start: it replaces SP.
;
sam_lmpr:               EQU 250
sam_hmpr:               EQU 251
sam_vmpr:               EQU 252
sam_clut:               EQU 248
sam_jmode:              EQU 0x015A       ; A = mode minus one
sam_jsetstrm:           EQU 0x0112       ; A = stream number
sam_stack_top:          EQU 0xFF00
sam_stack_floor:        EQU 0xE000
sam_fault_code:         EQU 0xFF00       ; Outside downward-growing stack
;
; Save both register sets on BASIC's own stack BEFORE replacing it.
; LD A,I captures IFF2 in P/V; DI does not change those flags.
sam_start:
                PUSH    AF
                LD      A,I
                DI
                JP      PO,sam_start_di
                LD      A,1
                JR      sam_start_iff
sam_start_di:
                XOR     A
sam_start_iff:
                LD      (sam_saved_iff),A
                PUSH    BC
                PUSH    DE
                PUSH    HL
                PUSH    IX
                PUSH    IY
                LD      (sam_basic_iy),IY
                EX      AF,AF'
                PUSH    AF
                EX      AF,AF'
                EXX
                PUSH    BC
                PUSH    DE
                PUSH    HL
                EXX
                LD      (sam_basic_sp),SP
                IN      A,(sam_lmpr)
                LD      (sam_saved_lmpr),A
                AND     0x9F           ; ROM0 on, ROM1 off; keep RAM-bank bits
                LD      (sam_rom_lmpr),A
                OUT     (sam_lmpr),A
                IN      A,(sam_hmpr)
                LD      (sam_saved_hmpr),A
                IN      A,(sam_vmpr)
                LD      (sam_saved_vmpr),A
                LD      HL,0x5A00      ; BASIC display/stream/interrupt variables
                LD      DE,sam_basic_vars
                LD      BC,0x0300
                LDIR
                LD      HL,0x55D8      ; ROM interrupt's two palette phases
                LD      DE,sam_basic_palette
                LD      BC,40
                LDIR
                ; The compiler reserves a pair not occupied by banked code/data.
                LD      A,cs_initial_screen_page
                LD      (sam_draw_page),A
                OR      0x60          ; Explicit MODE 4 on the reserved pair
                OUT     (sam_vmpr),A
                LD      (0x5A78),A     ; ROM's current screen descriptor
                LD      A,3
                CALL    sam_jmode      ; ROM1 paging requires BASIC's low stack!
                DI
                LD      A,2
                CALL    sam_jsetstrm   ; Screen output, never inherit printer stream
                DI
                LD      SP,sam_stack_top
                XOR     A
                LD      (sam_fault_code),A
                CALL    sam_palette
                CALL    sam_clear_screen
                CALL    cs_main
                JP      sam_exit
;
; Nonreturning exit. Unwind by restoring BASIC SP, not the compiler call stack.
; The original BASIC return address remains below the saved registers.
sam_exit:
                DI
                LD      A,(sam_rom_lmpr)
                OUT     (sam_lmpr),A
                LD      SP,(sam_basic_sp)
                LD      IY,(sam_basic_iy)
                LD      A,(sam_basic_vars+0x40)
                CALL    sam_jmode      ; Restore original ROM mode bookkeeping
                DI
                LD      HL,sam_basic_vars
                LD      DE,0x5A00
                LD      BC,0x0300
                LDIR
                LD      HL,sam_basic_palette
                LD      DE,0x55D8
                LD      BC,40
                LDIR
                LD      HL,0x55D8
                LD      BC,sam_clut
sam_exit_palette:
                LD      A,(HL)
                OUT     (C),A
                INC     HL
                INC     B
                LD      A,B
                CP      16
                JR      NZ,sam_exit_palette
                LD      A,(sam_saved_vmpr)
                OUT     (sam_vmpr),A
                LD      A,(sam_saved_hmpr)
                OUT     (sam_hmpr),A
                LD      A,(sam_saved_lmpr)
                OUT     (sam_lmpr),A
                EXX
                POP     HL
                POP     DE
                POP     BC
                EXX
                EX      AF,AF'
                POP     AF
                EX      AF,AF'
                POP     IY
                POP     IX
                POP     HL
                POP     DE
                POP     BC
                LD      A,(sam_saved_iff)
                OR      A
                JR      Z,sam_exit_di
                POP     AF
                EI                      ; RET executes before any pending interrupt
                RET
sam_exit_di:
                POP     AF
                RET
;
; Typed, no-argument ROM wrappers. These are not arbitrary CALL gateways.
; Preserve the caller's A before choosing the internal operation.
sam_rom_clear_screen:
                PUSH    AF
                XOR     A
                JR      sam_rom_dispatch
sam_rom_wait_for_key:
                PUSH    AF
                LD      A,1
sam_rom_dispatch:
                PUSH    BC
                PUSH    DE
                PUSH    HL
                PUSH    IX
                PUSH    IY
                EX      AF,AF'
                PUSH    AF
                EX      AF,AF'
                EXX
                PUSH    BC
                PUSH    DE
                PUSH    HL
                EXX
                LD      E,A
                DI
                LD      A,(sam_rom_lmpr)
                OUT     (sam_lmpr),A
                LD      (sam_runtime_sp),SP
                LD      SP,(sam_basic_sp)
                LD      IY,(sam_basic_iy)
                LD      A,E
                OR      A
                JR      NZ,sam_rom_key_initial
                XOR     A
                CALL    0x014E         ; JCLSBL: A=0 clears full ROM screen/cursor
                JR      sam_rom_done
;
; JREADKEY returns Z when no translated key. Frame interrupts run only with
; ROM0 mapped AND BASIC's low stack active; ROM1 can cover the resident stack.
; HALT also avoids busy polling. No character result is exposed to C#.
sam_rom_key_initial:
                EI
                HALT
                CALL    0x0169
                DI
                CALL    sam_key_state  ; Z from JREADKEY is NOT a physical release:
                                        ; translated buffer may be empty while held
                JR      NZ,sam_rom_key_initial
sam_rom_key_press:
                EI
                HALT
                CALL    0x0169
                JR      Z,sam_rom_key_press
sam_rom_key_release:
                EI
                HALT
                CALL    0x0169
                DI
                CALL    sam_key_state  ; Require actual matrix release, not buffer gap
                JR      NZ,sam_rom_key_release
sam_rom_done:
                DI
                LD      SP,(sam_runtime_sp)
                CALL    sam_restore_lower_mapping
                CALL    sam_palette    ; ROM frame interrupts may reset the CLUT
                EXX
                POP     HL
                POP     DE
                POP     BC
                EXX
                EX      AF,AF'
                POP     AF
                EX      AF,AF'
                POP     IY
                POP     IX
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
;
; All public helpers preserve primary/alternate registers and IX/IY.
; Flags are not part of the ABI. Helpers leave interrupts disabled.
sam_clear_screen:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                DI
                CALL    sam_map_draw_page
                LD      HL,0
                LD      DE,1
                LD      BC,0x5FFF
                LD      (HL),0         ; 256x192 MODE 4: 128 bytes/row, 24 KB
                LDIR
                CALL    sam_restore_lower_mapping
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
;
; HL = byte offset in [0,0x6000), A = two packed 4-bit pixels.
sam_write_screen_byte:
                PUSH    AF
                LD      A,H
                CP      0x60
                JR      NC,sam_screen_bounds
                POP     AF
                PUSH    AF
                PUSH    BC
                LD      B,A
                DI
                LD      A,(sam_saved_lmpr)
                AND     0x80
                OR      0x24
                OUT     (sam_lmpr),A
                LD      (HL),B
                CALL    sam_restore_lower_mapping
                POP     BC
                POP     AF
                RET
sam_screen_bounds:
                LD      A,5
                JP      sam_trap
;
; HL = screen offset, DE = C#-defined opaque sprite data.
sam_draw_opaque_sprite:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                DI
                PUSH    HL
                PUSH    DE
                EX      DE,HL
                LD      BC,512
                CALL    sam_validate_data
                POP     DE
                POP     HL
                LD      B,16
                LD      C,32
                CALL    sam_validate_sprite_rectangle
                CALL    sam_map_draw_page
                LD      C,32
sam_opaque_row:
                PUSH    BC
                LD      B,16
sam_opaque_byte:
                LD      A,(DE)
                LD      (HL),A
                INC     DE
                INC     HL
                DJNZ    sam_opaque_byte
                LD      BC,112
                ADD     HL,BC
                POP     BC
                DEC     C
                JR      NZ,sam_opaque_row
                CALL    sam_restore_lower_mapping
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
;
; HL = C#-defined pixels, DE = C#-defined masks; build an interleaved frame.
sam_build_interleaved_sprite:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                PUSH    IX
                DI
                LD      BC,512
                CALL    sam_validate_data
                EX      DE,HL
                CALL    sam_validate_data
                EX      DE,HL
                PUSH    DE
                POP     IX
                LD      DE,sam_interleave_buffer
                LD      BC,512
sam_interleave_loop:
                LD      A,(IX+0)
                LD      (DE),A
                INC     DE
                CPL
                AND     (HL)
                LD      (DE),A
                INC     DE
                INC     HL
                INC     IX
                DEC     BC
                LD      A,B
                OR      C
                JR      NZ,sam_interleave_loop
                POP     IX
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
;
; HL = screen offset, DE = C#-defined interleaved frames, A = frame,
; B = width in bytes, C = height. Each frame occupies width*height*2 bytes.
sam_draw_masked_sprite:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                PUSH    IX
                DI
                LD      (sam_sprite_screen_offset),HL
                LD      (sam_sprite_data_pointer),DE
                LD      (sam_sprite_frame_index),A
                LD      A,B
                LD      (sam_sprite_width),A
                LD      A,C
                LD      (sam_sprite_height),A
                LD      A,B
                OR      A
                JP      Z,sam_sprite_bounds
                LD      A,C
                OR      A
                JP      Z,sam_sprite_bounds
                CALL    sam_validate_sprite_rectangle
                PUSH    BC
                LD      HL,(sam_sprite_data_pointer)
                LD      BC,1
                CALL    sam_validate_data
                POP     BC
                LD      E,(HL)          ; First byte is the C# frame count
                LD      A,(sam_sprite_frame_index)
                CP      E
                JP      NC,sam_sprite_bounds
                LD      A,128
                SUB     B
                LD      (sam_sprite_stride),A
                CALL    sam_map_draw_page
                ; Compute frame size (width * height * two interleaved planes).
                LD      A,(sam_sprite_width)
                LD      E,A
                LD      D,0
                LD      A,(sam_sprite_height)
                LD      B,A
                LD      HL,0
sam_frame_size_loop:
                ADD     HL,DE
                DJNZ    sam_frame_size_loop
                ADD     HL,HL
                LD      (sam_sprite_frame_size),HL
                ; Add frame_size * frame_index to the C# array base address.
                LD      A,(sam_sprite_frame_index)
                LD      B,A
                LD      DE,(sam_sprite_frame_size)
                LD      HL,0
sam_frame_offset_loop:
                LD      A,B
                OR      A
                JR      Z,sam_frame_offset_ready
                ADD     HL,DE
                DEC     B
                JR      sam_frame_offset_loop
sam_frame_offset_ready:
                PUSH    HL
                ADD     HL,DE
                INC     HL
                LD      B,H
                LD      C,L
                LD      HL,(sam_sprite_data_pointer)
                CALL    sam_validate_data
                POP     HL
                LD      DE,(sam_sprite_data_pointer)
                INC     DE              ; Skip the frame-count byte
                ADD     HL,DE
                EX      DE,HL
                LD      HL,(sam_sprite_screen_offset)
                CALL    sam_draw_masked_data
                CALL    sam_restore_lower_mapping
                POP     IX
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
sam_sprite_bounds:
                LD      A,7
                JP      sam_trap
;
; Draw the resident frame built from separate C# pixel and mask arrays.
; HL = screen offset, B = width, C = height.
sam_draw_built_masked_sprite:
                PUSH    AF
                PUSH    DE
                LD      DE,sam_interleave_header
                XOR     A
                CALL    sam_draw_masked_sprite
                POP     DE
                POP     AF
                RET
;
; Decode two count/value RLE planes from a C#-defined array and draw.
; Each frame has little-endian mask/pixel stream offsets relative to the array.
; Packets repeat 1..127 values, or copy 1..127 literals (high bit set); zero ends.
sam_draw_rle_masked_sprite:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                PUSH    IX
                DI
                LD      (sam_sprite_screen_offset),HL
                LD      (sam_sprite_data_pointer),DE
                LD      (sam_sprite_frame_index),A
                LD      A,B
                LD      (sam_sprite_width),A
                LD      A,C
                LD      (sam_sprite_height),A
                LD      A,B
                OR      A
                JP      Z,sam_sprite_bounds
                LD      A,C
                OR      A
                JP      Z,sam_sprite_bounds
                CALL    sam_validate_sprite_rectangle
                PUSH    BC
                LD      HL,(sam_sprite_data_pointer)
                LD      BC,1
                CALL    sam_validate_data
                POP     BC
                LD      E,(HL)
                LD      A,(sam_sprite_frame_index)
                CP      E
                JP      NC,sam_sprite_bounds
                LD      A,128
                SUB     B
                LD      (sam_sprite_stride),A
                ; The per-frame directory contains two 16-bit offsets.
                LD      A,(sam_sprite_frame_index)
                LD      L,A
                LD      H,0
                ADD     HL,HL
                ADD     HL,HL
                EX      DE,HL
                LD      HL,(sam_sprite_data_pointer)
                PUSH    DE
                EX      DE,HL
                LD      BC,5
                ADD     HL,BC
                LD      B,H
                LD      C,L
                LD      HL,(sam_sprite_data_pointer)
                CALL    sam_validate_data
                POP     DE
                PUSH    HL
                LD      HL,(sam_data_end)
                LD      (sam_rle_end),HL
                POP     HL
                INC     HL              ; Skip the frame-count byte
                ADD     HL,DE
                LD      (sam_sprite_frame_table_pointer),HL
                LD      E,(HL)
                INC     HL
                LD      D,(HL)
                LD      HL,(sam_sprite_data_pointer)
                ADD     HL,DE
                JP      C,sam_sprite_bounds
                LD      IX,sam_interleave_buffer
                CALL    sam_rle_reset
                CALL    sam_unpack_rle_plane
                LD      HL,(sam_sprite_frame_table_pointer)
                INC     HL
                INC     HL
                LD      E,(HL)
                INC     HL
                LD      D,(HL)
                LD      HL,(sam_sprite_data_pointer)
                ADD     HL,DE
                JP      C,sam_sprite_bounds
                LD      IX,sam_interleave_buffer+1
                CALL    sam_rle_reset
                CALL    sam_unpack_rle_plane
                CALL    sam_map_draw_page
                LD      HL,(sam_sprite_screen_offset)
                LD      DE,sam_interleave_buffer
                CALL    sam_draw_masked_data
                CALL    sam_restore_lower_mapping
                POP     IX
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
;
; HL = count/value byte stream, IX = interleaved destination plane.
sam_unpack_rle_plane:
                CALL    sam_rle_read
                OR      A
                JR      NZ,sam_rle_packet
                LD      DE,(sam_rle_remaining)
                LD      A,D
                OR      E
                JP      NZ,sam_sprite_bounds
                RET
sam_rle_packet:
                JP      M,sam_unpack_rle_literals
                LD      C,A
                CALL    sam_rle_read
sam_unpack_rle_run:
                CALL    sam_rle_consume
                LD      (IX+0),A
                INC     IX
                INC     IX
                DEC     C
                JR      NZ,sam_unpack_rle_run
                JR      sam_unpack_rle_plane
sam_unpack_rle_literals:
                AND     0x7F
                JP      Z,sam_sprite_bounds
                LD      C,A
sam_unpack_rle_literal:
                CALL    sam_rle_read
                CALL    sam_rle_consume
                LD      (IX+0),A
                INC     IX
                INC     IX
                DEC     C
                JR      NZ,sam_unpack_rle_literal
                JR      sam_unpack_rle_plane
sam_rle_read:
                PUSH    DE
                LD      DE,(sam_sprite_data_pointer)
                OR      A
                SBC     HL,DE
                JP      C,sam_sprite_bounds
                ADD     HL,DE
                LD      DE,(sam_rle_end)
                OR      A
                SBC     HL,DE
                ADD     HL,DE
                POP     DE
                JP      NC,sam_sprite_bounds
                LD      A,(HL)
                INC     HL
                RET
sam_rle_consume:
                PUSH    AF
                PUSH    DE
                LD      DE,(sam_rle_remaining)
                LD      A,D
                OR      E
                JP      Z,sam_sprite_bounds
                DEC     DE
                LD      (sam_rle_remaining),DE
                POP     DE
                POP     AF
                RET
sam_rle_reset:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                LD      A,(sam_sprite_width)
                LD      E,A
                LD      D,0
                LD      A,(sam_sprite_height)
                LD      B,A
                LD      HL,0
sam_rle_size:
                ADD     HL,DE
                DJNZ    sam_rle_size
                LD      (sam_rle_remaining),HL
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
;
; HL = resident C# array payload, BC = minimum byte count. The compiler emits
; a uint16 length immediately before the payload; no heap or runtime allocation.
sam_validate_data:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                LD      DE,0x8002
                OR      A
                SBC     HL,DE
                JP      C,sam_sprite_bounds
                ADD     HL,DE
                LD      DE,0xE000
                OR      A
                SBC     HL,DE
                JP      NC,sam_sprite_bounds
                ADD     HL,DE
                DEC     HL
                LD      D,(HL)
                DEC     HL
                LD      E,(HL)
                INC     HL
                INC     HL
                PUSH    HL
                LD      H,D
                LD      L,E
                OR      A
                SBC     HL,BC
                JP      C,sam_sprite_bounds
                POP     HL
                ADD     HL,DE
                JP      C,sam_sprite_bounds
                LD      (sam_data_end),HL
                LD      DE,0xE000
                OR      A
                SBC     HL,DE
                JP      NC,sam_sprite_bounds
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
;
; Only the tutorial's at-most 16x32 byte rectangle fits the resident decoder.
sam_validate_sprite_rectangle:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                LD      A,B
                OR      A
                JP      Z,sam_sprite_bounds
                CP      17
                JP      NC,sam_sprite_bounds
                LD      A,C
                OR      A
                JP      Z,sam_sprite_bounds
                CP      33
                JP      NC,sam_sprite_bounds
                LD      A,L
                AND     127
                ADD     A,B
                CP      129
                JP      NC,sam_sprite_bounds
                LD      E,B
                LD      D,0
                ADD     HL,DE
                JP      C,sam_sprite_bounds
                LD      DE,128
                LD      B,C
                DEC     B
                JR      Z,sam_rectangle_end
sam_rectangle_row:
                ADD     HL,DE
                JP      C,sam_sprite_bounds
                DJNZ    sam_rectangle_row
sam_rectangle_end:
                LD      DE,0x6001
                OR      A
                SBC     HL,DE
                JP      NC,sam_sprite_bounds
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
;
; HL = C#-defined CLUT data; retain its values for interrupt-time restoration.
sam_load_tutorial_palette:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                PUSH    IX
                DI
                LD      BC,16
                CALL    sam_validate_data
                LD      (sam_palette_source),HL
                LD      HL,(sam_palette_source)
                LD      IX,sam_palette_data
                LD      BC,sam_clut
                LD      D,16
sam_tutorial_palette_loop:
                LD      A,(HL)
                LD      (IX+0),A
                INC     HL
                INC     IX
                INC     B
                DEC     D
                JR      NZ,sam_tutorial_palette_loop
                POP     IX
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                JP      sam_palette
;
; HL = screen byte offset, DE = interleaved frame.
sam_draw_masked_data:
sam_masked_row:
                LD      A,(sam_sprite_width)
                LD      B,A
sam_masked_byte:
                LD      A,(DE)
                INC     DE
                AND     (HL)
                LD      C,A
                LD      A,(DE)
                INC     DE
                OR      C
                LD      (HL),A
                INC     HL
                DJNZ    sam_masked_byte
                LD      A,(sam_sprite_stride)
                LD      C,A
                LD      B,0
                ADD     HL,BC
                LD      A,(sam_sprite_height)
                DEC     A
                LD      (sam_sprite_height),A
                JR      NZ,sam_masked_row
                RET
;
; Clear only the animation rectangle, as the original tutorials do.
sam_clear_sprite:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                DI
                CALL    sam_validate_sprite_rectangle
                LD      A,B
                LD      (sam_sprite_width),A
                CALL    sam_map_draw_page
                LD      A,128
                SUB     B
                LD      E,A
                LD      D,0
sam_clear_sprite_row:
                LD      A,(sam_sprite_width)
                LD      B,A
sam_clear_sprite_byte:
                LD      (HL),0
                INC     HL
                DJNZ    sam_clear_sprite_byte
                ADD     HL,DE
                DEC     C
                JR      NZ,sam_clear_sprite_row
                CALL    sam_restore_lower_mapping
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
;
; Fill the currently selected drawing page with one packed MODE 4 colour byte.
sam_fill_screen:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                LD      (sam_background_first),A
                DI
                CALL    sam_map_draw_page
                LD      HL,0
                LD      DE,1
                LD      BC,0x5FFF
                LD      A,(sam_background_first)
                LD      (HL),A
                LDIR
                CALL    sam_restore_lower_mapping
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
;
; Paint alternating full-width scanlines using two packed screen bytes.
sam_fill_striped_background:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                DI
                LD      (sam_background_first),A
                LD      A,B
                LD      (sam_background_second),A
                LD      HL,0
                CALL    sam_map_draw_page
                LD      A,(sam_background_first)
                LD      (sam_background_current),A
                LD      B,192
sam_background_row:
                LD      A,(sam_background_current)
                LD      C,A
                LD      A,128
                LD      D,A
sam_background_byte:
                LD      A,C
                LD      (HL),A
                INC     HL
                DEC     D
                JR      NZ,sam_background_byte
                LD      A,(sam_background_current)
                LD      C,A
                LD      A,(sam_background_first)
                CP      C
                JR      NZ,sam_background_use_first
                LD      A,(sam_background_second)
                JR      sam_background_next
sam_background_use_first:
                LD      A,(sam_background_first)
sam_background_next:
                LD      (sam_background_current),A
                DJNZ    sam_background_row
                CALL    sam_restore_lower_mapping
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
;
; A = CLUT index, B = seven-bit colour value.
sam_set_palette_entry:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                DI
                AND     15
                LD      E,A
                LD      D,0
                LD      HL,sam_palette_data
                ADD     HL,DE
                LD      A,B
                LD      (HL),A
                CALL    sam_palette
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
;
; Wait on the ROM interrupt, then restore the currently selected palette.
sam_wait_frames:
                PUSH    AF
                PUSH    BC
                OR      A
                JR      Z,sam_wait_frames_done
                LD      B,A
                LD      A,(sam_rom_lmpr)
                OUT     (sam_lmpr),A
                EI
sam_wait_frame_loop:
                HALT
                DI
                CALL    sam_palette
                EI
                DJNZ    sam_wait_frame_loop
                DI
                CALL    sam_restore_lower_mapping
sam_wait_frames_done:
                POP     BC
                POP     AF
                RET
;
; BC = keyboard port, A = active-low key mask. Return A=1 if pressed.
sam_is_key_pressed:
                PUSH    BC
                PUSH    DE
                LD      E,A
                IN      A,(C)
                CPL
                AND     E
                JR      NZ,sam_key_is_pressed
                XOR     A
                POP     DE
                POP     BC
                RET
sam_key_is_pressed:
                LD      A,1
                POP     DE
                POP     BC
                RET
;
; Copy a checked non-overlapping byte range between C#-defined arrays.
; The caller maps any banked source before calling; both array headers stay visible.
sam_copy_bytes:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                PUSH    IX
                PUSH    IY
                EX      AF,AF'
                PUSH    AF
                EX      AF,AF'
                EXX
                PUSH    BC
                PUSH    DE
                PUSH    HL
                EXX
                LD      HL,(sam_copy_count)
                LD      A,H
                OR      L
                JP      Z,sam_copy_done
                LD      HL,(sam_copy_source)
                LD      A,H
                OR      L
                JP      Z,sam_copy_null
                LD      A,(HL)
                LD      (sam_copy_source_length),A
                INC     HL
                LD      A,(HL)
                LD      (sam_copy_source_length+1),A
                LD      HL,(sam_copy_source_offset)
                LD      DE,(sam_copy_count)
                ADD     HL,DE
                JP      C,sam_copy_source_bounds
                LD      DE,(sam_copy_source_length)
                LD      A,H
                CP      D
                JP      C,sam_copy_source_range_valid
                JP      NZ,sam_copy_source_bounds
                LD      A,L
                CP      E
                JP      C,sam_copy_source_range_valid
                JP      Z,sam_copy_source_range_valid
                JP      sam_copy_source_bounds
sam_copy_source_range_valid:
                LD      HL,(sam_copy_destination)
                LD      A,H
                OR      L
                JP      Z,sam_copy_null
                LD      A,(HL)
                LD      (sam_copy_destination_length),A
                INC     HL
                LD      A,(HL)
                LD      (sam_copy_destination_length+1),A
                LD      HL,(sam_copy_destination_offset)
                LD      DE,(sam_copy_count)
                ADD     HL,DE
                JP      C,sam_copy_bounds
                LD      DE,(sam_copy_destination_length)
                LD      A,H
                CP      D
                JP      C,sam_copy_destination_range_valid
                JP      NZ,sam_copy_bounds
                LD      A,L
                CP      E
                JP      C,sam_copy_destination_range_valid
                JP      Z,sam_copy_destination_range_valid
                JP      sam_copy_bounds
sam_copy_destination_range_valid:
                LD      HL,(sam_copy_source)
                INC     HL
                INC     HL
                LD      DE,(sam_copy_source_offset)
                ADD     HL,DE
                LD      (sam_copy_source_pointer),HL
                LD      HL,(sam_copy_destination)
                INC     HL
                INC     HL
                LD      DE,(sam_copy_destination_offset)
                ADD     HL,DE
                LD      (sam_copy_destination_pointer),HL
                LD      HL,(sam_copy_source_pointer)
                LD      DE,(sam_copy_destination_pointer)
                LD      BC,(sam_copy_count)
                LDIR
sam_copy_done:
                EXX
                POP     HL
                POP     DE
                POP     BC
                EXX
                EX      AF,AF'
                POP     AF
                EX      AF,AF'
                POP     IY
                POP     IX
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
sam_copy_null:
                LD      A,1
                JP      sam_trap
sam_copy_source_bounds:
sam_copy_bounds:
                LD      A,8
                JP      sam_trap
;
; Copy a checked resident byte range to the active hidden MODE 4 screen.
; The screen offset/count must stay within one 128-byte row.
sam_copy_bytes_to_screen:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                PUSH    IX
                PUSH    IY
                EX      AF,AF'
                PUSH    AF
                EX      AF,AF'
                EXX
                PUSH    BC
                PUSH    DE
                PUSH    HL
                EXX
                LD      A,I
                DI
                JP      PO,sam_screen_iff_off
                LD      A,1
                JR      sam_screen_iff_saved
sam_screen_iff_off:
                XOR     A
sam_screen_iff_saved:
                LD      (sam_screen_iff),A
                LD      HL,(sam_screen_count)
                LD      A,H
                OR      L
                JP      Z,sam_screen_copy_done
                LD      HL,(sam_screen_source)
                LD      A,H
                OR      L
                JP      Z,sam_screen_copy_null
                LD      A,(HL)
                LD      (sam_screen_source_length),A
                INC     HL
                LD      A,(HL)
                LD      (sam_screen_source_length+1),A
                LD      HL,(sam_screen_source_offset)
                LD      DE,(sam_screen_count)
                ADD     HL,DE
                JP      C,sam_screen_copy_bounds
                LD      DE,(sam_screen_source_length)
                LD      A,H
                CP      D
                JP      C,sam_screen_source_valid
                JP      NZ,sam_screen_copy_bounds
                LD      A,L
                CP      E
                JP      C,sam_screen_source_valid
                JP      Z,sam_screen_source_valid
                JP      sam_screen_copy_bounds
sam_screen_source_valid:
                LD      HL,(sam_screen_offset)
                LD      DE,(sam_screen_count)
                ADD     HL,DE
                JP      C,sam_screen_copy_bounds
                LD      A,H
                CP      0x60
                JP      C,sam_screen_destination_valid
                JP      NZ,sam_screen_copy_bounds
                LD      A,L
                OR      A
                JP      NZ,sam_screen_copy_bounds
sam_screen_destination_valid:
                LD      HL,(sam_screen_offset)
                LD      A,L
                AND     0x7F
                LD      E,A
                LD      HL,(sam_screen_count)
                LD      A,H
                OR      A
                JP      NZ,sam_screen_copy_bounds
                LD      A,L
                ADD     A,E
                JP      C,sam_screen_copy_bounds
                CP      129
                JP      NC,sam_screen_copy_bounds
                LD      HL,(sam_screen_source)
                INC     HL
                INC     HL
                LD      DE,(sam_screen_source_offset)
                ADD     HL,DE
                LD      (sam_screen_source_pointer),HL
                CALL    sam_map_draw_page
                LD      HL,(sam_screen_source_pointer)
                LD      DE,(sam_screen_offset)
                LD      BC,(sam_screen_count)
                LDIR
                CALL    sam_restore_lower_mapping
                JR      sam_screen_copy_done
sam_screen_copy_null:
                LD      A,1
                JP      sam_trap
sam_screen_copy_bounds:
                LD      A,13
                JP      sam_trap
sam_screen_copy_done:
                LD      A,(sam_screen_iff)
                OR      A
                JR      Z,sam_screen_interrupts_remain_off
                EI
sam_screen_interrupts_remain_off:
                EXX
                POP     HL
                POP     DE
                POP     BC
                EXX
                EX      AF,AF'
                POP     AF
                EX      AF,AF'
                POP     IY
                POP     IX
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
;
; Materialize compiler-owned high-page initializers after SAMDOS has returned.
; Page 3 contains uint16 count / byte value runs; zero count terminates.
sam_initialize_banked_runs:
                DI
                LD      A,(sam_initial_data_page)
                CALL    sam_map_memory_page
                LD      HL,(sam_initial_data_destination)
                LD      DE,(sam_initial_data_length)
                LD      (HL),E
                INC     HL
                LD      (HL),D
                INC     HL
                LD      (sam_initial_data_destination),HL
                CALL    sam_restore_memory_page
sam_initial_data_run:
                LD      A,3
                CALL    sam_map_memory_page
                LD      HL,(sam_initial_data_source)
                LD      E,(HL)
                INC     HL
                LD      D,(HL)
                INC     HL
                LD      A,(HL)
                INC     HL
                LD      (sam_initial_data_value),A
                LD      (sam_initial_data_source),HL
                CALL    sam_restore_memory_page
                LD      A,D
                OR      E
                JR      Z,sam_initial_data_done
                LD      HL,(sam_initial_data_remaining)
                OR      A
                SBC     HL,DE
                JP      C,sam_copy_bounds
                LD      (sam_initial_data_remaining),HL
                LD      B,D
                LD      C,E
                LD      A,(sam_initial_data_page)
                CALL    sam_map_memory_page
                LD      HL,(sam_initial_data_destination)
                LD      A,(sam_initial_data_value)
                LD      (HL),A
                DEC     BC
                LD      A,B
                OR      C
                JR      Z,sam_initial_data_advance
                LD      D,H
                LD      E,L
                INC     DE
                LDIR
sam_initial_data_advance:
                INC     HL
                LD      (sam_initial_data_destination),HL
                CALL    sam_restore_memory_page
                JR      sam_initial_data_run
sam_initial_data_done:
                LD      HL,(sam_initial_data_remaining)
                LD      A,H
                OR      L
                JP      NZ,sam_copy_bounds
                RET
;
; Shift a 13x22 interleaved C# sprite right by one MODE 4 pixel.
; The source is [1, 572 frame bytes]; destination is [1, 616 frame bytes].
sam_shift_masked_sprite_one_pixel:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                PUSH    IX
                PUSH    IY
                EX      AF,AF'
                PUSH    AF
                EX      AF,AF'
                EXX
                PUSH    BC
                PUSH    DE
                PUSH    HL
                EXX
                DI
                LD      HL,(sam_shift_source)
                LD      A,H
                OR      L
                JP      Z,sam_copy_null
                LD      DE,(sam_shift_destination)
                OR      A
                SBC     HL,DE
                JP      Z,sam_copy_bounds
                LD      HL,(sam_shift_source)
                INC     HL
                INC     HL
                LD      BC,573
                CALL    sam_validate_data
                LD      (sam_shift_source_pointer),HL
                LD      A,(HL)
                CP      1
                JP      NZ,sam_sprite_bounds
                LD      HL,(sam_shift_destination)
                LD      A,H
                OR      L
                JP      Z,sam_copy_null
                INC     HL
                INC     HL
                LD      BC,617
                CALL    sam_validate_data
                LD      (sam_shift_destination_pointer),HL
                LD      A,(HL)
                CP      1
                JP      NZ,sam_sprite_bounds
                LD      HL,(sam_shift_source_pointer)
                INC     HL
                LD      DE,(sam_shift_destination_pointer)
                INC     DE
                LD      B,22
sam_shift_row:
                LD      A,(HL)
                LD      (sam_shift_previous_mask),A
                INC     HL
                LD      A,(HL)
                LD      (sam_shift_previous_pixel),A
                INC     HL
                LD      A,(sam_shift_previous_mask)
                RRCA
                RRCA
                RRCA
                RRCA
                AND     0x0F
                OR      0xF0
                LD      (DE),A
                INC     DE
                LD      A,(sam_shift_previous_pixel)
                RRCA
                RRCA
                RRCA
                RRCA
                AND     0x0F
                LD      (DE),A
                INC     DE
                LD      C,12
sam_shift_pair:
                LD      A,(HL)
                LD      (sam_shift_current),A
                PUSH    BC
                LD      A,(sam_shift_previous_mask)
                RLCA
                RLCA
                RLCA
                RLCA
                AND     0xF0
                LD      C,A
                LD      A,(sam_shift_current)
                RRCA
                RRCA
                RRCA
                RRCA
                AND     0x0F
                OR      C
                LD      (DE),A
                INC     DE
                POP     BC
                LD      A,(sam_shift_current)
                LD      (sam_shift_previous_mask),A
                INC     HL
                LD      A,(HL)
                LD      (sam_shift_current),A
                PUSH    BC
                LD      A,(sam_shift_previous_pixel)
                RLCA
                RLCA
                RLCA
                RLCA
                AND     0xF0
                LD      C,A
                LD      A,(sam_shift_current)
                RRCA
                RRCA
                RRCA
                RRCA
                AND     0x0F
                OR      C
                LD      (DE),A
                INC     DE
                POP     BC
                LD      A,(sam_shift_current)
                LD      (sam_shift_previous_pixel),A
                INC     HL
                DEC     C
                JR      NZ,sam_shift_pair
                LD      A,(sam_shift_previous_mask)
                RLCA
                RLCA
                RLCA
                RLCA
                AND     0xF0
                OR      0x0F
                LD      (DE),A
                INC     DE
                LD      A,(sam_shift_previous_pixel)
                RLCA
                RLCA
                RLCA
                RLCA
                AND     0xF0
                LD      (DE),A
                INC     DE
                DEC     B
                JP      NZ,sam_shift_row
                EXX
                POP     HL
                POP     DE
                POP     BC
                EXX
                EX      AF,AF'
                POP     AF
                EX      AF,AF'
                POP     IY
                POP     IX
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
;
; All callers have disabled interrupts; preserve unrelated LMPR write-protect.
sam_map_draw_page:
                PUSH    AF
                PUSH    BC
                LD      A,(sam_saved_lmpr)
                AND     0xC0
                LD      B,A
                LD      A,(sam_draw_page)
                OR      B
                OR      0x20
                OUT     (sam_lmpr),A
                POP     BC
                POP     AF
                RET
;
; HL = byte array, DE = payload offset, B = first nibble (0 high, 1 low),
; C = pixel count. Return A=1 if a selected nibble is nonzero, else A=0.
sam_any_solid_pixel_row:
                LD      (sam_packed_row_array),HL
                LD      (sam_packed_row_offset),DE
                LD      A,B
                LD      (sam_packed_row_parity),A
                LD      A,C
                LD      (sam_packed_row_count),A
                LD      A,H
                OR      L
                JP      Z,sam_packed_row_null
                LD      A,(HL)
                LD      (sam_packed_row_length),A
                INC     HL
                LD      A,(HL)
                LD      (sam_packed_row_length+1),A
                LD      A,(sam_packed_row_parity)
                CP      2
                JP      NC,sam_packed_row_bounds
                LD      A,(sam_packed_row_count)
                OR      A
                JR      Z,sam_packed_row_empty
                LD      B,A
                LD      A,(sam_packed_row_parity)
                ADD     A,B
                INC     A
                SRL     A
                LD      (sam_packed_row_bytes),A
                LD      HL,(sam_packed_row_offset)
                LD      E,A
                LD      D,0
                ADD     HL,DE
                EX      DE,HL
                LD      HL,(sam_packed_row_length)
                OR      A
                SBC     HL,DE
                JP      C,sam_packed_row_bounds
                LD      HL,(sam_packed_row_array)
                INC     HL
                INC     HL
                LD      DE,(sam_packed_row_offset)
                ADD     HL,DE
                LD      A,(sam_packed_row_parity)
                OR      A
                JR      Z,sam_packed_row_pairs
                LD      A,(HL)
                AND     0x0F
                JR      NZ,sam_packed_row_solid
                INC     HL
                LD      A,(sam_packed_row_count)
                DEC     A
                LD      (sam_packed_row_count),A
                JR      Z,sam_packed_row_empty
sam_packed_row_pairs:
                LD      A,(sam_packed_row_count)
                OR      A
                JR      Z,sam_packed_row_empty
                CP      2
                JR      C,sam_packed_row_last_high
sam_packed_row_pair_loop:
                LD      A,(HL)
                OR      A
                JR      NZ,sam_packed_row_solid
                INC     HL
                LD      A,(sam_packed_row_count)
                SUB     2
                LD      (sam_packed_row_count),A
                OR      A
                JR      Z,sam_packed_row_empty
                CP      2
                JR      NC,sam_packed_row_pair_loop
sam_packed_row_last_high:
                LD      A,(HL)
                AND     0xF0
                JR      NZ,sam_packed_row_solid
sam_packed_row_empty:
                XOR     A
                RET
sam_packed_row_solid:
                LD      A,1
                RET
sam_packed_row_bounds:
                LD      A,14
                JP      sam_trap
sam_packed_row_null:
                LD      A,1
                JP      sam_trap
;
; Decode a count/value or literal packet stream into one complete MODE 4 background.
; Source and destination are banked byte-array payloads; page arguments select their
; lower-memory mappings. Zero terminates only after exactly 0x6000 output bytes.
sam_decode_rle_background:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                PUSH    IX
                LD      HL,(sam_decode_source)
                LD      A,H
                OR      L
                JP      Z,sam_decode_null
                LD      A,(sam_decode_source_page)
                CALL    sam_map_memory_page
                LD      HL,(sam_decode_source)
                LD      E,(HL)
                INC     HL
                LD      D,(HL)
                LD      (sam_decode_source_length),DE
                LD      HL,(sam_decode_source)
                INC     HL
                INC     HL
                ADD     HL,DE
                JP      C,sam_decode_bounds
                LD      (sam_decode_source_end),HL
                LD      HL,(sam_decode_source)
                INC     HL
                INC     HL
                LD      (sam_decode_source_pointer),HL
                CALL    sam_restore_memory_page
                LD      HL,(sam_decode_destination)
                LD      A,H
                OR      L
                JP      Z,sam_decode_null
                LD      A,(sam_decode_destination_page)
                CALL    sam_map_memory_page
                LD      HL,(sam_decode_destination)
                LD      E,(HL)
                INC     HL
                LD      D,(HL)
                LD      HL,0x6000
                OR      A
                SBC     HL,DE
                JP      C,sam_decode_bounds
                JP      Z,sam_decode_destination_ready
                JP      sam_decode_bounds
sam_decode_destination_ready:
                CALL    sam_restore_memory_page
                LD      HL,0
                LD      (sam_decode_written),HL
sam_decode_packet:
                LD      A,(sam_decode_source_page)
                CALL    sam_map_memory_page
                CALL    sam_decode_source_available
                LD      A,(HL)
                INC     HL
                LD      (sam_decode_source_pointer),HL
                OR      A
                JP      Z,sam_decode_finished
                BIT     7,A
                JP      NZ,sam_decode_literals
                LD      (sam_decode_packet_count),A
                LD      A,0
                LD      (sam_decode_packet_count+1),A
                CALL    sam_decode_check_capacity
                CALL    sam_decode_source_available
                LD      A,(HL)
                INC     HL
                LD      (sam_decode_source_pointer),HL
                LD      (sam_decode_packet_value),A
                LD      IX,sam_decode_buffer
                LD      A,(sam_decode_packet_count)
                LD      B,A
sam_decode_run_fill:
                LD      A,(sam_decode_packet_value)
                LD      (IX+0),A
                INC     IX
                DJNZ    sam_decode_run_fill
                JR      sam_decode_copy_packet
sam_decode_literals:
                AND     0x7F
                JP      Z,sam_decode_bounds
                LD      (sam_decode_packet_count),A
                LD      A,0
                LD      (sam_decode_packet_count+1),A
                CALL    sam_decode_check_capacity
                LD      A,(sam_decode_packet_count)
                LD      B,A
                LD      IX,sam_decode_buffer
sam_decode_literal_copy:
                CALL    sam_decode_source_available
                LD      A,(HL)
                INC     HL
                LD      (sam_decode_source_pointer),HL
                LD      (IX+0),A
                INC     IX
                DJNZ    sam_decode_literal_copy
sam_decode_copy_packet:
                CALL    sam_restore_memory_page
                LD      A,(sam_decode_destination_page)
                CALL    sam_map_memory_page
                LD      HL,(sam_decode_destination)
                INC     HL
                INC     HL
                LD      DE,(sam_decode_written)
                ADD     HL,DE
                EX      DE,HL
                LD      HL,sam_decode_buffer
                LD      BC,(sam_decode_packet_count)
                LDIR
                CALL    sam_restore_memory_page
                LD      HL,(sam_decode_new_written)
                LD      (sam_decode_written),HL
                JP      sam_decode_packet
sam_decode_finished:
                CALL    sam_restore_memory_page
                LD      HL,(sam_decode_written)
                LD      DE,0x6000
                OR      A
                SBC     HL,DE
                JP      NZ,sam_decode_bounds
                POP     IX
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
sam_decode_source_available:
                LD      HL,(sam_decode_source_pointer)
                LD      DE,(sam_decode_source_end)
                OR      A
                SBC     HL,DE
                JP      NC,sam_decode_bounds
                ADD     HL,DE
                RET
sam_decode_check_capacity:
                LD      HL,(sam_decode_written)
                LD      DE,(sam_decode_packet_count)
                ADD     HL,DE
                JP      C,sam_decode_bounds
                LD      A,H
                CP      0x60
                JP      C,sam_decode_capacity_valid
                JP      NZ,sam_decode_bounds
                LD      A,L
                OR      A
                JP      NZ,sam_decode_bounds
sam_decode_capacity_valid:
                LD      (sam_decode_new_written),HL
                RET
sam_decode_null:
                LD      A,1
                JP      sam_trap
sam_decode_bounds:
                LD      A,14
                JP      sam_trap
;
; Expand word-count/value records into the caller's mapped 24 KB collision plane.
; A zero word terminates only after exactly the destination array length is filled.
sam_expand_byte_runs:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                LD      HL,(sam_decode_source)
                LD      A,H
                OR      L
                JP      Z,sam_expand_null
                LD      A,(sam_decode_source_page)
                CALL    sam_map_memory_page
                LD      HL,(sam_decode_source)
                LD      E,(HL)
                INC     HL
                LD      D,(HL)
                LD      (sam_expand_source_length),DE
                LD      HL,(sam_decode_source)
                INC     HL
                INC     HL
                ADD     HL,DE
                JP      C,sam_expand_bounds
                LD      (sam_expand_source_end),HL
                LD      HL,(sam_decode_source)
                INC     HL
                INC     HL
                LD      (sam_expand_source_pointer),HL
                CALL    sam_restore_memory_page
                LD      HL,(sam_decode_destination)
                LD      A,H
                OR      L
                JP      Z,sam_expand_null
                LD      A,(sam_decode_destination_page)
                CALL    sam_map_memory_page
                LD      HL,(sam_decode_destination)
                LD      E,(HL)
                INC     HL
                LD      D,(HL)
                LD      HL,0x6000
                OR      A
                SBC     HL,DE
                JP      C,sam_expand_bounds
                JP      Z,sam_expand_destination_ready
                JP      sam_expand_bounds
sam_expand_destination_ready:
                CALL    sam_restore_memory_page
                LD      HL,0
                LD      (sam_expand_written),HL
sam_expand_record:
                LD      A,(sam_decode_source_page)
                CALL    sam_map_memory_page
                CALL    sam_expand_source_available
                LD      A,(HL)
                LD      (sam_expand_count),A
                INC     HL
                LD      (sam_expand_source_pointer),HL
                CALL    sam_expand_source_available
                LD      A,(HL)
                LD      (sam_expand_count+1),A
                INC     HL
                LD      (sam_expand_source_pointer),HL
                LD      A,(sam_expand_count)
                LD      B,A
                LD      A,(sam_expand_count+1)
                OR      B
                JP      Z,sam_expand_finished
                CALL    sam_expand_source_available
                LD      A,(HL)
                LD      (sam_expand_value),A
                INC     HL
                LD      (sam_expand_source_pointer),HL
                LD      HL,(sam_expand_written)
                LD      DE,(sam_expand_count)
                ADD     HL,DE
                JP      C,sam_expand_bounds
                LD      A,H
                CP      0x60
                JP      C,sam_expand_capacity_valid
                JP      NZ,sam_expand_bounds
                LD      A,L
                OR      A
                JP      NZ,sam_expand_bounds
sam_expand_capacity_valid:
                LD      (sam_expand_new_written),HL
                CALL    sam_restore_memory_page
                LD      A,(sam_decode_destination_page)
                CALL    sam_map_memory_page
                LD      HL,(sam_decode_destination)
                INC     HL
                INC     HL
                LD      DE,(sam_expand_written)
                ADD     HL,DE
                LD      BC,(sam_expand_count)
                LD      A,(sam_expand_value)
sam_expand_fill:
                LD      (HL),A
                INC     HL
                DEC     BC
                LD      A,B
                OR      C
                LD      A,(sam_expand_value)
                JR      NZ,sam_expand_fill
                CALL    sam_restore_memory_page
                LD      HL,(sam_expand_new_written)
                LD      (sam_expand_written),HL
                JP      sam_expand_record
sam_expand_finished:
                CALL    sam_restore_memory_page
                LD      HL,(sam_expand_written)
                LD      DE,0x6000
                OR      A
                SBC     HL,DE
                JP      NZ,sam_expand_bounds
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
sam_expand_source_available:
                LD      HL,(sam_expand_source_pointer)
                LD      DE,(sam_expand_source_end)
                OR      A
                SBC     HL,DE
                JP      NC,sam_expand_bounds
                ADD     HL,DE
                RET
sam_expand_null:
                LD      A,1
                JP      sam_trap
sam_expand_bounds:
                LD      A,14
                JP      sam_trap
;
; Copy a checked set of MODE 4 rows from a banked byte array to the hidden page.
; A 128-byte resident staging array keeps source and display mappings separate.
sam_copy_banked_rows_to_screen:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                LD      HL,(sam_banked_screen_source)
                LD      (sam_copy_source),HL
                LD      HL,(sam_banked_screen_source_offset)
                LD      (sam_copy_source_offset),HL
                LD      HL,sam_copy_banked_screen_buffer
                LD      (sam_copy_destination),HL
                LD      HL,0
                LD      (sam_copy_destination_offset),HL
                LD      A,(sam_banked_screen_width)
                LD      (sam_copy_count),A
                XOR     A
                LD      (sam_copy_count+1),A
                LD      A,(sam_banked_screen_rows)
                LD      (sam_banked_screen_remaining),A
                LD      HL,(sam_banked_screen_offset)
                LD      (sam_banked_screen_current_offset),HL
sam_copy_banked_screen_row:
                LD      A,(sam_banked_screen_remaining)
                OR      A
                JR      Z,sam_copy_banked_screen_done
                LD      A,(sam_banked_screen_page)
                CALL    sam_map_memory_page
                CALL    sam_copy_bytes
                CALL    sam_restore_memory_page
                LD      HL,sam_copy_banked_screen_buffer
                LD      (sam_screen_source),HL
                LD      HL,0
                LD      (sam_screen_source_offset),HL
                LD      HL,(sam_banked_screen_current_offset)
                LD      (sam_screen_offset),HL
                LD      HL,(sam_copy_count)
                LD      (sam_screen_count),HL
                CALL    sam_copy_bytes_to_screen
                LD      HL,(sam_copy_source_offset)
                LD      DE,128
                ADD     HL,DE
                JP      C,sam_copy_bounds
                LD      (sam_copy_source_offset),HL
                LD      HL,(sam_banked_screen_current_offset)
                ADD     HL,DE
                JP      C,sam_screen_copy_bounds
                LD      (sam_banked_screen_current_offset),HL
                LD      A,(sam_banked_screen_remaining)
                DEC     A
                LD      (sam_banked_screen_remaining),A
                JR      sam_copy_banked_screen_row
sam_copy_banked_screen_done:
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
;
; Overlay a packed MODE 4 image onto a banked background; colour zero is transparent.
; Each source row is staged in resident RAM so adjacent page mappings never alias.
sam_compose_transparent_image:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                LD      A,(sam_compose_width)
                OR      A
                JP      Z,sam_compose_bounds
                CP      129
                JP      NC,sam_compose_bounds
                LD      A,(sam_compose_rows)
                OR      A
                JP      Z,sam_compose_bounds
                LD      A,(sam_compose_destination_page)
                CALL    sam_map_memory_page
                LD      HL,(sam_compose_destination)
                LD      A,H
                OR      L
                JP      Z,sam_compose_null
                LD      A,(HL)
                LD      (sam_compose_destination_length),A
                INC     HL
                LD      A,(HL)
                LD      (sam_compose_destination_length+1),A
                CALL    sam_restore_memory_page
                LD      HL,(sam_compose_destination_offset)
                LD      A,L
                AND     0x7F
                LD      E,A
                LD      A,(sam_compose_width)
                ADD     A,E
                JP      C,sam_compose_bounds
                CP      129
                JP      NC,sam_compose_bounds
                LD      HL,(sam_compose_destination_offset)
                LD      (sam_compose_check_offset),HL
                LD      A,(sam_compose_rows)
                DEC     A
                LD      B,A
sam_compose_check_rows:
                LD      A,B
                OR      A
                JR      Z,sam_compose_check_end
                LD      HL,(sam_compose_check_offset)
                LD      DE,128
                ADD     HL,DE
                JP      C,sam_compose_bounds
                LD      (sam_compose_check_offset),HL
                DEC     B
                JR      sam_compose_check_rows
sam_compose_check_end:
                LD      HL,(sam_compose_check_offset)
                LD      A,(sam_compose_width)
                LD      E,A
                LD      D,0
                ADD     HL,DE
                JP      C,sam_compose_bounds
                LD      A,H
                CP      0x60
                JR      C,sam_compose_screen_end_valid
                JP      NZ,sam_compose_bounds
                LD      A,L
                OR      A
                JP      NZ,sam_compose_bounds
sam_compose_screen_end_valid:
                LD      DE,(sam_compose_destination_length)
                LD      A,H
                CP      D
                JR      C,sam_compose_destination_end_valid
                JP      NZ,sam_compose_bounds
                LD      A,L
                CP      E
                JR      C,sam_compose_destination_end_valid
                JP      NZ,sam_compose_bounds
sam_compose_destination_end_valid:
                LD      HL,(sam_compose_destination)
                INC     HL
                INC     HL
                LD      DE,(sam_compose_destination_offset)
                ADD     HL,DE
                JP      C,sam_compose_bounds
                LD      (sam_compose_destination_pointer),HL
                LD      HL,(sam_compose_source_offset)
                LD      (sam_compose_source_current_offset),HL
                LD      A,(sam_compose_rows)
                LD      (sam_compose_remaining),A
                LD      HL,(sam_compose_source)
                LD      (sam_copy_source),HL
                LD      HL,sam_compose_row_buffer
                LD      (sam_copy_destination),HL
                LD      HL,0
                LD      (sam_copy_destination_offset),HL
                LD      A,(sam_compose_width)
                LD      (sam_copy_count),A
                XOR     A
                LD      (sam_copy_count+1),A
sam_compose_row:
                LD      A,(sam_compose_source_page)
                CALL    sam_map_memory_page
                LD      HL,(sam_compose_source_current_offset)
                LD      (sam_copy_source_offset),HL
                CALL    sam_copy_bytes
                CALL    sam_restore_memory_page
                LD      A,(sam_compose_destination_page)
                CALL    sam_map_memory_page
                LD      HL,sam_compose_row_buffer+2
                LD      DE,(sam_compose_destination_pointer)
                LD      A,(sam_compose_width)
                LD      B,A
sam_compose_byte:
                LD      A,(HL)
                LD      C,A
                AND     0xF0
                JR      NZ,sam_compose_left_opaque
                LD      A,(DE)
                AND     0xF0
                LD      C,A
                LD      A,(HL)
                AND     0x0F
                OR      C
                LD      C,A
sam_compose_left_opaque:
                LD      A,C
                AND     0x0F
                JR      NZ,sam_compose_right_opaque
                LD      A,C
                AND     0xF0
                LD      C,A
                LD      A,(DE)
                AND     0x0F
                OR      C
                LD      C,A
                LD      A,(HL)
                AND     0xF0
                OR      C
                LD      C,A
sam_compose_right_opaque:
                LD      A,C
                LD      (DE),A
                INC     HL
                INC     DE
                DJNZ    sam_compose_byte
                CALL    sam_restore_memory_page
                LD      A,(sam_compose_remaining)
                DEC     A
                LD      (sam_compose_remaining),A
                JR      Z,sam_compose_done
                LD      HL,(sam_compose_destination_pointer)
                LD      DE,128
                ADD     HL,DE
                JP      C,sam_compose_bounds
                LD      (sam_compose_destination_pointer),HL
                LD      HL,(sam_compose_source_current_offset)
                LD      A,(sam_compose_source_stride)
                LD      E,A
                LD      D,0
                ADD     HL,DE
                JP      C,sam_compose_bounds
                LD      (sam_compose_source_current_offset),HL
                JR      sam_compose_row
sam_compose_done:
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
sam_compose_null:
                LD      A,1
                JP      sam_trap
sam_compose_bounds:
                LD      A,13
                JP      sam_trap
;
; Draw a one-pixel packed outline into a banked MODE 4 screen-sized array.
; The top/bottom bytes fill both nibbles; side bytes preserve the inner pixel.
sam_draw_packed_outline:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                LD      A,(sam_outline_width)
                OR      A
                JP      Z,sam_outline_bounds
                CP      129
                JP      NC,sam_outline_bounds
                LD      A,(sam_outline_height)
                CP      2
                JP      C,sam_outline_bounds
                LD      A,(sam_outline_page)
                CALL    sam_map_memory_page
                LD      HL,(sam_outline_destination)
                LD      A,H
                OR      L
                JP      Z,sam_outline_null
                LD      A,(HL)
                LD      (sam_outline_length),A
                INC     HL
                LD      A,(HL)
                LD      (sam_outline_length+1),A
                CALL    sam_restore_memory_page
                LD      HL,(sam_outline_offset)
                LD      A,L
                AND     0x7F
                LD      E,A
                LD      A,(sam_outline_width)
                ADD     A,E
                JP      C,sam_outline_bounds
                CP      129
                JP      NC,sam_outline_bounds
                LD      HL,(sam_outline_offset)
                LD      A,(sam_outline_height)
                DEC     A
                LD      B,A
sam_outline_check_rows:
                LD      A,B
                OR      A
                JR      Z,sam_outline_check_end
                LD      DE,128
                ADD     HL,DE
                JP      C,sam_outline_bounds
                DEC     B
                JR      sam_outline_check_rows
sam_outline_check_end:
                LD      A,(sam_outline_width)
                LD      E,A
                LD      D,0
                ADD     HL,DE
                JP      C,sam_outline_bounds
                LD      A,H
                CP      0x60
                JR      C,sam_outline_screen_end_valid
                JP      NZ,sam_outline_bounds
                LD      A,L
                OR      A
                JP      NZ,sam_outline_bounds
sam_outline_screen_end_valid:
                LD      DE,(sam_outline_length)
                LD      A,H
                CP      D
                JR      C,sam_outline_length_valid
                JP      NZ,sam_outline_bounds
                LD      A,L
                CP      E
                JR      C,sam_outline_length_valid
                JP      NZ,sam_outline_bounds
sam_outline_length_valid:
                LD      HL,(sam_outline_destination)
                INC     HL
                INC     HL
                LD      DE,(sam_outline_offset)
                ADD     HL,DE
                JP      C,sam_outline_bounds
                LD      (sam_outline_pointer),HL
                LD      A,(sam_outline_page)
                CALL    sam_map_memory_page
                LD      A,(sam_outline_top_bottom)
                LD      C,A
                LD      A,(sam_outline_width)
                LD      B,A
                LD      A,C
sam_outline_top:
                LD      (HL),A
                INC     HL
                DJNZ    sam_outline_top
                LD      HL,(sam_outline_pointer)
                LD      DE,128
                ADD     HL,DE
                LD      (sam_outline_current),HL
                LD      A,(sam_outline_height)
                SUB     2
                LD      (sam_outline_remaining),A
sam_outline_sides:
                LD      A,(sam_outline_remaining)
                OR      A
                JR      Z,sam_outline_bottom_start
                LD      HL,(sam_outline_current)
                LD      A,(sam_outline_left)
                LD      (HL),A
                LD      A,(sam_outline_width)
                DEC     A
                LD      E,A
                LD      D,0
                ADD     HL,DE
                LD      A,(sam_outline_right)
                LD      (HL),A
                LD      HL,(sam_outline_current)
                LD      DE,128
                ADD     HL,DE
                LD      (sam_outline_current),HL
                LD      A,(sam_outline_remaining)
                DEC     A
                LD      (sam_outline_remaining),A
                JR      sam_outline_sides
sam_outline_bottom_start:
                LD      HL,(sam_outline_current)
                LD      A,(sam_outline_top_bottom)
                LD      C,A
                LD      A,(sam_outline_width)
                LD      B,A
                LD      A,C
sam_outline_bottom:
                LD      (HL),A
                INC     HL
                DJNZ    sam_outline_bottom
                CALL    sam_restore_memory_page
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
sam_outline_null:
                LD      A,1
                JP      sam_trap
sam_outline_bounds:
                LD      A,13
                JP      sam_trap
;
; A = physical page. Pair with sam_restore_memory_page before enabling ROM work.
sam_map_memory_page:
                PUSH    AF
                PUSH    BC
                LD      C,A
                LD      A,I
                DI
                JP      PO,sam_map_page_iff_off
                LD      A,1
                JR      sam_map_page_iff_saved
sam_map_page_iff_off:
                XOR     A
sam_map_page_iff_saved:
                LD      (sam_bank_iff),A
                LD      A,(sam_saved_lmpr)
                AND     0xC0
                LD      B,A
                LD      A,C
                OR      B
                OR      0x20
                OUT     (sam_lmpr),A
                POP     BC
                POP     AF
                RET
;
sam_restore_memory_page:
                PUSH    AF
                DI
                CALL    sam_restore_lower_mapping
                LD      A,(sam_bank_iff)
                OR      A
                JR      Z,sam_restore_page_iff_off
                POP     AF
                EI
                RET
sam_restore_page_iff_off:
                POP     AF
                RET
;
; Restore the original lower mapping, or the active code bank while a
; banked C# method is running in that window.
sam_restore_lower_mapping:
                PUSH    AF
                PUSH    BC
                LD      A,(sam_active_code_page)
                OR      A
                JR      Z,sam_restore_original_mapping
                LD      C,A
                LD      A,(sam_saved_lmpr)
                AND     0xC0
                OR      C
                OR      0x20
                OUT     (sam_lmpr),A
                JR      sam_restore_mapping_done
sam_restore_original_mapping:
                LD      A,(sam_rom_lmpr)
                OUT     (sam_lmpr),A
sam_restore_mapping_done:
                POP     BC
                POP     AF
                RET
;
; A = physical code page. Keep the prior page and interrupt state beneath
; the stub's CALL return so nested banked methods restore in stack order.
sam_enter_banked_code:
                LD      C,A
                POP     HL
                LD      A,(sam_active_code_page)
                PUSH    AF
                LD      A,I
                DI
                LD      A,0
                JP      PO,sam_banked_iff_saved
                INC     A
sam_banked_iff_saved:
                PUSH    AF
                LD      A,C
                LD      (sam_active_code_page),A
                CALL    sam_map_memory_page
                PUSH    HL
                RET
;
; Restore the page and IFF state paired with sam_enter_banked_code.
sam_leave_banked_code:
                POP     HL
                POP     AF
                LD      D,A
                POP     AF
                LD      E,A
                LD      (sam_active_code_page),A
                OR      A
                JR      NZ,sam_banked_restore_previous_page
                LD      A,(sam_rom_lmpr)
                OUT     (sam_lmpr),A
                LD      A,D
                OR      A
                JR      Z,sam_banked_return_done
                EI
                JR      sam_banked_return_done
sam_banked_restore_previous_page:
                LD      A,(sam_saved_lmpr)
                AND     0xC0
                OR      E
                OR      0x20
                OUT     (sam_lmpr),A
sam_banked_return_done:
                XOR     A
                LD      (sam_bank_iff),A
                PUSH    HL
                RET
;
; Enable page 4/5 and 6/7 double buffering. Start drawing on hidden page 6.
sam_begin_double_buffer:
                PUSH    AF
                LD      A,4
                LD      (sam_screen_page_first),A
                LD      A,6
                LD      (sam_screen_page_second),A
                LD      (sam_draw_page),A
                XOR     A
                CALL    sam_fill_screen
                POP     AF
                RET
;
; A = initially displayed MODE 4 page, B = alternate page.
sam_begin_double_buffer_pages:
                PUSH    AF
                PUSH    BC
                LD      (sam_screen_page_first),A
                LD      A,B
                LD      (sam_screen_page_second),A
                LD      (sam_draw_page),A
                XOR     A
                CALL    sam_fill_screen
                IN      A,(sam_vmpr)
                AND     0xE1
                LD      B,A
                LD      A,(sam_screen_page_first)
                OR      B
                OUT     (sam_vmpr),A
                LD      (0x5A78),A
                POP     BC
                POP     AF
                RET
;
; Display the completed drawing page, then select the other page for drawing.
sam_flip_screen:
                PUSH    AF
                PUSH    BC
                DI
                LD      A,(sam_draw_page)
                LD      C,A
                IN      A,(sam_vmpr)
                AND     0xE1
                LD      B,A
                LD      A,C
                OR      B
                OUT     (sam_vmpr),A
                LD      (0x5A78),A
                LD      A,(sam_screen_page_first)
                CP      C
                JR      Z,sam_flip_to_second
                LD      A,(sam_screen_page_first)
                JR      sam_flip_store_draw_page
sam_flip_to_second:
                LD      A,(sam_screen_page_second)
sam_flip_store_draw_page:
                LD      (sam_draw_page),A
                POP     BC
                POP     AF
                RET
;
; HL = resident immutable uint16-LE byte count followed by ASCII bytes.
; Validate the WHOLE span before printing; no low RAM / stack / wrap access.
sam_print:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                LD      A,H
                OR      L
                JR      Z,sam_print_null
                LD      DE,0x8000
                OR      A
                SBC     HL,DE
                JR      C,sam_print_span
                ADD     HL,DE
                LD      DE,0xDFFE
                OR      A
                SBC     HL,DE
                JR      NC,sam_print_span
                ADD     HL,DE
                LD      C,(HL)
                INC     HL
                LD      B,(HL)
                INC     HL
                PUSH    HL
                ADD     HL,BC
                JR      C,sam_print_span
                LD      DE,0xE000
                OR      A
                SBC     HL,DE
                JR      NC,sam_print_span
                POP     HL
sam_print_loop:
                LD      A,B
                OR      C
                JR      Z,sam_print_done
                LD      A,(HL)
                CP      0x80
                JR      NC,sam_print_ascii
                CALL    sam_emit_ascii
                INC     HL
                DEC     BC
                JR      sam_print_loop
sam_print_done:
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
sam_print_null:
                LD      A,1
                JP      sam_trap
sam_print_span:
                LD      A,7
                JP      sam_trap
sam_print_ascii:
                LD      A,6
                JP      sam_trap
;
; HL = C# char (uint16). Non-ASCII is an explicit observable target failure.
sam_print_char:
                PUSH    AF
                LD      A,H
                OR      A
                JR      NZ,sam_print_ascii
                LD      A,L
                CP      0x80
                JR      NC,sam_print_ascii
                CALL    sam_emit_ascii
                POP     AF
                RET
;
; ROM calls can temporarily map ROM1 over C000..FFFF, including FF00 stack.
; Save registers on resident stack, then switch INLINE to BASIC's low stack.
; Never CALL a "set SP" helper: its return address would be on the wrong stack.
sam_emit_ascii:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                PUSH    IX
                PUSH    IY
                EX      AF,AF'
                PUSH    AF
                EX      AF,AF'
                EXX
                PUSH    BC
                PUSH    DE
                PUSH    HL
                EXX
                ; Do not interpret ASCII controls as SAM ink/AT/token commands.
                CP      10
                JR      NZ,sam_emit_not_lf
                LD      A,13           ; ASCII LF becomes SAM newline
sam_emit_not_lf:
                CP      13
                JR      Z,sam_emit_rom
                CP      32
                JR      NC,sam_emit_printable
                LD      A,32           ; NUL and other controls occupy one space
sam_emit_printable:
                CP      127
                JR      NZ,sam_emit_rom
                LD      A,63           ; DEL is not a printable ASCII character
sam_emit_rom:
                DI
                LD      (sam_runtime_sp),SP
                LD      SP,(sam_basic_sp)
                LD      IY,(sam_basic_iy)
                RST     16             ; ROM0 mapped by every public paging helper
                DI
                LD      SP,(sam_runtime_sp)
                EXX
                POP     HL
                POP     DE
                POP     BC
                EXX
                EX      AF,AF'
                POP     AF
                EX      AF,AF'
                POP     IY
                POP     IX
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
;
; Wait for an initial release, a fresh key press, then release (any matrix key).
; Direct polling avoids ROM's interrupt-maintained keyboard buffer and HALT.
sam_wait_for_key:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                DI
sam_wait_initial:
                CALL    sam_key_state
                JR      NZ,sam_wait_initial
sam_wait_press:
                CALL    sam_key_state
                JR      Z,sam_wait_press
sam_wait_release:
                CALL    sam_key_state
                JR      NZ,sam_wait_release
                POP     DE
                POP     BC
                POP     AF
                RET
;
; SAM extends the Spectrum's five FE bits with three F9 status-port bits.
; Match ROM v3.0 D5BC's row selection, including the ninth FF row, without
; using its mutable interrupt-maintained key buffer. Z means all keys released.
sam_key_state:
                LD      BC,0xFEFE
sam_key_row:
                IN      E,(C)
                LD      C,0xF9
                IN      A,(C)
                AND     0xE0           ; Ignore interrupt status in the low bits
                LD      D,A
                LD      A,E
                AND     0x1F
                OR      D
                CPL                     ; Active-low matrix: nonzero means pressed
                OR      A
                RET     NZ
                LD      C,0xFE
                RLC     B
                JR      C,sam_key_row
                LD      B,0xFF
                IN      A,(C)
                OR      0xE1           ; Only bits 1..4 used in ninth row
                CPL
                OR      A
                RET
;
; Nonreturning trap: A = code. A fixed debugger byte plus visible hex message.
; Keep native frame intact for debugger inspection; press any key to exit BASIC.
sam_trap:
                DI
                LD      (sam_fault_code),A
                LD      A,(sam_rom_lmpr)
                OUT     (sam_lmpr),A
                LD      (sam_runtime_sp),SP
                LD      SP,(sam_basic_sp)
                LD      IY,(sam_basic_iy)
                XOR     A
                CALL    0x014E         ; ROM clear resets text cursor for visible fault
                DI
                LD      SP,(sam_runtime_sp)
                CALL    sam_palette
                CALL    sam_clear_screen
                LD      HL,sam_trap_text
                CALL    sam_print
                LD      A,(sam_fault_code)
                RRCA
                RRCA
                RRCA
                RRCA
                CALL    sam_hex_digit
                LD      A,(sam_fault_code)
                CALL    sam_hex_digit
                CALL    sam_wait_for_key
                JP      sam_exit
sam_hex_digit:
                AND     15
                ADD     A,48
                CP      58
                JR      C,sam_hex_emit
                ADD     A,7
sam_hex_emit:
                JP      sam_emit_ascii
;
; Fixed, visible palette while DI: entry zero black, entry fifteen white.
; This is deliberately not a ROM-interrupt palette animation.
sam_palette:
                PUSH    AF
                PUSH    BC
                PUSH    DE
                PUSH    HL
                ; The ROM alternates two PALTAB phases during its interrupt.
                ; Updating only hardware permits a transient old colour/flash.
                LD      HL,sam_palette_data
                LD      DE,0x55D8
                LD      BC,16
                LDIR
                LD      HL,sam_palette_data
                LD      DE,0x55EC
                LD      BC,16
                LDIR
                LD      BC,sam_clut
                LD      HL,sam_palette_data
sam_palette_loop:
                LD      A,(HL)
                OUT     (C),A          ; B selects CLUT entry, C=248
                INC     HL
                INC     B
                LD      A,B
                CP      16
                JR      NZ,sam_palette_loop
                POP     HL
                POP     DE
                POP     BC
                POP     AF
                RET
sam_palette_data:
                DEFB    0,16,32,48,64,80,96,120,0,17,34,51,68,85,102,127
sam_palette_source:        DEFW 0
sam_interleave_array:
                DEFW 1025
sam_interleave_header:     DEFB 1
                ASSERT sam_interleave_header == sam_interleave_array+2
                ASSERT sam_interleave_buffer == sam_interleave_header+1
sam_interleave_buffer:
                DEFS    1024
sam_sprite_screen_offset: DEFW 0
sam_sprite_frame_index:   DEFB 0
sam_sprite_width:         DEFB 0
sam_sprite_height:        DEFB 0
sam_sprite_stride:        DEFB 0
sam_sprite_data_pointer:  DEFW 0
sam_sprite_frame_size:    DEFW 0
sam_sprite_frame_table_pointer: DEFW 0
sam_rle_end:              DEFW 0
sam_rle_remaining:        DEFW 0
sam_data_end:             DEFW 0
sam_bank_iff:             DEFB 0
sam_active_code_page:     DEFB 0
sam_copy_source:          DEFW 0
sam_copy_source_offset:   DEFW 0
sam_copy_destination:     DEFW 0
sam_copy_destination_offset: DEFW 0
sam_copy_count:           DEFW 0
sam_copy_source_length:   DEFW 0
sam_copy_destination_length: DEFW 0
sam_copy_source_pointer:  DEFW 0
sam_copy_destination_pointer: DEFW 0
sam_screen_source:        DEFW 0
sam_screen_source_offset: DEFW 0
sam_screen_offset:        DEFW 0
sam_screen_count:         DEFW 0
sam_screen_source_length: DEFW 0
sam_screen_source_pointer: DEFW 0
sam_screen_iff:           DEFB 0
sam_banked_screen_source: DEFW 0
sam_initial_data_source:      DEFW 0
sam_initial_data_destination: DEFW 0
sam_initial_data_length:      DEFW 0
sam_initial_data_remaining:   DEFW 0
sam_initial_data_page:        DEFB 0
sam_initial_data_value:       DEFB 0
sam_banked_screen_page:   DEFB 0
sam_banked_screen_source_offset: DEFW 0
sam_banked_screen_offset: DEFW 0
sam_banked_screen_width:  DEFB 0
sam_banked_screen_rows:   DEFB 0
sam_banked_screen_remaining: DEFB 0
sam_banked_screen_current_offset: DEFW 0
sam_copy_banked_screen_buffer: DEFW 128
                DEFS    128
sam_compose_source:             DEFW 0
sam_compose_source_page:        DEFB 0
sam_compose_destination:        DEFW 0
sam_compose_destination_page:   DEFB 0
sam_compose_source_offset:      DEFW 0
sam_compose_destination_offset: DEFW 0
sam_compose_check_offset:       DEFW 0
sam_compose_width:              DEFB 0
sam_compose_rows:               DEFB 0
sam_compose_source_stride:      DEFB 0
sam_compose_source_current_offset: DEFW 0
sam_compose_destination_pointer: DEFW 0
sam_compose_destination_length: DEFW 0
sam_compose_remaining:          DEFB 0
sam_compose_row_buffer:         DEFW 128
                DEFS    128
sam_outline_destination:        DEFW 0
sam_outline_page:               DEFB 0
sam_outline_offset:             DEFW 0
sam_outline_width:              DEFB 0
sam_outline_height:             DEFB 0
sam_outline_top_bottom:         DEFB 0
sam_outline_left:               DEFB 0
sam_outline_right:              DEFB 0
sam_outline_length:             DEFW 0
sam_outline_pointer:            DEFW 0
sam_outline_current:            DEFW 0
sam_outline_remaining:          DEFB 0
sam_decode_source:        DEFW 0
sam_decode_source_page:   DEFB 0
sam_decode_destination:   DEFW 0
sam_decode_destination_page: DEFB 0
sam_decode_source_length: DEFW 0
sam_decode_source_end:    DEFW 0
sam_decode_source_pointer: DEFW 0
sam_decode_packet_count:  DEFW 0
sam_decode_packet_value:  DEFB 0
sam_decode_written:       DEFW 0
sam_decode_new_written:   DEFW 0
sam_decode_buffer:        DEFS 127
sam_expand_source_length: DEFW 0
sam_expand_source_end:   DEFW 0
sam_expand_source_pointer: DEFW 0
sam_expand_count:         DEFW 0
sam_expand_value:         DEFB 0
sam_expand_written:       DEFW 0
sam_expand_new_written:   DEFW 0
sam_shift_source:         DEFW 0
sam_shift_destination:    DEFW 0
sam_shift_source_pointer: DEFW 0
sam_shift_destination_pointer: DEFW 0
sam_shift_previous_mask:  DEFB 0
sam_shift_previous_pixel: DEFB 0
sam_shift_current:        DEFB 0
sam_draw_page:            DEFB 4
sam_screen_page_first:  DEFB    4
sam_screen_page_second: DEFB    6
sam_background_first:   DEFB    0
sam_background_second:   DEFB 0
sam_background_current:  DEFB 0
sam_packed_row_array:    DEFW 0
sam_packed_row_offset:   DEFW 0
sam_packed_row_length:   DEFW 0
sam_packed_row_bytes:    DEFB 0
sam_packed_row_parity:   DEFB 0
sam_packed_row_count:    DEFB 0
sam_trap_text:
                DEFW    9
                DM      "SAM TRAP "
sam_basic_sp:           DEFW 0
sam_runtime_sp:         DEFW 0
sam_basic_iy:           DEFW 0
sam_saved_lmpr:         DEFB 0
sam_saved_hmpr:         DEFB 0
sam_saved_vmpr:         DEFB 0
sam_rom_lmpr:           DEFB 0
sam_saved_iff:          DEFB 0
sam_basic_palette:     DEFS 40
sam_basic_vars:         DEFS 0x0300
sam_runtime_end:
                ; Assembler fails rather than allowing a resident/stack overlap.
                ASSERT  sam_runtime_end < sam_stack_floor
