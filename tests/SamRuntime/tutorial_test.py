"""Execute compiled tutorials and real drawing helpers on Z80 with banked RAM.

Frame timing/ROM entry are mocked here; actual SimCoupe checks are separate.
Uses local external assets only, and compares screen bytes against the original
assembly data, not against the generated C# initializer.
"""
import argparse
import json
import re
import sys
from pathlib import Path

import z80

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "tools"))
from generate_tutorial_assets import TUTORIALS, frame_source


def symbols(output):
    return {name: int(value) for name, value in re.findall(
        r"V(\w+)\s+p\d+\s+I(\d+)", (output / "program.symbols").read_text())}


class Target:
    def __init__(self, output):
        self.labels = symbols(output)
        self.cpu = z80.Z80Machine()
        self.cpu.set_memory_block(0x8000, (output / "program.bin").read_bytes())
        self.banks = {0: bytearray(0x8000), 4: bytearray(0x8000), 6: bytearray(0x8000)}
        self.bank = 0
        self.display = 4
        self.keys = {}
        self.palette = [0] * 16
        self.flips = 0
        self.cpu.set_input_callback(self.read_port)
        self.cpu.set_output_callback(self.write_port)
        self.cpu.memory[self.labels["sam_rom_lmpr"]] = 0x1F
        self.cpu.memory[self.labels["sam_draw_page"]] = 4
        self.cpu.sp = 0xFEFE
        self.cpu.memory[0xFEFE:0xFF00] = bytes((0, 0x70))
        self.cpu.pc = self.labels["cs_main"]
        self.stops = {}
        for name in ("sam_wait_frames", "sam_wait_for_key", "sam_rom_wait_for_key", "sam_trap"):
            if name in self.labels:
                self.stops[self.labels[name]] = name
                self.cpu.set_breakpoint(self.labels[name])
        self.cpu.set_breakpoint(0x7000)

    def read_port(self, port):
        return 255 ^ self.keys.get(port, 0)

    def write_port(self, port, value):
        low = port & 255
        if low == 250:
            assert not self.cpu.iff1, "Interrupts enabled with screen RAM mapped"
            bank = value & 31 if value & 32 else 0
            assert bank in self.banks, (port, value)
            self.banks[self.bank][:] = self.cpu.memory[:0x8000]
            self.cpu.set_memory_block(0, self.banks[bank])
            self.bank = bank
        elif low == 252:
            self.display = value & 30
            self.flips += 1
        elif low == 248:
            self.palette[(port >> 8) & 15] = value

    def frame(self):
        for _ in range(100):
            self.cpu.ticks_to_stop = 1_000_000
            self.cpu.run()
            if self.cpu.pc in self.stops:
                name = self.stops[self.cpu.pc]
                assert name != "sam_trap", f"Native trap {self.cpu.a} at {self.cpu.pc:04X}"
                assert self.bank == 0, "ROM not restored before waiting"
                if name == "sam_wait_frames":
                    assert self.cpu.a == 5
                screen = bytes(self.banks[self.display][:0x6000])
                self.cpu.pc = self.cpu.memory[self.cpu.sp] | self.cpu.memory[self.cpu.sp + 1] << 8
                self.cpu.sp += 2
                return screen
        raise AssertionError(f"Did not finish a frame: PC={self.cpu.pc:04X}")


def expected_frame(data, width, height, offset=0, stripes=False, opaque=False):
    screen = bytearray(0x6000)
    if stripes:
        for row in range(192):
            screen[row * 128:(row + 1) * 128] = bytes([0x22 if row % 2 == 0 else 0x44]) * 128
    for y in range(height):
        for x in range(width):
            dest = offset + y * 128 + x
            index = y * width + x
            if opaque:
                screen[dest] = data[index]
            else:
                screen[dest] = (screen[dest] & data[index * 2]) | data[index * 2 + 1]
    return bytes(screen)


def check(reference):
    count = 0
    for folder, source, limit, compressed, custom in TUTORIALS[2:]:
        path = ROOT / "tutorials" / Path(folder)
        target = Target(path / "out")
        kind, payload = frame_source(reference / "assembly" / Path(source), reference)
        if kind == "opaque":
            expected = expected_frame(payload, 16, 32, opaque=True)
            assert target.frame() == expected, folder
        elif kind == "separate":
            pixels, masks = payload
            data = [value for mask, pixel in zip(masks, pixels) for value in (mask, pixel & ~mask)]
            assert target.frame() == expected_frame(data, 16, 32, stripes="Striped" in folder), folder
        else:
            frames, _ = payload
            width, height = (13, 22) if "Cropped" in folder else (16, 32)
            offset = 641 if width == 13 else 0
            def expect(index):
                actual = target.frame()
                assert actual == expected_frame(frames[index][1], width, height, offset,
                                                stripes="Striped" in folder), (folder, index)
                if custom:
                    assert target.palette[0] == 0, (folder, "background not black")
            if "Blog03" in folder:
                expect(0)
            elif "Blog04" in folder:
                for i in range(20):
                    expect(i % limit)
            else:
                expect(0)
                expect(1)
                # A single key starts the whole non-looping roll; release early.
                target.keys = {0x00FE: 1} if "KeyboardJump" in folder else {0x7FFE: 1}
                if "Blog05/07-1" in folder or "Blog05/07-2" in folder:
                    expect(0)  # Those lessons test input after the idle draw.
                    target.keys = {}
                    expect(0)
                else:
                    expect(0)
                    target.keys = {}
                for i in range(1, 8):
                    expect(i + 1)
                expect(0)
                if "WalkAndJump" in folder or "Blog06" in folder:
                    target.keys = {0xFEFE: 2}
                    for i in range(8):
                        expect(9 + i)
                    target.keys = {0xFEFE: 4}
                    for i in range(7):
                        expect(17 + i)
                    if width == 13:
                        for key, start in ((2, 24), (4, 31)):
                            target.keys = {0x7FFE: 1, 0xFEFE: key}
                            expect(start)
                            target.keys = {}
                            for i in range(1, 7):
                                expect(start + i)
                            expect(0)
                if "ChangeBackground" in folder or width == 13:
                    for bit, colour in enumerate((0, 0x11, 0x22, 0x44, 0x7F)):
                        target.keys = {0xF7FE: 1 << bit}
                        target.frame()
                        assert target.palette[0] == colour
                        assert target.cpu.memory[0x55D8] == colour
                        assert target.cpu.memory[0x55EC] == colour
                    target.keys = {}
                    target.frame()
                    assert target.palette[0] == 0x7F, "Background selection not persistent"
        count += 1
        print(f"PASS native drawing/control: {folder}")
    print(f"PASS {count} sprite tutorials (first two covered by .NET native tests)")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--tools", type=Path, default=ROOT / "tools.local.json")
    args = parser.parse_args()
    settings = json.loads(args.tools.read_text(encoding="utf-8-sig"))
    check(Path(settings["referenceRepo"]))
