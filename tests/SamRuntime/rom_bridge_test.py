"""CPU-only ROM bridge ABI/release regression check; ROM/frames are mocked.

The actual GUI check is separate. In particular, this verifies that an empty
translated buffer during a held key does not satisfy physical release.
"""
import argparse
from pathlib import Path

import z80
from build_runtime_tests import DEFAULT_OUT

REGISTERS = ("a", "bc", "de", "hl", "ix", "iy",
             "alt_a", "alt_bc", "alt_de", "alt_hl")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT)
    output = parser.parse_args().out
    symbols = {label: int(address, 16) for address, label in
               (line.split("=") for line in (output / "rom.map").read_text().splitlines())}
    image = (output / "rom.bin").read_bytes()
    for label in ("sam_rom_clear_screen", "sam_rom_wait_for_key"):
        cpu = z80.Z80Machine()
        cpu.set_memory_block(0x8000, image)
        cpu.set_output_callback(lambda port, value: None)
        def word(address, value):
            cpu.set_memory_block(address, value.to_bytes(2, "little"))
        word(symbols["sam_basic_sp"], 0x6000)
        word(symbols["sam_basic_iy"], 0x5C3A)
        cpu.memory[symbols["sam_rom_lmpr"]] = 0x1F
        # A NOP models completing a frame wait without requiring a CPU
        # interrupt source. Hardware EI/HALT remains untouched in real code.
        for name in ("sam_rom_key_initial", "sam_rom_key_press", "sam_rom_key_release"):
            address = symbols[name]
            assert cpu.memory[address] == 0xFB and cpu.memory[address + 1] == 0x76
            cpu.memory[address + 1] = 0
        for index, name in enumerate(REGISTERS):
            setattr(cpu, name, (0xA5 if name.endswith("a") else 0x1234 + index * 0x111))
        expected = {name: getattr(cpu, name) for name in REGISTERS}
        cpu.sp = 0xFEFE
        word(cpu.sp, 0x7000)
        cpu.pc = symbols[label]
        addresses = (0x014E, 0x0169, symbols["sam_key_state"], 0x7000)
        for address in addresses:
            cpu.set_breakpoint(address)
        reads = keys = clears = 0
        # Initial release, first/second release polls still held, then released.
        physical = iter((True, False, False, True))
        for _ in range(100):
            cpu.ticks_to_stop = 10000
            cpu.run()
            address = cpu.pc
            if address == 0x7000:
                break
            if address not in addresses:
                raise AssertionError(f"Unexpected stop at {address:04X}")
            assert cpu.sp < 0x6000, "ROM/matrix call still on resident stack"
            if address in (0x014E, 0x0169):
                assert cpu.iy == 0x5C3A, "ROM must receive BASIC IY"
            if address == 0x014E:
                assert cpu.a == 0
                clears += 1
                zero = True
            elif address == 0x0169:
                reads += 1
                zero = reads != 2  # Exactly one translated press, then buffer gaps.
            else:
                keys += 1
                zero = next(physical)
            # Deliberately clobber the entire register ABI, like a hostile ROM.
            # IY stays valid between ROM calls as the actual ROM requires it.
            for name in REGISTERS:
                if name != "iy":
                    setattr(cpu, name, 0)
            cpu.f = 0x40 if zero else 0
            cpu.pc = cpu.memory[cpu.sp] | cpu.memory[cpu.sp + 1] << 8
            cpu.sp += 2
        else:
            raise AssertionError("Wrapper did not return")
        assert cpu.sp == 0xFF00
        assert {name: getattr(cpu, name) for name in REGISTERS} == expected
        assert not cpu.iff1
        if label == "sam_rom_wait_for_key":
            assert reads == 5 and keys == 4, (reads, keys)
        else:
            assert clears == 1 and reads == keys == 0
        print(f"PASS {label}: low-stack ROM bridge, register ABI, DI"
              + (" and physical release despite empty ROM buffer" if keys else ""))


if __name__ == "__main__":
    main()
