"""Verify Blog 22–28 rendering, state transitions, actors, and sound writes."""

from __future__ import annotations

import json
import re
import sys
from pathlib import Path

import z80

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
import verify_tutorial_scene as scenes
from generate_tutorial_assets import render_bee_frames, render_water_frames


def verify_four_screen_route() -> None:
    machine = scenes.PagedMachine(
        ROOT / "tutorials" / "Blog21" / "23-1-FourScreenRoute" / "out"
    )
    machine.frame()
    room = machine.symbols["cs_v_001_0001"]
    player_x = machine.symbols["cs_v_002_0002"]
    player_y = machine.symbols["cs_v_003_0003"]
    grounded = machine.symbols["cs_v_004_0004"]
    # Seed room 3's grounded left boundary, then exercise the real transition
    # code and matching room-4 collision/background loaders.
    machine.cpu.memory[room] = 3
    machine.cpu.memory[player_x] = 0
    machine.cpu.memory[player_y] = 155
    machine.cpu.memory[grounded] = 0
    machine.frame({0xFEFE: 0x02})
    assert machine.cpu.memory[room] == 4
    assert machine.cpu.memory[player_x] == 230
    assert machine.cpu.memory[player_y] == 152
    machine.frame({0xFEFE: 0x04})
    assert machine.cpu.memory[room] == 3
    assert machine.cpu.memory[player_x] == 0
    assert machine.cpu.memory[player_y] == 155
    print("PASS Blog21: grounded room3/room4 lower route links select matching entries")


def verify_water(reference: Path) -> None:
    water_folder = ROOT / "tutorials" / "Blog22" / "24-1-AnimatedWater"
    for image_number in range(55, 59):
        source = reference / "assembly" / "blog22" / "assets" / f"image-{image_number}.bin"
        local = water_folder / "assets" / source.name
        assert local.read_bytes() == source.read_bytes(), f"Blog22 asset copy changed: {source.name}"
    assert render_water_frames(water_folder / "assets") == \
        (water_folder / "WaterFrames.g.cs").read_text(encoding="ascii")
    machine = scenes.PagedMachine(
        water_folder / "out"
    )
    assets = reference / "assembly" / "blog22" / "assets"
    clean = bytes(machine.banks[6][4:0x6004])
    for update in range(16):
        screen = machine.frame()
        frame = ((update + 1) // 3) % 4
        pixels = (assets / f"image-{55 + frame}.bin").read_bytes()[2:]
        expected = bytearray(clean)
        for row in range(4):
            for column in range(20):
                pixel = pixels[row * 20 + column]
                mask = (0xF0 if pixel & 0xF0 == 0 else 0) | \
                       (0x0F if pixel & 0x0F == 0 else 0)
                offset = 17890 + row * 128 + column
                expected[offset] = (expected[offset] & mask) | pixel
            # The source composition repeats the left half at the clipped
            # screen edge; it is a second 10-byte draw, not a second frame.
            for column in range(10):
                pixel = pixels[row * 20 + column]
                mask = (0xF0 if pixel & 0xF0 == 0 else 0) | \
                       (0x0F if pixel & 0x0F == 0 else 0)
                offset = 17910 + row * 128 + column
                expected[offset] = (expected[offset] & mask) | pixel
        region = [17890 + row * 128 + x for row in range(4) for x in range(30)]
        assert all(screen[index] == expected[index] for index in region), \
            f"Blog22 wave pixels/frame cadence diverged at update {update}"
    print("PASS Blog22: source-exact water frames and three-update cadence")


def verify_hazard_and_restart() -> None:
    hazard = scenes.PagedMachine(
        ROOT / "tutorials" / "Blog23" / "25-1-HazardDetection" / "out"
    )
    hazard.frame()
    colors = []
    for _ in range(90):
        hazard.frame({0xFEFE: 0x04})
        colors.append(hazard.border)
    assert 4 in colors and hazard.border == 4, "Blog23 sea overlap did not turn the border red"
    for _ in range(100):
        hazard.frame({0xFEFE: 0x02})
    assert hazard.border in (3, 7), "Blog23 leaving the sea did not restore support feedback"
    print("PASS Blog23: the sea is traversable, contact is red, and leaving restores support color")

    restart = scenes.PagedMachine(
        ROOT / "tutorials" / "Blog24" / "26-1-DeathRestart" / "out"
    )
    restart.frame()
    initial_world = bytes(restart.banks[6][4:0x6004])
    for _ in range(90):
        restart.frame({0xFEFE: 0x04})
    assert restart.border == 4, "Blog24 hazard did not start the death pause"
    flashing = []
    for _ in range(100):
        restart.frame({0xFEFE: 0x04, 0x7FFE: 0x01, 0xBFFE: 0x01})
        flashing.append(restart.border)
    assert 0 in flashing and 4 in flashing, "Blog24 death pause did not flash red/black"
    assert bytes(restart.banks[6][4:0x6004]) == initial_world, \
        "Blog24 did not restore the initial chest/world snapshot"
    print("PASS Blog24: one-life pause blocks input, flashes, and restores the initial world")


def verify_bee(reference: Path) -> None:
    folder = ROOT / "tutorials" / "Blog25" / "27-1-MovingBee"
    for tutorial in (
        folder,
        ROOT / "tutorials" / "Blog26" / "28-1-BeeHazard",
        ROOT / "tutorials" / "Blog27" / "29-1-CompleteRoute",
    ):
        for image_number in range(327, 331):
            source = reference / "assembly" / "blog25" / "assets" / f"image-{image_number}.bin"
            local = tutorial / "assets" / source.name
            assert local.read_bytes() == source.read_bytes(), \
                f"{tutorial.name} bee asset copy changed: {source.name}"
        assert render_bee_frames(tutorial / "assets") == \
            (tutorial / "BeeFrames.g.cs").read_text(encoding="ascii")
    machine = scenes.PagedMachine(folder / "out")
    machine.frame()
    # Seed only the room selector to exercise the actor without claiming that
    # this test is a complete keyboard-driven route playthrough.
    machine.cpu.memory[machine.symbols["cs_v_001_0001"]] = 4
    clean = bytes(machine.banks[6][4:0x6004])
    for _ in range(3):
        screen = machine.frame()
    bee_x = machine.symbols["cs_v_015_0015"]
    assert machine.cpu.memory[bee_x] == 78, "Blog25 bee did not move one byte after three updates"

    source = (reference / "assembly" / "blog25" / "assets" / "image-328.bin").read_bytes()
    assert source[:2] == bytes((11, 22)) and len(source) == 244
    pixels = source[2:]
    expected = bytearray(clean)
    for row in range(22):
        for column in range(11):
            pixel = pixels[row * 11 + column]
            mask = (0xF0 if pixel & 0xF0 == 0 else 0) | \
                   (0x0F if pixel & 0x0F == 0 else 0)
            offset = 100 * 128 + row * 128 + 78 + column
            expected[offset] = (expected[offset] & mask) | pixel
    indices = [100 * 128 + row * 128 + column
               for row in range(22) for column in range(78, 89)]
    assert all(screen[index] == expected[index] for index in indices), \
        "Blog25 bee pixels do not match source frame 328"

    observed = [machine.cpu.memory[bee_x]]
    for _ in range(400):
        machine.frame()
        observed.append(machine.cpu.memory[bee_x])
    assert min(observed) == 54 and max(observed) == 116, \
        f"Blog25 patrol should step just beyond both bounds: {min(observed)}..{max(observed)}"
    print("PASS Blog25: source-exact bee frame, three-update movement, and bounded patrol")


def verify_bee_hazard_and_final_program() -> None:
    machine = scenes.PagedMachine(
        ROOT / "tutorials" / "Blog26" / "28-1-BeeHazard" / "out"
    )
    machine.frame()
    machine.cpu.memory[machine.symbols["cs_v_001_0001"]] = 4
    machine.cpu.memory[machine.symbols["cs_v_003_0003"]] = 150
    machine.cpu.memory[machine.symbols["cs_v_004_0004"]] = 92
    machine.frame()
    assert machine.border == 4, "Blog26 bee body overlap did not start the fatal hazard state"

    blog26 = ROOT / "tutorials" / "Blog26" / "28-1-BeeHazard" / "out" / "program.bin"
    blog27 = ROOT / "tutorials" / "Blog27" / "29-1-CompleteRoute" / "out" / "program.bin"
    assert blog26.read_bytes() == blog27.read_bytes(), \
        "Blog27 is intentionally an unchanged assembly of Blog26's complete engine"
    print("PASS Blogs26–27: moving bee contact is fatal; final route binary is unchanged")


def field_address(machine, field: str) -> int:
    """Resolve an authored assignment through its actual source-map label."""
    output = machine.output
    mapping = json.loads((output / "program.source-map.json").read_text())
    assembly = (output / "program.asm").read_text()
    for point in mapping["points"]:
        source = Path(point["file"]).read_text().splitlines()
        line = source[point["line"] - 1].strip()
        if re.match(rf"{field}\s*=(?!=)", line):
            emitted = assembly.split(point["label"] + ":", 1)[1]
            store = re.search(r"LD \((cs_v_\d+_\d+)(?:\+0)?\),A", emitted)
            if store:
                return machine.symbols[store[1]]
    raise AssertionError(f"No mapped scalar assignment for {field}")


def verify_integrated_route() -> None:
    machine = scenes.PagedMachine(
        ROOT / "tutorials" / "Blog27" / "29-1-CompleteRoute" / "out"
    )
    machine.frame()
    names = ("CurrentRoom", "PlayerX", "PlayerY", "Grounded",
             "VerticalVelocity", "InventoryCount", "DeathTimer")
    addresses = {name: field_address(machine, name) for name in names}

    def value(name):
        return machine.cpu.memory[addresses[name]]

    initial_world = bytes(machine.banks[6][4:0x6004])
    for _ in range(35):
        machine.frame({0xFEFE: 2})
    machine.frame()
    machine.frame({0xBFFE: 1})
    assert value("InventoryCount") == 1, "Finished engine did not collect the beach chest"
    carried_world = bytes(machine.banks[6][4:0x6004])
    assert carried_world != initial_world, "Pickup did not update the world and HUD"
    machine.frame()
    for _ in range(105):
        machine.frame({0xFEFE: 4})
    assert value("DeathTimer") > 0, "Finished engine did not enter sea death"
    position = (value("PlayerX"), value("PlayerY"))
    while value("DeathTimer") > 1:
        machine.frame({0xFEFE: 2, 0x7FFE: 1, 0xBFFE: 1})
        assert (value("PlayerX"), value("PlayerY")) == position, "Death pause accepted movement"
        assert value("InventoryCount") == 1, "Death pause accepted an inventory action"
    machine.frame()
    assert value("CurrentRoom") == 1 and value("InventoryCount") == 0
    assert bytes(machine.banks[6][4:0x6004]) == initial_world

    # Seed only a route boundary, then run the authored room3 -> room4 link
    # and both loaders. This is not a claim of solving the entire chest puzzle.
    for name, value_ in (("CurrentRoom", 3), ("PlayerX", 0), ("PlayerY", 155),
                         ("Grounded", 1), ("VerticalVelocity", 0)):
        machine.cpu.memory[addresses[name]] = value_
    machine.frame({0xFEFE: 2})
    assert value("CurrentRoom") == 4 and value("PlayerX") == 230
    room4_world = bytes(machine.banks[6][4:0x6004])
    assert room4_world != initial_world
    for _ in range(9):
        machine.frame()
    machine.cpu.memory[addresses["PlayerX"]] = 150
    machine.cpu.memory[addresses["PlayerY"]] = 92
    machine.cpu.memory[addresses["Grounded"]] = 0
    machine.frame()
    assert value("DeathTimer") > 0 and machine.border == 4
    print("PASS Blog27: chest pickup/HUD, blocked death input, full snapshot restart, room4 link/bee death")


class SoundMachine:
    def __init__(self, folder: str):
        output = ROOT / "tutorials" / "Blog28" / folder / "out"
        self.symbols = {
            name: int(value)
            for name, value in re.findall(
                r"V(\w+)\s+p\d+\s+I(\d+)",
                (output / "program.symbols").read_text(),
            )
        }
        image = (output / "program.bin").read_bytes()
        self.cpu = z80.Z80Machine()
        self.cpu.set_memory_block(0x8000, image[:0x8000])
        self.keys: dict[int, int] = {}
        self.ports: list[tuple[int, int]] = []
        self.cpu.set_input_callback(lambda port: 0xFF ^ self.keys.get(port, 0))
        self.cpu.set_output_callback(lambda port, value: self.ports.append((port, value)))
        self.cpu.memory[self.symbols["sam_rom_lmpr"]] = 0x1F
        self.cpu.sp = 0xFEFE
        self.cpu.memory[0xFEFE:0xFF00] = bytes((0, 0x70))
        self.cpu.pc = self.symbols["cs_main"]
        self.cpu.set_breakpoint(self.symbols["sam_wait_frames"])

    def frame(self, keys: dict[int, int] | None = None) -> None:
        self.keys = keys or {}
        for _ in range(5000):
            self.cpu.ticks_to_stop = 1_000_000
            self.cpu.run()
            if self.cpu.pc == self.symbols["sam_wait_frames"]:
                return_address = (
                    self.cpu.memory[self.cpu.sp]
                    | self.cpu.memory[self.cpu.sp + 1] << 8
                )
                self.cpu.sp += 2
                self.cpu.pc = return_address
                return
        raise AssertionError(f"Sound program did not reach frame wait: PC={self.cpu.pc:04X}")

    def saa_writes(self) -> list[tuple[int, int]]:
        writes = []
        for index in range(len(self.ports) - 1):
            select_port, register = self.ports[index]
            data_port, value = self.ports[index + 1]
            if select_port == 0x01FF and data_port == 0x00FF:
                writes.append((register, value))
        return writes


def verify_sound() -> None:
    tone = SoundMachine("30-1-SustainedTone")
    tone.frame()
    writes = tone.saa_writes()
    assert writes[:32] == [(register, 0) for register in range(32)]
    assert writes[32:] == [(0, 0xAA), (8, 227), (16, 2), (20, 1), (28, 1)]

    effect = SoundMachine("30-2-TriggeredEffect")
    effect.frame()
    effect.ports.clear()
    effect.frame({0x7FFE: 0x01})
    assert (8, 33) in effect.saa_writes(), "Blog28 part 2 did not trigger on fresh Space"
    effect.ports.clear()
    effect.frame({0x7FFE: 0x01})
    assert (8, 33) not in effect.saa_writes(), "Holding Space retriggered the effect"
    effect.frame()
    effect.ports.clear()
    effect.frame({0x7FFE: 0x01})
    assert (8, 33) in effect.saa_writes(), "Release and fresh press did not retrigger"

    music = SoundMachine("30-3-MelodyAndEffect")
    music.frame()
    assert (8, 33) in music.saa_writes(), "Blog28 part 3 did not start its melody"
    music.ports.clear()
    music.frame({0x7FFE: 0x01})
    assert (9, 33) in music.saa_writes() and (20, 3) in music.saa_writes(), \
        "Effect did not use channel one alongside the melody"
    print("PASS Blog28: SAA select/data ports, full reset, edge-triggered effect, and separate channels")

    # Check every update, including the silent intervals, rather than merely
    # seeing each expected frequency appear somewhere in the register trace.
    effect = SoundMachine("30-2-TriggeredEffect")
    effect.frame()
    timeline = {}
    for update in range(21):
        effect.ports.clear()
        effect.frame({0x7FFE: 1})
        writes = effect.saa_writes()
        if writes:
            timeline[update] = writes
    assert timeline == {
        0: [(8, 33), (20, 1)],
        4: [(8, 85), (20, 1)],
        8: [(8, 132), (20, 1)],
        16: [(20, 0)],
    }, f"Effect pitch/duration/disable trace differs: {timeline}"
    effect.frame()
    effect.ports.clear()
    effect.frame({0x7FFE: 1})
    assert effect.saa_writes() == [(8, 33), (20, 1)]

    music = SoundMachine("30-3-MelodyAndEffect")
    melody = []
    cue = []
    for update in range(65):
        music.ports.clear()
        music.frame({0x7FFE: 1} if update >= 1 else {})
        for register, value in music.saa_writes():
            if register == 8:
                melody.append((update, value))
            elif register in (9, 20) and update != 0:
                cue.append((update, register, value))
    assert melody == [(0, 0), (0, 33), (8, 85), (16, 132), (24, 193),
                      (32, 132), (40, 85), (48, 33), (60, 33)]
    assert cue == [(1, 9, 33), (1, 20, 3), (5, 9, 85), (5, 20, 3),
                   (9, 9, 132), (9, 20, 3), (17, 20, 1)]
    # For the SAM's 8 MHz SAA clock: f = 15625 * 2^octave / (511-register).
    registers = dict(tone.saa_writes())
    assert abs(15625 * 2 ** (registers[16] & 7) / (511 - registers[8]) - 220) < 0.1
    print("PASS Blog28: exact 4/4/8 effect timers, no held-key restart, 8/12-tick melody loop, independent enables")


def main() -> None:
    settings = json.loads((ROOT / "tools.local.json").read_text(encoding="utf-8-sig"))
    reference = Path(settings["referenceRepo"])
    verify_four_screen_route()
    verify_water(reference)
    verify_hazard_and_restart()
    verify_bee(reference)
    verify_bee_hazard_and_final_program()
    verify_integrated_route()
    verify_sound()


if __name__ == "__main__":
    main()
