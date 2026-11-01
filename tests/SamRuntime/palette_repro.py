"""Build a deliberately stale-ROM-palette fixture; never modify the runtime.

This isolates the black/cyan failure mechanism using the actual ROM interrupt.
"""
import re
import subprocess
from build_runtime_tests import ROOT, load_tools


def main():
    tools = load_tools()
    output = ROOT / "out" / "verification" / "palette-repro"
    output.mkdir(parents=True, exist_ok=True)
    runtime = (ROOT / "runtime" / "sam" / "hardware.asm").read_text()
    runtime, matches = re.subn(
        r"(sam_palette:\s+PUSH\s+AF\s+PUSH\s+BC\s+PUSH\s+DE\s+PUSH\s+HL\s+)"
        r".*?(?=                LD      BC,sam_clut)",
        r"\1", runtime, flags=re.S)
    assert matches == 1
    runtime, matches = re.subn(
        r"(sam_wait_frame_loop:\s+HALT\s+DI\s+)CALL\s+sam_palette",
        r"\1NOP", runtime)
    assert matches == 1
    (output / "sam-runtime.asm").write_text(runtime)
    (output / "program.asm").write_text("""
cs_initial_screen_page: EQU 4
        ORG 0x8000
        DUMP 1,0
        AUTOEXEC
        JP sam_start
cs_main:
        LD HL,0x55D8
        LD DE,0x55D9
        LD BC,39
        LD (HL),0
        LDIR
        LD A,0x5D
        LD (0x55EC),A
        LD A,8
        LD (0x5A08),A
repro_loop:
        LD A,1
        CALL sam_wait_frames
        JR repro_loop
        INCLUDE "sam-runtime.asm"
""")
    subprocess.run([tools["pyz80"], "--nozip", "--obj=program.bin",
                    "-I", tools["samdos"], "-o", "program.mgt", "program.asm"],
                   cwd=output, check=True)
    subprocess.run([tools["samdisk"], "copy", "--force", "program.mgt", "program.dsk"],
                   cwd=output, check=True)
    print(output / "program.dsk")


if __name__ == "__main__":
    main()
