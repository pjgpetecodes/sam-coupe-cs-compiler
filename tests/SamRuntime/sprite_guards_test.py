"""Focused Z80 execution of sprite, decoder, key and palette regressions.

Requires build_runtime_tests.py first; synthetic bytes are authored here.
"""
import argparse
from pathlib import Path
import z80
from build_runtime_tests import DEFAULT_OUT


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, default=DEFAULT_OUT)
    out = parser.parse_args().out
    labels = {label: int(address, 16) for address, label in
              (line.split("=") for line in (out / "smoke.map").read_text().splitlines())}
    image = (out / "smoke.bin").read_bytes()

    def cpu():
        target = z80.Z80Machine()
        target.set_memory_block(0x8000, image)
        target.sp = 0xFEFE
        target.memory[0xFEFE:0xFF00] = b"\x00\x70"
        target.set_breakpoint(0x7000)
        target.set_breakpoint(labels["sam_trap"])
        target.memory[labels["sam_rom_lmpr"]] = 0x1F
        target.set_output_callback(lambda port, value: None)
        return target

    def run(target, name, trap=None):
        target.pc = labels[name]
        cycles = 0
        for _ in range(20):
            target.ticks_to_stop = 100000
            target.run()
            cycles += 100000 - target.ticks_to_stop
            if target.pc == labels["sam_trap"]:
                assert trap is not None and target.a == trap, (name, target.a)
                return cycles
            if target.pc == 0x7000:
                assert trap is None, name
                return cycles
        raise AssertionError(f"{name} did not return/trap")

    for mask, state, expected in ((31, 254, 1), (31, 255, 0), (2, 253, 1), (2, 254, 0)):
        target = cpu()
        target.a = mask
        target.bc = 0xFEFE
        target.set_input_callback(lambda port, state=state: state)
        run(target, "sam_is_key_pressed")
        assert target.a == expected and target.bc == 0xFEFE
    print("PASS active-low single/any-key masks")

    target = cpu()
    target.a = 0
    run(target, "sam_wait_frames")
    assert not target.halted and not target.iff1
    print("PASS zero frame wait returns without a 256-frame wrap")

    for offset, width, height in ((0, 0, 32), (0, 16, 0), (0, 17, 32),
                                  (0, 16, 33), (120, 16, 32), (0x5F80, 16, 2),
                                  (0xFFF0, 16, 1)):
        target = cpu()
        target.hl = offset
        target.b, target.c = width, height
        run(target, "sam_validate_sprite_rectangle", 7)
    target = cpu()
    target.hl, target.b, target.c = 0x5FF0, 16, 1
    run(target, "sam_validate_sprite_rectangle")
    print("PASS sprite rectangle bounds and last legal screen byte")
    target = cpu()
    target.set_memory_block(0, bytes([0x44]) * 0x6000)
    target.hl, target.b, target.c = 1, 2, 2
    run(target, "sam_clear_sprite")
    assert list(target.memory[:4]) == [0x44, 0, 0, 0x44]
    assert list(target.memory[128:132]) == [0x44, 0, 0, 0x44]
    assert target.memory[256] == 0x44
    print("PASS rectangle clear preserves neighboring background bytes")

    target = cpu()
    source = labels["sam_runtime_end"] + 0x100
    destination = source + 0x300
    target.memory[source:source + 2] = (573).to_bytes(2, "little")
    target.memory[source + 2] = 1
    target.memory[source + 3] = 0x12
    target.memory[source + 4] = 0x34
    target.memory[destination:destination + 2] = (617).to_bytes(2, "little")
    target.memory[destination + 2] = 1
    target.memory[labels["sam_shift_source"]:labels["sam_shift_source"] + 2] = source.to_bytes(2, "little")
    target.memory[labels["sam_shift_destination"]:labels["sam_shift_destination"] + 2] = destination.to_bytes(2, "little")
    cycles = run(target, "sam_shift_masked_sprite_one_pixel")
    shifted = bytes(target.memory[destination + 3:destination + 3 + 616])
    expected = bytearray()
    for row in range(22):
        source_row = [0] * 26
        if row == 0:
            source_row[:2] = [0x12, 0x34]
        mask_pixel_pairs = [(source_row[index], source_row[index + 1])
                            for index in range(0, 26, 2)]
        masks = [pair[0] for pair in mask_pixel_pairs]
        pixels = [pair[1] for pair in mask_pixel_pairs]
        row_bytes = [0xF0 | (masks[0] >> 4), pixels[0] >> 4]
        for index in range(1, 13):
            row_bytes.extend((((masks[index - 1] << 4) | (masks[index] >> 4)) & 0xFF,
                              ((pixels[index - 1] << 4) | (pixels[index] >> 4)) & 0xFF))
        row_bytes.extend((((masks[12] << 4) | 0x0F) & 0xFF, (pixels[12] << 4) & 0xFF))
        expected.extend(row_bytes)
    assert shifted == bytes(expected)
    print(f"PASS one-pixel C# frame shift (616 bytes, {cycles} Z80 T-states)")

    target = cpu()
    alias = labels["sam_runtime_end"] + 0x100
    target.memory[alias:alias + 2] = (617).to_bytes(2, "little")
    target.memory[alias + 2] = 1
    target.memory[labels["sam_shift_source"]:labels["sam_shift_source"] + 2] = alias.to_bytes(2, "little")
    target.memory[labels["sam_shift_destination"]:labels["sam_shift_destination"] + 2] = alias.to_bytes(2, "little")
    run(target, "sam_shift_masked_sprite_one_pixel", 8)
    print("PASS one-pixel shift rejects overlapping input/output arrays")

    for source_offset, destination_offset in ((2, 0), (0, 2)):
        target = cpu()
        source, destination = 0x9000, 0x9100
        target.memory[source:source + 3] = b"\x01\x00\x55"
        target.memory[destination:destination + 3] = b"\x01\x00\xAA"
        for name, value in (("sam_copy_source", source), ("sam_copy_destination", destination),
                            ("sam_copy_source_offset", source_offset),
                            ("sam_copy_destination_offset", destination_offset),
                            ("sam_copy_count", 1)):
            address = labels[name]
            target.memory[address:address + 2] = value.to_bytes(2, "little")
        run(target, "sam_copy_bytes", 8)
        assert target.memory[destination + 2] == 0xAA
    print("PASS source/destination array-copy bounds report stable fault 08 before writing")

    for data in (b"\x01\x00", b"\xFF\xFF"):
        target = cpu()
        target.set_memory_block(0x9000, data)
        target.hl, target.bc = 0x9002, 512
        run(target, "sam_validate_data", 7)
    print("PASS truncated and wrapping resident data spans")

    # Literal and repeated packets fill alternating mask/pixel positions.
    target = cpu()
    stream = bytes((0x83, 1, 2, 3, 3, 4, 0))
    target.set_memory_block(0x9000, stream)
    target.memory[labels["sam_rle_remaining"]:labels["sam_rle_remaining"] + 2] = b"\x06\x00"
    target.memory[labels["sam_rle_end"]:labels["sam_rle_end"] + 2] = (0x9000 + len(stream)).to_bytes(2, "little")
    target.memory[labels["sam_sprite_data_pointer"]:labels["sam_sprite_data_pointer"] + 2] = b"\x00\x90"
    target.hl, target.ix = 0x9000, 0x7000
    # Return marker cannot overlap destination.
    target.memory[0xFEFE:0xFF00] = b"\x00\x71"
    target.set_breakpoint(0x7100)
    target.pc = labels["sam_unpack_rle_plane"]
    target.ticks_to_stop = 100000
    target.run()
    assert target.pc == 0x7100
    assert list(target.memory[0x7000:0x700C:2]) == [1, 2, 3, 4, 4, 4]
    for remaining, stream in ((2, b"\x03\xFF\x00"), (3, b"\x02\xFF\x00"), (2, b"\x82\xAA"),
                              (256, b"\x80" + bytes([0xAA]) * 256 + b"\x00")):
        target = cpu()
        target.set_memory_block(0x9000, stream)
        target.memory[labels["sam_rle_remaining"]:labels["sam_rle_remaining"] + 2] = remaining.to_bytes(2, "little")
        target.memory[labels["sam_rle_end"]:labels["sam_rle_end"] + 2] = (0x9000 + len(stream)).to_bytes(2, "little")
        target.memory[labels["sam_sprite_data_pointer"]:labels["sam_sprite_data_pointer"] + 2] = b"\x00\x90"
        target.hl, target.ix = 0x9000, 0x7200
        run(target, "sam_unpack_rle_plane", 7)
    print("PASS literal/repeat decoding and overlong/short/truncated packet traps")

    target = cpu()
    # Reproduce the black/cyan fault at the interrupt's palette source:
    # old helpers only wrote CLUT; the ROM's flash phase still held cyan.
    target.memory[0x55D8] = target.memory[0x55EC] = 0x5D
    outputs = []
    target.set_output_callback(lambda port, value: outputs.append((port, value)))
    run(target, "sam_palette")
    assert target.memory[0x55D8] == target.memory[0x55EC] == 0
    assert len(outputs) == 16 and outputs[0] == (248, 0)
    target.sp = 0xFEFE
    target.a, target.b = 0, 0x5D
    run(target, "sam_set_palette_entry")
    assert target.memory[0x55D8] == target.memory[0x55EC] == 0x5D
    assert outputs[-16] == (248, 0x5D)
    print("PASS black/cyan reproduction: both ROM palette phases and retained CLUT agree")


if __name__ == "__main__":
    main()
