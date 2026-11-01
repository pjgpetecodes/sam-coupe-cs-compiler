"""Measure real assembled sprite helpers in nominal Z80 CPU T-states.

No SAM contention, ROM interrupts or frame waiting are included.
"""
import json
import re
import sys
from pathlib import Path
import z80
from generate_tutorial_assets import ROOT

sys.path.insert(0, str(ROOT / "tests" / "SamRuntime"))
from tutorial_test import Target


def execute(cpu, address):
    cpu.sp = 0xFEFE
    cpu.memory[0xFEFE:0xFF00] = b"\x00\x70"
    cpu.pc = address
    cpu.set_breakpoint(0x7000)
    total = 0
    for _ in range(20):
        cpu.ticks_to_stop = 1_000_000
        cpu.run()
        total += 1_000_000 - cpu.ticks_to_stop
        if cpu.pc == 0x7000:
            return total
    raise AssertionError(f"Kernel did not return: {cpu.pc:04X}")


def main():
    rows = []
    for folder, compressed, width, height, offset in (
        ("Blog03/05-InterleavedSprite", False, 16, 32, 0),
        ("Blog04/06-3-AnimationTable", False, 16, 32, 0),
        ("Blog05/07-3-WalkAndJump", True, 16, 32, 0),
        ("Blog06/08-3-CroppedSidewaysJump", True, 13, 22, 641),
    ):
        out = ROOT / "tutorials" / Path(folder) / "out"
        target = Target(out)
        # SelectSpriteFrame passes the array payload, after its ushort length.
        target.cpu.hl, target.cpu.de = offset, target.labels["cs_data_001"] + 2
        target.cpu.a, target.cpu.b, target.cpu.c = 0, width, height
        helper = "sam_draw_rle_masked_sprite" if compressed else "sam_draw_masked_sprite"
        native = execute(target.cpu, target.labels[helper])
        original = z80.Z80Machine()
        original.set_memory_block(0x8000, (out / "comparison" / "original.bin").read_bytes())
        labels = {name: int(value) for name, value in re.findall(
            r"V(\w+)\s+p\d+\s+I(\d+)", (out / "comparison" / "original.symbols").read_text())}
        data = next(labels[name] for name in ("Sprite1_Interleaved", "Dizzy_System_Interleaved_Image1",
                                              "TIDizzy_1_Interleaved") if name in labels)
        original.hl, original.de = offset, data
        original.bc = 0
        handwritten = execute(original, labels["Draw_Sprite"])
        assert bytes(original.memory[:0x6000]) == bytes(target.banks[4][:0x6000])
        target = Target(out)
        target.cpu.hl, target.cpu.b, target.cpu.c = offset, width, height
        clearing = execute(target.cpu, target.labels["sam_clear_sprite"]) if "sam_clear_sprite" in target.labels else None
        row = dict(folder=folder, handwritten_draw_tstates=handwritten,
                   native_draw_tstates=native, native_rectangle_clear_tstates=clearing)
        rows.append(row)
        print(row)
    (ROOT / "out" / "verification" / "kernel-costs.json").write_text(json.dumps(rows, indent=2))


if __name__ == "__main__":
    main()
