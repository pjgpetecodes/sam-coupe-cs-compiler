"""Verify prepared native rendering and the assembly-style Blog6 refresh loop.

Build the compiler and Blog06/08-3-AssemblyStyle first. This test builds the small
synthetic program and stages the reference assembly; its checkout is read-only.
"""
import json
import re
import shutil
import subprocess
from pathlib import Path

import z80
from build_runtime_tests import ROOT
from tutorial_test import Target, expected_frame

import sys
sys.path.insert(0, str(ROOT / "tools"))
from generate_tutorial_assets import frame_source


def call(target, name, fault=None):
    cpu = target.cpu
    cpu.sp = 0xFEFE
    cpu.memory[0xFEFE:0xFF00] = b"\x00\x70"
    cpu.pc = target.labels[name]
    ticks = 0
    for _ in range(50):
        cpu.ticks_to_stop = 100_000
        cpu.run()
        ticks += 100_000 - cpu.ticks_to_stop
        if cpu.pc == target.labels["sam_trap"]:
            assert fault is not None and cpu.a == fault, (name, cpu.a, fault)
            return ticks
        if cpu.pc == 0x7000:
            assert fault is None, (name, fault)
            return ticks
    raise AssertionError(f"{name} did not return")


def verify_synthetic():
    folder = ROOT / "tests" / "SamRuntime" / "out" / "prepared"
    folder.mkdir(parents=True, exist_ok=True)
    source = folder / "Program.cs"
    source.write_text("""
using SamCoupe.Sdk;
static class Program
{
    static readonly byte[] Frames = { 2, 0xF0, 1, 0x0F, 0x20, 0xF0, 3, 0x0F, 0x40,
                                        0xF0, 5, 0x0F, 0x60, 0xF0, 7, 0x0F, 0x80 };
    static readonly byte[] Last = { 1, 0xF0, 9 };
    static void Main()
    {
        Sam.PrepareFixedSpriteRenderer(Frames, 129, 2, 2);
        Sam.DrawPreparedSprite(0);
        Sam.PrepareFixedSpriteRenderer(Last, 0x5FFF, 1, 1);
        Sam.DrawPreparedSprite(0);
    }
}
""", encoding="ascii")
    cli = ROOT / "src" / "SamCoupe.Compiler.Cli" / "bin" / "Debug" / "net10.0" / "SamCoupe.Compiler.Cli.dll"
    subprocess.run(["dotnet", str(cli), str(source), "--build",
                    "--tools", str(ROOT / "tools.local.json")], check=True, cwd=ROOT)
    output = folder / "out"

    def target():
        t = Target(output)
        t.banks[4][:0x6000] = b"\xAA" * 0x6000
        return t

    for interrupts in (False, True):
        t = target()
        t.cpu.iff1 = t.cpu.iff2 = interrupts
        registers = (0x1234, 0x5678, 0x9ABC, 0xDEF0)
        t.cpu.af, t.cpu.bc, t.cpu.de, t.cpu.hl = registers
        call(t, "cs_prepared_001_prepare")
        assert t.cpu.iff1 == t.cpu.iff2 == interrupts
        assert (t.cpu.af, t.cpu.bc, t.cpu.de, t.cpu.hl) == registers
        t.cpu.a = 1
        before = (t.cpu.af, t.cpu.bc, t.cpu.de, t.cpu.hl)
        call(t, "cs_draw_prepared_sprite")
        expected = bytearray(b"\xAA" * 0x6000)
        expected[129:131] = b"\x05\x60"
        expected[257:259] = b"\x07\x80"
        assert t.banks[4][:0x6000] == expected
        assert (t.cpu.af, t.cpu.bc, t.cpu.de, t.cpu.hl) == before
        assert t.cpu.iff1 == t.cpu.iff2 == interrupts
        assert t.bank == 0
        call(t, "cs_prepared_002_prepare")
        t.cpu.a = 0
        call(t, "cs_draw_prepared_sprite")
        expected[0x5FFF] = 9
        assert t.banks[4][:0x6000] == expected

    for index, prepare in ((0, False), (2, True), (255, True)):
        t = target()
        if prepare:
            call(t, "cs_prepared_001_prepare")
        t.cpu.a = index
        call(t, "cs_draw_prepared_sprite", 7)
        assert t.banks[4][:0x6000] == b"\xAA" * 0x6000

    for header_offset, replacement in ((0, 1), (-2, 1)):
        t = target()
        call(t, "cs_prepared_002_prepare")
        kernel = bytes(t.cpu.memory[t.labels["cs_prepared_kernel"]:t.labels["cs_prepared_kernel"] + 2])
        payload = t.labels["cs_data_001_payload"]
        t.cpu.memory[payload + header_offset] = replacement
        call(t, "cs_prepared_001_prepare", 7)
        assert bytes(t.cpu.memory[t.labels["cs_prepared_kernel"]:t.labels["cs_prepared_kernel"] + 2]) == kernel
        assert t.banks[4][:0x6000] == b"\xAA" * 0x6000
    print("PASS prepared sprites: exact clear/draw, last screen byte, reconfiguration, guards, registers and IFF")


class Original:
    """Run the actual reference update code; mock only ROM clear and HALT."""
    def __init__(self, output):
        self.labels = {name: int(value) for name, value in re.findall(
            r"V(\w+)\s+p\d+\s+I(\d+)", (output / "original.symbols").read_text())}
        self.cpu = z80.Z80Machine()
        self.cpu.set_memory_block(0x8000, (output / "original.bin").read_bytes())
        self.banks = {page: bytearray(0x8000) for page in (-1, 0, 4)}
        self.bank = -1
        self.display = 4
        self.keys = {}
        self.cpu.set_output_callback(self.write_port)
        self.cpu.set_input_callback(lambda port: 255 ^ self.keys.get(port, 0))
        self.cpu.memory[self.labels["Display_Page"]] = 4
        self.cpu.hl = self.labels["Idle_Animation"]
        self.cpu.sp = 0xFEFE
        self.cpu.memory[0xFEFE:0xFF00] = self.labels["Animation_Loop"].to_bytes(2, "little")
        self.cpu.pc = self.labels["Set_Animation"]
        self.cpu.set_breakpoint(self.labels["Animation_Loop"])
        self.run()

    def write_port(self, port, value):
        if port & 255 == 250:
            assert not self.cpu.iff1
            self.banks[self.bank][:] = self.cpu.memory[:0x8000]
            self.bank = value & 31 if value & 32 else -1
            self.cpu.set_memory_block(0, self.banks[self.bank])
        elif port & 255 == 252:
            self.display = value & 30

    def run(self):
        for _ in range(50):
            self.cpu.ticks_to_stop = 100_000
            self.cpu.run()
            if self.cpu.pc == self.labels["Animation_Loop"]:
                assert self.bank == -1
                return bytes(self.banks[self.display][:0x6000])
        raise AssertionError("Original did not finish update")

    def tick(self, keys):
        self.keys = keys
        self.cpu.pc += 1  # HALT's ROM interrupt is outside the CPU-only comparison.
        return self.run()


def wait(target):
    ticks = 0
    for _ in range(100):
        target.cpu.ticks_to_stop = 100_000
        target.cpu.run()
        ticks += 100_000 - target.cpu.ticks_to_stop
        if target.cpu.pc in target.stops:
            assert target.stops[target.cpu.pc] == "sam_wait_frames", target.cpu.a
            assert target.cpu.a == 1
            assert target.bank == 0
            return ticks
    raise AssertionError("Prepared tutorial did not reach next refresh")


def verify_pilot(reference):
    output = ROOT / "tutorials" / "Blog06" / "08-3-AssemblyStyle" / "out"
    t = Target(output)
    wait(t)
    assert not any(t.banks[t.display][:0x6000]), "Initial sprite must still be hidden"
    original_output = output / "comparison"
    original_output.mkdir(parents=True, exist_ok=True)
    source = reference / "assembly" / "blog6" / "8-3.asm"
    text = re.sub(r'(?im)(\bINCLUDE\s+)["\']([^"\']+)["\']',
                  lambda m: m[1] + '"' + str(source.parent / m[2]) + '"',
                  source.read_text(encoding="utf-8"))
    (original_output / "original.asm").write_text(text, encoding="utf-8")
    tools = json.loads((ROOT / "tools.local.json").read_text(encoding="utf-8-sig"))
    executable = tools["pyz80"]
    if not shutil.which(executable):
        executable = str((ROOT / executable).resolve())
    subprocess.run([executable, "--nozip", "--obj=original.bin",
                    "--mapfile=original.map", "--exportfile=original.symbols",
                    "original.asm"], cwd=original_output, check=True)
    original = Original(original_output)
    _, payload = frame_source(reference / "assembly" / "blog6" / "8-3.asm", reference)
    frames, _ = payload
    expected = {expected_frame(data, 13, 22, 641) for _, data in frames}
    schedule = [({}, 12), ({0xFEFE: 2}, 45), ({0xFEFE: 4}, 40),
                ({0x7FFE: 1}, 2), ({}, 43),
                ({0x7FFE: 1, 0xFEFE: 2}, 2), ({}, 38),
                ({0x7FFE: 1, 0xFEFE: 4}, 2), ({}, 38),
                ({0x7FFE: 1, 0xFEFE: 6}, 82), ({}, 40)]
    costs = []
    seen = set()
    last_flip = t.flips
    for keys, count in schedule:
        for _ in range(count):
            t.keys = keys
            cpu = t.cpu
            cpu.pc = cpu.memory[cpu.sp] | cpu.memory[cpu.sp + 1] << 8
            cpu.sp += 2
            costs.append(wait(t))
            actual = bytes(t.banks[t.display][:0x6000])
            assert actual == original.tick(keys), ("Per-refresh scene differs from reference", keys, len(costs))
            assert actual in expected
            seen.add(actual)
            # At most one completed page is presented per refresh.
            assert t.flips - last_flip <= 1
            last_flip = t.flips
    assert len(seen) == len(expected), (len(seen), len(expected))
    for bit, colour in enumerate((0, 0x11, 0x22, 0x44, 0x7F)):
        for keys in ({0xF7FE: 1 << bit}, {}):
            t.keys = keys
            cpu = t.cpu
            cpu.pc = cpu.memory[cpu.sp] | cpu.memory[cpu.sp + 1] << 8
            cpu.sp += 2
            wait(t)
            assert t.palette[0] == colour
            assert cpu.memory[0x55D8] == cpu.memory[0x55EC] == colour
    assert t.labels["sam_cli_image_end"] < 0xE000
    runtime = (output / "sam-runtime.asm").read_text()
    assert "sam_interleave_buffer:" not in runtime and "sam_unpack_rle_plane:" not in runtime
    print(f"PASS assembly-style Blog6: {len(costs)} exact reference refreshes, all {len(seen)} poses, persistent colours")
    print(f"CPU work: mean {sum(costs) // len(costs)} T-states/tick; max {max(costs)}; resident end {t.labels['sam_cli_image_end']:04X}")


if __name__ == "__main__":
    verify_synthetic()
    settings = json.loads((ROOT / "tools.local.json").read_text(encoding="utf-8-sig"))
    verify_pilot(Path(settings["referenceRepo"]))
