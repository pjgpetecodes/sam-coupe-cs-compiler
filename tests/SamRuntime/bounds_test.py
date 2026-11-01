"""Execute runtime guard instructions on a Z80 CPU (not a SAM hardware model).

Build smoke.bin/smoke.map first. Requires the locally installed `z80` package.
Hardware/ROM behavior is tested separately with verify_simcoupe.py.
"""
import argparse
from pathlib import Path

import z80
from build_runtime_tests import DEFAULT_OUT

SYMBOLS = {}
IMAGE = b""


def guard(label, hl, code, data=None):
    cpu = z80.Z80Machine()
    cpu.set_memory_block(0x8000, IMAGE)
    if data:
        cpu.set_memory_block(hl, data)
    cpu.pc = SYMBOLS[label]
    cpu.hl = hl
    cpu.a = 0xA5
    cpu.sp = 0xFEF0
    cpu.set_breakpoint(SYMBOLS["sam_trap"])
    cpu.ticks_to_stop = 10000
    for _ in range(10):
        cpu.run()
        if cpu.pc == SYMBOLS["sam_trap"]:
            assert cpu.a == code, (label, hex(hl), cpu.a, code)
            print(f"PASS {label} HL={hl:04X} -> trap {code:02X}")
            return
    raise AssertionError(f"Guard failed to trap: {label} HL={hl:04X}")


def main():
    global SYMBOLS, IMAGE
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT)
    args = parser.parse_args()
    output = args.out.resolve()
    SYMBOLS = {label: int(address, 16) for address, label in
               (line.split("=") for line in
                (output / "smoke.map").read_text().splitlines())}
    IMAGE = (output / "smoke.bin").read_bytes()
    for offset in (0x6000, 0x7FFF, 0x8000, 0xFFFF):
        guard("sam_write_screen_byte", offset, 5)
    for char in (0x0080, 0x00FF, 0x0100, 0xFFFF):
        guard("sam_print_char", char, 6)
    guard("sam_print", 0, 1)
    for pointer in (0x7FFF, 0xDFFF, 0xE000, 0xFFFF):
        guard("sam_print", pointer, 7)
    guard("sam_print", 0x9000, 7, b"\xff\xff")
    guard("sam_print", 0x9000, 7, b"\x00\x60")
    guard("sam_print", 0x9000, 6, b"\x01\x00\x80")


if __name__ == "__main__":
    main()
