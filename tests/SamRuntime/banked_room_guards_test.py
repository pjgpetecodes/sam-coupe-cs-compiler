"""Execute checked banked row copying, room decoding and collision kernels.

Run build_runtime_tests.py first. All masks and packet streams are synthetic.
"""
import argparse
from pathlib import Path

import z80
from build_runtime_tests import DEFAULT_OUT


class Target:
    def __init__(self, image, labels, interrupts=False):
        self.labels = labels
        self.cpu = z80.Z80Machine()
        self.cpu.set_memory_block(0x8000, image)
        self.banks = {page: bytearray(0x8000) for page in (0, 6, 8, 10, 12)}
        self.bank = 0
        self.cpu.iff1 = self.cpu.iff2 = interrupts
        self.cpu.set_output_callback(self.write_port)
        self.byte("sam_rom_lmpr", 0x1F)
        self.byte("sam_draw_page", 10)

    def byte(self, name, value):
        self.cpu.memory[self.labels[name]] = value

    def word(self, name, value):
        address = self.labels[name]
        self.cpu.memory[address:address + 2] = value.to_bytes(2, "little")

    def array(self, page, data):
        self.banks[page][2:4] = len(data).to_bytes(2, "little")
        self.banks[page][4:4 + len(data)] = data

    def write_port(self, port, value):
        if port & 255 == 250:
            assert not self.cpu.iff1, "Interrupts enabled with lower RAM mapped"
            self.banks[self.bank][:] = self.cpu.memory[:0x8000]
            self.bank = value & 31 if value & 32 else 0
            self.cpu.set_memory_block(0, self.banks[self.bank])

    def run(self, name, fault=None):
        self.cpu.sp = 0xFEFE
        self.cpu.memory[0xFEFE:0xFF00] = b"\x00\x70"
        self.cpu.set_breakpoint(0x7000)
        self.cpu.set_breakpoint(self.labels["sam_trap"])
        self.cpu.pc = self.labels[name]
        for _ in range(30):
            self.cpu.ticks_to_stop = 1_000_000
            self.cpu.run()
            if self.cpu.pc == self.labels["sam_trap"]:
                assert fault is not None and self.cpu.a == fault, (name, self.cpu.a, fault)
                return
            if self.cpu.pc == 0x7000:
                assert fault is None, (name, "Expected trap", fault)
                return
        raise AssertionError(f"{name} did not return: {self.cpu.pc:04X}")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT)
    out = parser.parse_args().out
    labels = {label: int(address, 16) for address, label in
              (line.split("=") for line in (out / "smoke.map").read_text().splitlines())}
    image = (out / "smoke.bin").read_bytes()

    def copier(source_offset, screen_offset, width, rows, length=0x6000, interrupts=False):
        t = Target(image, labels, interrupts)
        data = bytes((i * 13 + i // 128) & 255 for i in range(length))
        t.array(6, data)
        t.banks[10][:] = bytes([0xA5]) * 0x8000
        for name, value in (("sam_banked_screen_source", 2),
                            ("sam_banked_screen_source_offset", source_offset),
                            ("sam_banked_screen_offset", screen_offset)):
            t.word(name, value)
        for name, value in (("sam_banked_screen_page", 6),
                            ("sam_banked_screen_width", width),
                            ("sam_banked_screen_rows", rows)):
            t.byte(name, value)
        return t, data

    for source_offset, screen_offset, width, rows in (
        (0, 0, 128, 192), (0x5FFF, 0x5FFF, 1, 1), (17, 259, 14, 22),
        (0, 0, 0, 2), (0, 0, 128, 0),
    ):
        for interrupts in (False, True):
            t, data = copier(source_offset, screen_offset, width, rows, interrupts=interrupts)
            before_source = bytes(t.banks[6])
            expected = bytearray(t.banks[10])
            for row in range(rows):
                src, dst = source_offset + row * 128, screen_offset + row * 128
                expected[dst:dst + width] = data[src:src + width]
            registers = (0x1234, 0x5678, 0x9ABC, 0xDEF0)
            t.cpu.af, t.cpu.bc, t.cpu.de, t.cpu.hl = registers
            t.run("sam_copy_banked_rows_to_screen")
            assert t.banks[10] == expected
            assert bytes(t.banks[6]) == before_source
            assert t.bank == 0 and t.cpu.iff1 == interrupts
            assert (t.cpu.af, t.cpu.bc, t.cpu.de, t.cpu.hl) == registers
    for source_offset, screen_offset, width, rows, length, fault in (
        (0, 127, 2, 1, 0x6000, 13),
        (0, 0x5F80, 128, 2, 0x6000, 13),
        (0, 0, 129, 1, 0x6000, 13),
        (0, 0, 2, 2, 2, 8),
        (0xFFFF, 0, 1, 2, 0x6000, 8),
    ):
        t, _ = copier(source_offset, screen_offset, width, rows, length)
        before = bytes(t.banks[10])
        t.run("sam_copy_banked_rows_to_screen", fault)
        assert bytes(t.banks[10]) == before
    print("PASS checked banked rectangles: full screen, edges, IFF/registers, no writes on invalid spans")

    def decoder(stream):
        t = Target(image, labels)
        t.array(8, stream)
        t.array(6, bytes([0xA5]) * 0x6000)
        t.word("sam_decode_source", 2)
        t.word("sam_decode_destination", 2)
        t.byte("sam_decode_source_page", 8)
        t.byte("sam_decode_destination_page", 6)
        return t

    expected = bytes((i * 37) & 255 for i in range(0x6000))
    packets = bytearray()
    for start in range(0, len(expected), 127):
        chunk = expected[start:start + 127]
        packets.extend((128 | len(chunk),))
        packets.extend(chunk)
    packets.append(0)
    t = decoder(packets)
    t.run("sam_decode_rle_background")
    assert bytes(t.banks[6][4:0x6004]) == expected and t.bank == 0
    packets = bytes((1, 0x42, 127, 0x33)) * 192 + b"\x00"
    t = decoder(packets)
    t.run("sam_decode_rle_background")
    assert bytes(t.banks[6][4:0x6004]) == (b"\x42" + b"\x33" * 127) * 192
    for stream in (b"\x83\x01\x02", b"\x80", b"\x01", b"\x00",
                   bytes((127, 0x55)) * 194 + b"\x00"):
        t = decoder(stream)
        t.run("sam_decode_rle_background", 14)
    for stream, fault in (
        (b"\x01\x00\x42\xff\x5f\x33\x00\x00", None),
        (b"\x01\x00", 14), (b"\x00\x00", 14), (b"\xff\xff\x42\x00\x00", 14),
    ):
        t = decoder(stream)
        t.run("sam_expand_byte_runs", fault)
        if fault is None:
            assert bytes(t.banks[6][4:0x6004]) == b"\x42" + b"\x33" * 0x5FFF
    print("PASS bulk room packets: exact literals/runs, count-one cases and truncation/capacity traps")

    def collision(mask):
        t = Target(image, labels)
        t.array(12, mask)
        t.bank = 12
        t.cpu.set_memory_block(0, t.banks[12])
        return t

    for x in (0, 1, 254, 255):
        mask = bytearray(0x6000)
        mask[191 * 128 + x // 2] = 0x0F if x % 2 == 0 else 0xF0
        t = collision(mask)
        t.cpu.hl, t.cpu.b, t.cpu.d, t.cpu.c = 2, x, 175, 17
        t.run("sam_any_solid_pixel_column")
        assert t.cpu.a == 0, "Neighbouring nibble must not collide"
        mask[191 * 128 + x // 2] = 0xFF
        t = collision(mask)
        t.cpu.hl, t.cpu.b, t.cpu.d, t.cpu.c = 2, x, 175, 17
        t.run("sam_any_solid_pixel_column")
        assert t.cpu.a == 1
    for y, rows in ((192, 1), (250, 17)):
        t = collision(bytes(0x6000))
        t.cpu.hl, t.cpu.b, t.cpu.d, t.cpu.c = 2, 0, y, rows
        t.run("sam_any_solid_pixel_column", 14)
    t = collision(bytes(127))
    t.cpu.hl, t.cpu.b, t.cpu.d, t.cpu.c = 2, 0, 0, 1
    t.run("sam_any_solid_pixel_column", 14)
    t = collision(bytes(0x6000))
    t.cpu.hl, t.cpu.b, t.cpu.d, t.cpu.c = 0, 0, 0, 1
    t.run("sam_any_solid_pixel_column", 1)
    for parity in (0, 1):
        mask = bytearray(0x6000)
        for solid, expected_result in ((False, 0), (True, 1)):
            mask[128 + 16 * 128 + 4] = 0xF0 if solid else 0
            t = collision(mask)
            t.word("sam_packed_rectangle_array", 2)
            t.word("sam_packed_rectangle_offset", 128)
            t.byte("sam_packed_rectangle_parity", parity)
            t.byte("sam_packed_rectangle_width", 10)
            t.byte("sam_packed_rectangle_rows", 17)
            t.run("sam_any_solid_pixel_rectangle")
            assert t.cpu.a == expected_result, "Interior final-row pixel must be checked"
    for parity in (0, 1):
        mask = bytearray(0x6000)
        # The high nibble is outside an odd-start rectangle, the low outside an
        # even-start three-pixel rectangle. Neither should produce a collision.
        mask[128] = 0xF0 if parity else 0
        mask[129] = 0 if parity else 0x0F
        t = collision(mask)
        t.word("sam_packed_rectangle_array", 2)
        t.word("sam_packed_rectangle_offset", 128)
        t.byte("sam_packed_rectangle_parity", parity)
        t.byte("sam_packed_rectangle_width", 3)
        t.byte("sam_packed_rectangle_rows", 2)
        t.run("sam_any_solid_pixel_rectangle")
        assert t.cpu.a == 0
    for length, offset, parity, width in ((127, 0, 1, 255), (0x6000, 0xFFFF, 0, 2)):
        t = collision(bytes(length))
        t.cpu.hl, t.cpu.de, t.cpu.b, t.cpu.c = 2, offset, parity, width
        t.run("sam_any_solid_pixel_row", 14)
    print("PASS packed columns/rectangles: both nibbles, interior rows and widened span-overflow guards")


if __name__ == "__main__":
    main()
