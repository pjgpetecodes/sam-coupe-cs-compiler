"""Execute full runtime startup with physical 16 KB RAM banks, not cs_main.

ROM far calls are mocked at their entry (including JMODE's destructive clear).
Startup, stack/paging, native clear and buffer selection execute real Z80.
Run: python tests\\SamRuntime\\startup_banked_test.py
"""
import argparse
import re
import subprocess
from pathlib import Path

import z80
from build_runtime_tests import ROOT, load_tools


class StartupMachine:
    def __init__(self, output, screen, image=None):
        self.labels = {name: int(value) for name, value in re.findall(
            r"V(\w+)\s+p\d+\s+I(\d+)", (output / "program.symbols").read_text())}
        self.cpu = z80.Z80Machine()
        self.pages = [bytearray([0xA5]) * 0x4000 for _ in range(16)]
        image = (output / "program.bin").read_bytes() if image is None else image
        for offset in range(0, len(image), 0x4000):
            chunk = image[offset:offset + 0x4000]
            self.pages[1 + offset // 0x4000][:len(chunk)] = chunk
        self.lmpr, self.hmpr, self.vmpr = 0x1F, 1, 0x64
        self.screen = screen
        self.mapping = [None, 0, 1, 2]
        self.remap()
        self.cpu.set_input_callback(self.read_port)
        self.cpu.set_output_callback(self.write_port)
        self.cpu.sp, self.cpu.iy, self.cpu.pc = 0x4F00, 0x5C3A, 0x8000
        self.cpu.iff1 = self.cpu.iff2 = True
        self.cpu.memory[0x4F00:0x4F02] = bytes((0, 0x70))
        self.cpu.memory[0x5A40] = 0
        self.cpu.memory[0x5A78] = self.vmpr
        self.before = [bytes(page) for page in self.pages]
        self.stops = (0x015A, 0x0112, self.labels["cs_main"], self.labels["sam_wait_frames"])
        for address in self.stops:
            self.cpu.set_breakpoint(address)

    def flush(self):
        for window, page in enumerate(self.mapping):
            if page is not None:
                self.pages[page][:] = self.cpu.memory[window * 0x4000:(window + 1) * 0x4000]

    def remap(self):
        self.mapping = [(self.lmpr & 31) if self.lmpr & 0x20 else None,
                        (self.lmpr + 1) & 31, self.hmpr & 31,
                        None if self.lmpr & 0x40 else (self.hmpr + 1) & 31]
        for window, page in enumerate(self.mapping):
            self.cpu.set_memory_block(window * 0x4000,
                                      bytes(0x4000) if page is None else self.pages[page])

    def read_port(self, port):
        return {250: self.lmpr, 251: self.hmpr, 252: self.vmpr}.get(port & 255, 255)

    def write_port(self, port, value):
        low = port & 255
        if low in (250, 251):
            assert not self.cpu.iff1, "Paging with interrupts enabled"
            self.flush()
            if low == 250:
                self.lmpr = value
            else:
                self.hmpr = value
            self.remap()
        elif low == 252:
            self.vmpr = value

    def run_to(self, destination):
        for _ in range(100):
            self.cpu.ticks_to_stop = 1_000_000
            self.cpu.run()
            pc = self.cpu.pc
            if pc == destination:
                self.flush()
                return
            if pc in (0x015A, 0x0112):
                assert 0x4000 <= self.cpu.sp < 0x8000, "ROM called on resident stack"
                assert self.hmpr == 1 and not self.lmpr & 0x60
                if pc == 0x015A:
                    assert self.cpu.a == 3 and self.vmpr == 0x60 | self.screen
                    self.flush()
                    self.pages[self.screen][:] = bytes(0x4000)
                    self.pages[self.screen + 1][:0x2000] = bytes(0x2000)
                    self.remap()
                else:
                    assert self.cpu.a == 2
                self.cpu.pc = self.cpu.memory[self.cpu.sp] | self.cpu.memory[self.cpu.sp + 1] << 8
                self.cpu.sp += 2
        raise AssertionError(f"Startup did not reach {destination:04X}: PC={self.cpu.pc:04X}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--compiled", type=Path,
                        help="Also execute a compiled banked-copy probe through startup and its first wait")
    args = parser.parse_args()
    tools = load_tools(required=("pyz80",))
    output = ROOT / "out" / "verification" / "startup-native"
    output.mkdir(parents=True, exist_ok=True)
    template = (ROOT / "runtime" / "sam" / "hardware.asm").read_text()
    (output / "sam-runtime.asm").write_text(template)
    for screen, asset in ((6, 4), (10, 4), (14, 4), (4, 8)):
        source = f"""cs_initial_screen_page: EQU {screen}
 ORG 0x8000
 DUMP 1,0
 JP sam_start
cs_main:
 LD A,10
 LD B,14
 CALL sam_begin_double_buffer_pages
 CALL sam_clear_screen
 LD A,1
 CALL sam_wait_frames
 RET
 INCLUDE "sam-runtime.asm"
 ORG 2
 DUMP {asset},2
 DEFW 0x6000
 {chr(10).join("DEFB " + ",".join(["0x55"] * 128) for _ in range(192))}
"""
        (output / "program.asm").write_text(source)
        subprocess.run([tools["pyz80"], "--nozip", "--obj=program.bin",
                        "--exportfile=program.symbols", "program.asm"],
                       cwd=output, check=True, capture_output=True, text=True)
        machine = StartupMachine(output, screen)
        machine.run_to(machine.labels["cs_main"])
        assert machine.cpu.sp == 0xFEFE and machine.hmpr == 1 and machine.lmpr == 0x1F
        assert not machine.cpu.iff1
        for page in (asset, asset + 1):
            assert bytes(machine.pages[page]) == machine.before[page], \
                f"Startup erased asset page {page} using screen {screen}"
        assert bytes(machine.pages[screen]) == bytes(0x4000)
        assert bytes(machine.pages[screen + 1][:0x2000]) == bytes(0x2000)
        assert machine.cpu.memory[0xFF00] == 0
        machine.cpu.clear_breakpoint(machine.labels["cs_main"])
        machine.run_to(machine.labels["sam_wait_frames"])
        assert machine.cpu.memory[machine.labels["sam_draw_page"]] == 14
        assert machine.vmpr == 0x6A and machine.lmpr == 0x1F
        for page in (asset, asset + 1):
            assert bytes(machine.pages[page]) == machine.before[page], \
                "Clear after buffer selection overwrote banked assets"
        print(f"PASS full runtime startup: screen {screen}/{screen+1}, asset {asset}/{asset+1}, "
              "ROM low stack, resident stack, hidden clear and ROM restored before wait")

    # Prove this oracle catches the original bug, rather than starting at cs_main.
    legacy = template.replace("LD      A,cs_initial_screen_page", "LD      A,4")
    negative = output / "negative"
    negative.mkdir(exist_ok=True)
    (negative / "sam-runtime.asm").write_text(legacy)
    (negative / "program.asm").write_text(source.replace("DUMP 8,2", "DUMP 4,2"))
    subprocess.run([tools["pyz80"], "--nozip", "--obj=program.bin",
                    "--exportfile=program.symbols", "program.asm"],
                   cwd=negative, check=True, capture_output=True, text=True)
    machine = StartupMachine(negative, 4)
    machine.run_to(machine.labels["cs_main"])
    assert machine.before[4][2:6] == bytes((0, 0x60, 0x55, 0x55))
    assert machine.pages[4][2:6] == bytes(4), "Legacy startup must reproduce lost header/payload"
    print("PASS negative control: original startup destroys page4 length 0060 and payload 5555")
    (output / "sam-runtime.asm").write_text(template)
    if args.compiled:
        assembly = (args.compiled / "program.asm").read_text()
        screen = int(re.search(r"cs_initial_screen_page:\s+EQU (\d+)", assembly)[1])
        machine = StartupMachine(args.compiled, screen)
        machine.run_to(machine.labels["cs_main"])
        asset_pages = [int(page) for page in re.findall(r"DUMP (\d+),2", assembly)]
        for asset in asset_pages:
            assert machine.pages[asset] == machine.before[asset]
        machine.cpu.clear_breakpoint(machine.labels["cs_main"])
        machine.run_to(machine.labels["sam_wait_frames"])
        for asset in asset_pages:
            assert machine.pages[asset] == machine.before[asset]
        display = machine.vmpr & 30
        assert bytes(machine.pages[display]) == bytes([0x55]) * 0x4000
        assert bytes(machine.pages[display + 1][:0x2000]) == bytes([0x55]) * 0x2000
        print("PASS compiled C# page4 probe: entry, banked copy, flip and complete 24 KB payload")


if __name__ == "__main__":
    main()
