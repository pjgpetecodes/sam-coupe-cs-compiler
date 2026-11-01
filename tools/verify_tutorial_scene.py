"""Verify banked beach copying and per-display-page background restoration."""

import json
import re
import sys
from pathlib import Path

import z80

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / "tools"))
from generate_tutorial_assets import frame_source


class PagedMachine:
    def __init__(self, output):
        self.output = output
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
        self.banks = {page: bytearray(0x8000) for page in range(32)}
        for page in range(1, 32):
            start = (page - 1) * 0x4000
            self.banks[page][:] = (image[start : start + 0x8000] + bytes(0x8000))[:0x8000]
        self.bank = 0
        self.display_page = 4
        self.border = 0
        self.keys = {}
        self.cpu.set_input_callback(self.read_port)
        self.cpu.set_output_callback(self.write_port)
        self.cpu.memory[self.symbols["sam_rom_lmpr"]] = 0x1F
        self.cpu.memory[self.symbols["sam_draw_page"]] = 4
        self.cpu.sp = 0xFEFE
        self.cpu.memory[0xFEFE:0xFF00] = bytes((0, 0x70))
        self.cpu.pc = self.symbols["cs_main"]
        self.stops = {
            self.symbols["sam_wait_frames"]: "wait",
            self.symbols["sam_trap"]: "trap",
        }
        for address in self.stops:
            self.cpu.set_breakpoint(address)

    def read_port(self, port):
        return 0xFF ^ self.keys.get(port, 0)

    def write_port(self, port, value):
        if port & 0xFF == 250:
            assert not self.cpu.iff1, "Interrupts enabled while a bank is mapped"
            self.banks[self.bank][:] = self.cpu.memory[:0x8000]
            self.bank = (value & 31) if value & 0x20 else 0
            self.cpu.set_memory_block(0, self.banks[self.bank])
        elif port & 0xFF == 252:
            self.display_page = value & 30
        elif port & 0xFF == 254:
            self.border = value & 7

    def frame(self, keys=None):
        self.keys = keys or {}
        for _ in range(5000):
            self.cpu.ticks_to_stop = 1_000_000
            self.cpu.run()
            if self.cpu.pc in self.stops:
                stop = self.stops[self.cpu.pc]
                if stop == "trap":
                    raise AssertionError(f"Native SAM trap {self.cpu.a:02X}")
                assert self.bank == 0, "The lower ROM mapping was not restored before the wait"
                screen = bytes(self.banks[self.display_page][:0x6000])
                return_address = (
                    self.cpu.memory[self.cpu.sp]
                    | self.cpu.memory[self.cpu.sp + 1] << 8
                )
                self.cpu.sp += 2
                self.cpu.pc = return_address
                return screen
        raise AssertionError(f"Native program did not reach a frame wait: PC={self.cpu.pc:04X}")


def shift_frame(frame):
    shifted = []
    for row in range(22):
        pairs = [(frame[(row * 13 + x) * 2], frame[(row * 13 + x) * 2 + 1]) for x in range(13)]
        previous_mask, previous_pixel = pairs[0]
        shifted.extend(((previous_mask >> 4) | 0xF0, previous_pixel >> 4))
        for mask, pixel in pairs[1:]:
            shifted.extend((((previous_mask << 4) & 0xF0) | (mask >> 4),
                            ((previous_pixel << 4) & 0xF0) | (pixel >> 4)))
            previous_mask, previous_pixel = mask, pixel
        shifted.extend((((previous_mask << 4) & 0xF0) | 0x0F,
                        (previous_pixel << 4) & 0xF0))
    return shifted


def scene(background, frame, x, y=115):
    result = bytearray(background)
    if x & 1:
        frame = shift_frame(frame)
        width = 14
    else:
        width = 13
    for row in range(22):
        for column in range(width):
            source = (row * width + column) * 2
            destination = (y + row) * 128 + x // 2 + column
            result[destination] = (result[destination] & frame[source]) | frame[source + 1]
    return bytes(result)


def verify_beach(reference):
    output = ROOT / "tutorials" / "Blog10" / "12-2-StartingBeach" / "out"
    machine = PagedMachine(output)
    actual = machine.frame()
    expected = (reference / "assembly" / "blog10" / "assets" / "screen-1.bin").read_bytes()
    assert actual == expected, "The complete banked beach differs from the original 24 KB bitmap"
    print("PASS Blog 10: both physical bank pairs copy the exact 24 KB beach")


def verify_restoration(reference):
    output = ROOT / "tutorials" / "Blog11" / "13-1-BackgroundRestoration" / "out"
    machine = PagedMachine(output)
    background = (reference / "assembly" / "blog10" / "assets" / "screen-1.bin").read_bytes()
    source = reference / "assembly" / "blog6" / "tidizzy_cropped.asm"
    kind, (frames, _) = frame_source(source, reference)
    assert kind == "frames"

    actual = machine.frame()
    assert actual == scene(background, frames[0][1], 0), "Initial background and idle sprite differ"
    actual = machine.frame({0xFEFE: 0x04})
    assert actual == scene(background, frames[17][1], 1), "Odd-position walking frame differs"
    actual = machine.frame()
    assert actual == scene(background, frames[0][1], 1), "Previous odd-width rectangle was not restored"
    actual = machine.frame({0xFEFE: 0x04})
    assert actual == scene(background, frames[17][1], 4), "Previous odd-position scenery was not restored"
    print("PASS Blog 11: both buffers restore exact scenery before even/odd sprite draws")


def verify_sprite_edge_limits(reference):
    source = reference / "assembly" / "blog6" / "tidizzy_cropped.asm"
    kind, (frames, _) = frame_source(source, reference)
    assert kind == "frames"
    background = bytes(0x6000)

    byte_output = ROOT / "tutorials" / "Blog08" / "10-1-ByteStepMovement" / "out"
    byte_machine = PagedMachine(byte_output)
    for _ in range(115):
        byte_screen = byte_machine.frame({0xFEFE: 0x04})
    assert byte_screen in {
        scene(background, frames[17 + index][1], 230, 5)
        for index in range(7)
    }, "Blog 8's final byte step must draw the whole sprite at the screen's right edge"
    assert 1 + 114 + 13 == 128, "Blog 8's rightmost frame must end at the row boundary"

    pixel_output = ROOT / "tutorials" / "Blog09" / "11-1-PixelPositioning" / "out"
    pixel_machine = PagedMachine(pixel_output)
    for _ in range(115):
        pixel_screen = pixel_machine.frame({0xFEFE: 0x04})
    assert pixel_screen in {
        scene(background, frames[17 + index][1], 230, 5)
        for index in range(7)
    }, "Blog 9 must clamp at pixel X=228 without drawing past the row"
    assert 1 + 228 // 2 + 13 == 128, "Blog 9's even frame must end at the row boundary"

    # Reach X=227 through the normal alternating one/three-pixel controls, then
    # verify the fourteen-byte odd frame also ends exactly at byte 128.
    for key in (0x02, 0x02, 0x04):
        pixel_screen = pixel_machine.frame({0xFEFE: key})
    assert pixel_screen in {
        scene(background, frames[17 + index][1], 229, 5)
        for index in range(7)
    }, "Blog 9's odd frame at X=227 must fit without crossing the screen row"
    assert 1 + 227 // 2 + 14 == 128, "Blog 9's odd frame must end at the row boundary"

    pixel_screen = pixel_machine.frame({0xFEFE: 0x04})
    assert pixel_screen in {
        scene(background, frames[17 + index][1], 230, 5)
        for index in range(7)
    }, "Blog 9 must return from odd X=227 to the maximum even X=228 safely"
    assert byte_machine.bank == pixel_machine.bank == 0
    print("PASS Blogs 8-9: native edge movement preserves the full sprite within 128-byte rows")


def verify_room_composition(reference):
    output = ROOT / "tutorials" / "Blog12" / "14-1-RoomComposition" / "out"
    machine = PagedMachine(output)
    background = (reference / "assembly" / "blog10" / "assets" / "screen-1.bin").read_bytes()
    source = reference / "assembly" / "blog6" / "tidizzy_cropped.asm"
    kind, (frames, _) = frame_source(source, reference)
    assert kind == "frames"
    actual = machine.frame()
    assert actual == scene(background, frames[0][1], 0), "Room records do not reproduce the source-derived beach"
    assembly = (output / "program.asm").read_text()
    assert "DUMP 8,2" in assembly and "DUMP 4,2" in assembly
    assert "DUMP 6,2" not in assembly and "CALL cs_initialize_banked_data" in assembly
    print("PASS Blog 12: C# room records compose the reference beach; unused room fields are tree-shaken")


def constrain_x(collision, x, desired, y):
    direction = 1 if desired > x else -1
    while x != desired:
        candidate = x + direction
        leading_x = candidate + (21 if direction > 0 else 4)
        blocked = False
        for row in range(y + 4, y + 21):
            mask = collision[row * 128 + leading_x // 2]
            pixel_mask = 0x0F if leading_x & 1 else 0xF0
            if mask & pixel_mask:
                blocked = True
                break
        if blocked:
            break
        x = candidate
    return x


def verify_horizontal_collision(reference):
    output = ROOT / "tutorials" / "Blog13" / "15-1-HorizontalCollision" / "out"
    machine = PagedMachine(output)
    assets = reference / "assembly" / "blog10" / "assets"
    background = (assets / "screen-2.bin").read_bytes()
    collision = (reference / "assembly" / "blog13" / "assets" / "screen-2-collision.bin").read_bytes()
    source = reference / "assembly" / "blog6" / "tidizzy_cropped.asm"
    kind, (frames, _) = frame_source(source, reference)
    assert kind == "frames"

    actual = machine.frame()
    assert actual == scene(background, frames[0][1], 120, 87), "The cliff room or starting sprite differs"
    assert bytes(machine.banks[6][4:0x6004]) == background, "Room records did not compose the exact cliff bitmap"

    x = 120
    phase = 0
    animation = 0
    frame = 0
    delay = 5
    for _ in range(50):
        delay = max(0, delay - 1)
        step = 1 if phase == 0 else 3
        phase = 1 - phase
        x = constrain_x(collision, x, max(0, x - step), 87)
        if animation != 2:
            animation = 2
            frame = 0
            delay = 5
        if delay == 0:
            frame += 1
            if frame == 8:
                frame = 0
            delay = 5
        actual = machine.frame({0xFEFE: 0x02})
        assert actual == scene(background, frames[9 + frame][1], x, 87), \
            f"Collision draw differs after left step: X={x}, frame={frame}"

    assert x == 38, f"Expected the cliff collision boundary at X=38, got X={x}"
    assert bytes(machine.banks[12][4:0x6004]) == collision, "The banked collision mask changed during probing"
    assembly = (output / "program.asm").read_text()
    assert "CALL cs_initialize_banked_data" in assembly and "cs_array_byte_address_bounds:" in assembly
    assert bytes(machine.banks[12][4:0x6004]) == collision
    print("PASS Blog 13: exact cliff scene and mask; C# stops at the reference-derived X=38 boundary")


def row_has_solid(mask, y, first_x, count=10):
    for x in range(first_x, first_x + count):
        value = mask[y * 128 + x // 2]
        pixel_mask = 0x0F if x & 1 else 0xF0
        if value & pixel_mask:
            return True
    return False


def box_clear(mask, x, y):
    return not any(row_has_solid(mask, row, x + 8) for row in range(y + 4, y + 21))


def move_grounded(mask, x, y, grounded, destination):
    direction = 1 if destination > x else -1
    blocked = False
    while x != destination and not blocked:
        candidate_x = x + direction
        found_y = None
        for candidate_y in range(max(0, y - 3), min(170, y + 3) + 1):
            if row_has_solid(mask, candidate_y + 21, candidate_x + 8) and \
                    box_clear(mask, candidate_x, candidate_y):
                found_y = candidate_y
                break
        if found_y is not None:
            x, y, grounded = candidate_x, found_y, True
        elif box_clear(mask, candidate_x, y):
            x, grounded = candidate_x, False
        else:
            blocked = True
    return x, y, grounded


def move_airborne(mask, x, y, destination):
    direction = 1 if destination > x else -1
    blocked = False
    while x != destination and not blocked:
        candidate_x = x + direction
        if box_clear(mask, candidate_x, y):
            x = candidate_x
        else:
            blocked = True
    return x


def apply_gravity(mask, x, y, fall_speed, grounded):
    moved = False
    if row_has_solid(mask, y + 21, x + 8):
        return y, 0, True, moved

    grounded = False
    if fall_speed < 4:
        fall_speed += 1
    remaining = fall_speed
    while remaining and y < 170:
        candidate_y = y + 1
        if box_clear(mask, x, candidate_y):
            y = candidate_y
            moved = True
            if row_has_solid(mask, y + 21, x + 8):
                grounded = True
                fall_speed = 0
                remaining = 0
            else:
                remaining -= 1
        else:
            grounded = row_has_solid(mask, y + 21, x + 8)
            fall_speed = 0
            remaining = 0
    if y == 170:
        grounded = False
        fall_speed = 0
    return y, fall_speed, grounded, moved


def verify_ground_example(reference, folder, screen, collision_file, start, direction, steps,
                          return_steps=0):
    output = ROOT / "tutorials" / "Blog14" / folder / "out"
    machine = PagedMachine(output)
    assets = reference / "assembly" / "blog10" / "assets"
    background = (assets / f"screen-{screen}.bin").read_bytes()
    collision = (reference / "assembly" / "blog14" / "assets" / collision_file).read_bytes()
    source = reference / "assembly" / "blog6" / "tidizzy_cropped.asm"
    kind, (frames, _) = frame_source(source, reference)
    assert kind == "frames"

    x, y = start
    actual = machine.frame()
    assert actual == scene(background, frames[0][1], x, y), f"{folder}: initial room or sprite differs"
    assert machine.border == 7, f"{folder}: initial supported state should have a black border"

    move_phase = 0
    animation = 0
    frame = 0
    delay = 5
    grounded = True
    saw_unsupported = False
    for iteration in range(steps + return_steps):
        move_direction = direction if iteration < steps else -direction
        delay = max(0, delay - 1)
        step = 1 if move_phase == 0 else 3
        move_phase = 1 - move_phase
        destination = max(0, min(230, x + move_direction * step))
        x, y, grounded = move_grounded(collision, x, y, grounded, destination)
        if not grounded:
            saw_unsupported = True

        next_animation = 2 if move_direction < 0 else 3
        if animation != next_animation:
            animation = next_animation
            frame = 0
            delay = 5
        if delay == 0:
            frame += 1
            if animation == 2 and frame == 8 or animation == 3 and frame == 7:
                frame = 0
            delay = 5

        sprite = 9 + frame if animation == 2 else 17 + frame
        key = 0x02 if move_direction < 0 else 0x04
        actual = machine.frame({0xFEFE: key})
        assert actual == scene(background, frames[sprite][1], x, y), \
            f"{folder}: ground-following draw differs at X={x}, Y={y}, frame={frame}"
        assert machine.border == (7 if grounded else 3), \
            f"{folder}: support border disagrees at X={x}, Y={y}"

    assert bytes(machine.banks[6][4:0x6004]) == background, f"{folder}: background bank was altered"
    assert bytes(machine.banks[12][4:0x6004]) == collision, f"{folder}: collision bank was altered"
    assembly = (output / "program.asm").read_text()
    runtime = (output / "sam-runtime.asm").read_text()
    symbols = {
        name: int(value)
        for name, value in re.findall(
            r"V(\w+)\s+p\d+\s+I(\d+)",
            (output / "program.symbols").read_text(),
        )
    }
    assert "CALL sam_any_solid_pixel_row" in assembly and \
        "sam_any_solid_pixel_row:" in runtime, f"{folder}: typed row kernel was not linked"
    assert symbols["sam_cli_image_end"] < 0xE000, f"{folder}: resident image exceeds the stack floor"
    if direction > 0:
        assert machine.border == 7 and x == 160, f"{folder}: walking back should restore support"
        assert saw_unsupported, f"{folder}: the real beach edge was not reached"
    else:
        assert x == 34 and y == 87, f"{folder}: cliff wall should stop at the reference-derived position"
    print(f"PASS {folder}: exact screen/mask, packed-row bounds, support tracking and resident budget")


def verify_ground_following(reference):
    verify_ground_example(
        reference,
        "16-1-GroundFollowing",
        2,
        "screen-2-collision.bin",
        (120, 99),
        -1,
        70,
    )
    verify_ground_example(
        reference,
        "16-2-LedgeSupport",
        1,
        "screen-1-collision.bin",
        (120, 106),
        1,
        40,
        20,
    )


def verify_gravity_and_landing(reference):
    output = ROOT / "tutorials" / "Blog15" / "17-1-GravityAndLanding" / "out"
    machine = PagedMachine(output)
    assets = reference / "assembly" / "blog10" / "assets"
    background = (assets / "screen-1.bin").read_bytes()
    collision = (reference / "assembly" / "blog14" / "assets" / "screen-1-collision.bin").read_bytes()
    source = reference / "assembly" / "blog6" / "tidizzy_cropped.asm"
    kind, (frames, _) = frame_source(source, reference)
    assert kind == "frames"

    x, y, fall_speed, grounded = 120, 70, 0, False
    y, fall_speed, grounded, _ = apply_gravity(collision, x, y, fall_speed, grounded)
    actual = machine.frame()
    assert actual == scene(background, frames[0][1], x, y), "Initial gravity step differs"
    assert (y, fall_speed, grounded, machine.border) == (71, 1, False, 3)

    delay = 5
    frame = 0
    for _ in range(35):
        delay = max(0, delay - 1)
        y, fall_speed, grounded, moved = apply_gravity(collision, x, y, fall_speed, grounded)
        if delay == 0:
            frame += 1
            if frame == 2:
                frame = 0
            delay = 5
        actual = machine.frame()
        assert actual == scene(background, frames[frame][1], x, y), \
            f"Idle fall/landing differs at Y={y}, frame={frame}"
        assert machine.border == (7 if grounded else 3), "Gravity support border is incorrect"
    assert (y, fall_speed, grounded) == (106, 0, True), \
        f"Expected landing on lower terrain at Y=106, got {y}, speed {fall_speed}"
    assert bytes(machine.banks[6][4:0x6004]) == background
    assert bytes(machine.banks[12][4:0x6004]) == collision

    move_phase = 0
    animation = 0
    frame = 0
    for _ in range(20):
        delay = max(0, delay - 1)
        step = 1 if move_phase == 0 else 3
        move_phase = 1 - move_phase
        destination = min(230, x + step)
        if grounded:
            x, y, grounded = move_grounded(collision, x, y, grounded, destination)
        else:
            x = move_airborne(collision, x, y, destination)
        y, fall_speed, grounded, _ = apply_gravity(collision, x, y, fall_speed, grounded)
        if animation != 3:
            animation = 3
            frame = 0
            delay = 5
        if delay == 0:
            frame += 1
            if frame == 7:
                frame = 0
            delay = 5
        actual = machine.frame({0xFEFE: 0x04})
        assert actual == scene(background, frames[17 + frame][1], x, y), \
            f"Grounded movement after landing differs at X={x}, Y={y}"
        assert machine.border == (7 if grounded else 3)

    assembly = (output / "program.asm").read_text()
    runtime = (output / "sam-runtime.asm").read_text()
    symbols = {
        name: int(value)
        for name, value in re.findall(
            r"V(\w+)\s+p\d+\s+I(\d+)",
            (output / "program.symbols").read_text(),
        )
    }
    assert "CALL sam_any_solid_pixel_row" in assembly
    assert symbols["sam_cli_image_end"] < 0xE000
    assert "sam_draw_rle_masked_sprite:" not in runtime
    print("PASS Blog 15: accelerated fall, no-tunnel landing, post-landing movement and resident budget")


def simulate_jump_frame(mask, state, space, left, right):
    state["delay"] = max(0, state["delay"] - 1)
    started = False
    if space and not state["space_was_down"] and state["grounded"]:
        state["velocity"] = -10
        state["grounded"] = False
        state["animation"] = 4 if left else 5
        state["frame"] = 0
        state["delay"] = 5
        state["jumps"] += 1
        started = True
    state["space_was_down"] = space

    direction = -1 if left else 1 if right else 0
    if direction:
        step = 1 if state["move_phase"] == 0 else 3
        state["move_phase"] = 1 - state["move_phase"]
        destination = max(0, min(230, state["x"] + direction * step))
        if state["grounded"]:
            state["x"], state["y"], state["grounded"] = move_grounded(
                mask, state["x"], state["y"], state["grounded"], destination)
        else:
            state["x"] = move_airborne(mask, state["x"], state["y"], destination)

    velocity = state["velocity"]
    if velocity < 0:
        velocity += 1
        state["velocity"] = velocity
        if velocity < 0:
            remaining = -velocity
            while remaining and state["y"] > 48:
                candidate_y = state["y"] - 1
                if box_clear(mask, state["x"], candidate_y):
                    state["y"] = candidate_y
                    remaining -= 1
                else:
                    state["velocity"] = 0
                    remaining = 0
            if state["y"] == 48:
                state["velocity"] = 0
    elif row_has_solid(mask, state["y"] + 21, state["x"] + 8):
        state["grounded"] = True
        state["velocity"] = 0
    else:
        state["grounded"] = False
        if velocity < 4:
            velocity += 1
        state["velocity"] = velocity
        remaining = velocity
        while remaining and state["y"] < 170:
            candidate_y = state["y"] + 1
            if box_clear(mask, state["x"], candidate_y):
                state["y"] = candidate_y
                if row_has_solid(mask, state["y"] + 21, state["x"] + 8):
                    state["grounded"] = True
                    state["velocity"] = 0
                    remaining = 0
                else:
                    remaining -= 1
            else:
                state["grounded"] = row_has_solid(mask, state["y"] + 21, state["x"] + 8)
                state["velocity"] = 0
                remaining = 0
        if state["y"] == 170:
            state["grounded"] = False
            state["velocity"] = 0

    animation = state["animation"]
    if not started and state["grounded"]:
        next_animation = 2 if left else 3 if right else 0
    elif not state["grounded"] and animation not in (4, 5):
        next_animation = 0
    else:
        next_animation = animation
    if next_animation != animation:
        state["animation"] = next_animation
        state["frame"] = 0
        state["delay"] = 5

    if state["delay"] == 0:
        state["frame"] += 1
        frame = state["frame"]
        if animation == 0 and frame == 2 or animation == 2 and frame == 8 or \
                animation == 3 and frame == 7:
            state["frame"] = 0
        elif animation == 4 and frame == 7 or animation == 5 and frame == 7:
            state["animation"] = 0
            state["frame"] = 0
        state["delay"] = 5


def verify_jump_trace(reference, folder, keys, fixture=False):
    output = ROOT / "tutorials" / "Blog16" / folder / "out"
    machine = PagedMachine(output)
    assets = reference / "assembly" / "blog10" / "assets"
    background = bytearray((assets / "screen-1.bin").read_bytes())
    collision = bytearray(
        (reference / "assembly" / "blog14" / "assets" / "screen-1-collision.bin").read_bytes())
    if fixture:
        for row in range(85, 88):
            start = row * 128 + 55
            background[start:start + 32] = bytes([0x77]) * 32
            collision[start:start + 32] = bytes([0xFF]) * 32
    source = reference / "assembly" / "blog6" / "tidizzy_cropped.asm"
    kind, (frames, _) = frame_source(source, reference)
    assert kind == "frames"

    state = dict(x=120, y=106, velocity=0, grounded=True, space_was_down=False,
                 move_phase=0, animation=0, frame=0, delay=5, jumps=0, min_y=106)
    actual = machine.frame()
    assert actual == scene(background, frames[0][1], state["x"], state["y"])
    for space, left, right in keys:
        simulate_jump_frame(collision, state, space, left, right)
        state["min_y"] = min(state["min_y"], state["y"])
        key_bits = (0x02 if left else 0) | (0x04 if right else 0)
        actual = machine.frame({
            0x7FFE: 1 if space else 0,
            0xFEFE: key_bits,
        })
        animation = state["animation"]
        sprite = state["frame"]
        if animation == 2:
            sprite = 9 + state["frame"]
        elif animation == 3:
            sprite = 17 + state["frame"]
        elif animation == 4:
            sprite = 24 + state["frame"]
        elif animation == 5:
            sprite = 31 + state["frame"]
        assert actual == scene(background, frames[sprite][1], state["x"], state["y"]), \
            f"{folder}: jump frame differs at X={state['x']}, Y={state['y']}, sprite={sprite}"
        assert machine.border == (7 if state["grounded"] else 3)

    assert state["grounded"] and row_has_solid(collision, state["y"] + 21, state["x"] + 8) and \
        state["jumps"] == 1, \
        f"{folder}: expected one completed jump, got {state}"
    assert bytes(machine.banks[6][4:0x6004]) == background
    assert bytes(machine.banks[12][4:0x6004]) == collision
    assembly = (output / "program.asm").read_text()
    runtime = (output / "sam-runtime.asm").read_text()
    symbols = {
        name: int(value)
        for name, value in re.findall(
            r"V(\w+)\s+p\d+\s+I(\d+)",
            (output / "program.symbols").read_text(),
        )
    }
    assert symbols["sam_cli_image_end"] < 0xE000
    assert "CALL sam_any_solid_pixel_row" in assembly
    assert "sam_draw_rle_masked_sprite:" not in runtime
    if fixture:
        assert state["min_y"] == 84, f"Artificial ceiling should stop at row 84, got {state['min_y']}"
    print(f"PASS {folder}: {state['jumps']} fresh jump, exact trajectory/ceiling, no retrigger and resident budget")


def verify_jumping(reference):
    verify_jump_trace(
        reference,
        "18-1-PhysicalJump",
        [(True, False, False)] * 70,
    )
    verify_jump_trace(
        reference,
        "18-1-PhysicalJump",
        [(True, False, False), (False, False, False), (True, False, False),
         (False, False, False)] + [(False, False, False)] * 45,
    )
    verify_jump_trace(
        reference,
        "18-1-PhysicalJump",
        [(True, True, False)] + [(False, True, False)] * 10 +
        [(False, False, False)] * 50,
    )
    verify_jump_trace(
        reference,
        "18-2-CeilingCollision",
        [(True, False, False)] * 70,
        fixture=True,
    )


def verify_connected_rooms(reference):
    output = ROOT / "tutorials" / "Blog17" / "19-1-ConnectedRooms" / "out"
    machine = PagedMachine(output)
    assets = reference / "assembly" / "blog10" / "assets"
    backgrounds = [(assets / f"screen-{room}.bin").read_bytes() for room in (1, 2)]
    collisions = [
        (assets / f"screen-{room}-collision.bin").read_bytes()
        for room in (1, 2)
    ]
    source = reference / "assembly" / "blog6" / "tidizzy_cropped.asm"
    kind, (frames, _) = frame_source(source, reference)
    assert kind == "frames"

    actual = machine.frame()
    assert actual == scene(backgrounds[0], frames[0][1], 120, 106)
    assert bytes(machine.banks[6][4:0x6004]) == backgrounds[0]
    assert bytes(machine.banks[12][4:0x6004]) == collisions[0]

    for _ in range(61):
        actual = machine.frame({0xFEFE: 0x02})
    assert bytes(machine.banks[6][4:0x6004]) == backgrounds[1]
    assert bytes(machine.banks[12][4:0x6004]) == collisions[1]
    assert actual == scene(backgrounds[1], frames[9][1], 229, 97), \
        "Left exit must enter the cliff room on its supported right-hand edge"

    for _ in range(61):
        actual = machine.frame({0xFEFE: 0x04})
    assert bytes(machine.banks[6][4:0x6004]) == backgrounds[0]
    assert bytes(machine.banks[12][4:0x6004]) == collisions[0]
    assert actual == scene(backgrounds[0], frames[22][1], 120, 106), \
        "Right exit must restore the beach scenery, collision and player state"
    assembly = (output / "program.asm").read_text()
    runtime = (output / "sam-runtime.asm").read_text()
    assert "CALL sam_decode_rle_background" in assembly
    assert "CALL sam_expand_byte_runs" in assembly
    assert "CALL sam_copy_banked_rows_to_screen" in assembly
    assert "sam_draw_rle_masked_sprite:" not in runtime
    assert machine.bank == 0
    print("PASS Blog 17: exact two-way room transitions, banked scenery/masks, animation state and resident budget")


def compose_chest(background, image):
    assert image[:2] == bytes((10, 16)) and len(image) == 162
    result = bytearray(background)
    for row in range(16):
        for column in range(10):
            source = image[2 + row * 10 + column]
            destination = (111 + row) * 128 + 24 + column
            old = result[destination]
            high = source & 0xF0 if source & 0xF0 else old & 0xF0
            low = source & 0x0F if source & 0x0F else old & 0x0F
            result[destination] = high | low
    return bytes(result)


def compose_item_image(background, image, offset):
    width, height = image[:2]
    result = bytearray(background)
    for row in range(height):
        for column in range(width):
            source = image[2 + row * width + column]
            destination = offset + row * 128 + column
            old = result[destination]
            high = source & 0xF0 if source & 0xF0 else old & 0xF0
            low = source & 0x0F if source & 0x0F else old & 0x0F
            result[destination] = high | low
    return result


def inventory_background(beach, images, world_x, world_y, rooms, inventory, active_room=1):
    result = bytearray(beach)
    for item, image in enumerate(images):
        if rooms[item] == active_room:
            result = compose_item_image(
                result, image, world_y[item] * 128 + world_x[item])

    slot_x = 16
    for item in inventory:
        for row in range(32):
            offset = 8 * 128 + row * 128 + slot_x
            if row in (0, 31):
                result[offset:offset + 24] = bytes([0x77]) * 24
            else:
                result[offset] = 0x70
                result[offset + 23] = 0x07
        result = compose_item_image(result, images[item], 15 * 128 + slot_x + 6)
        slot_x += 36

    for slot in range(len(inventory), 3):
        for row in range(32):
            offset = 8 * 128 + row * 128 + slot_x
            if row in (0, 31):
                result[offset:offset + 24] = bytes([0x77]) * 24
            else:
                result[offset] = 0x70
                result[offset + 23] = 0x07
        slot_x += 36
    return bytes(result)


def inventory_item_overlaps(x, y, item_x, item_y, width, height):
    return (
        x + 17 >= item_x * 2 and
        x + 8 < (item_x + width) * 2 and
        y + 20 >= item_y and
        y + 4 < item_y + height
    )


def safe_drop_position(mask, x, y, width, height):
    drop_x = (x + 3) // 2
    first_y = max(48, y + 18 - height)
    last_y = 191 - height
    pixel_width = width * 2
    for candidate_y in range(first_y, first_y + 7):
        if candidate_y > last_y:
            continue
        if any(row_has_solid(mask, candidate_y + row, drop_x * 2, pixel_width)
               for row in range(height)):
            continue
        if row_has_solid(mask, candidate_y + height, drop_x * 2, pixel_width):
            return drop_x, candidate_y
    return None


def verify_visible_inventory(reference):
    output = ROOT / "tutorials" / "Blog19" / "21-1-VisibleInventory" / "out"
    machine = PagedMachine(output)
    assets = reference / "assembly" / "blog10" / "assets"
    beach = (assets / "screen-1.bin").read_bytes()
    collision = (reference / "assembly" / "blog14" / "assets" / "screen-1-collision.bin").read_bytes()
    item_images = [
        (reference / "assembly" / "blog18" / "assets" / "image-215.bin").read_bytes(),
        (reference / "assembly" / "blog19" / "assets" / "image-216.bin").read_bytes(),
        (reference / "assembly" / "blog19" / "assets" / "image-211.bin").read_bytes(),
        (reference / "assembly" / "blog19" / "assets" / "image-231.bin").read_bytes(),
    ]
    world_x = [24, 40, 60, 80]
    world_y = [111, 110, 111, 110]
    world_rooms = [1, 1, 1, 1]
    widths = [10, 11, 10, 10]
    heights = [16, 17, 16, 16]
    inventory = []

    x, y, velocity, grounded, phase = 120, 106, 0, True, 0
    move_animation = 0

    def run_frame(direction=0, keys=None):
        nonlocal x, y, velocity, grounded, phase, move_animation
        if direction:
            step = 1 if phase == 0 else 3
            phase = 1 - phase
            destination = max(0, min(230, x + direction * step))
            if grounded:
                x, y, grounded = move_grounded(collision, x, y, grounded, destination)
            else:
                x = move_airborne(collision, x, y, destination)
            move_animation = 2 if direction < 0 else 3
        y, velocity, grounded, _ = apply_gravity(
            collision, x, y, velocity, grounded)
        machine.frame(keys)
        assert machine.bank == 0, "Inventory input left the collision page mapped before HALT"

    def press_enter():
        run_frame(keys={0xBFFE: 0x01})
        run_frame()

    def move_until_item(item, direction):
        for _ in range(100):
            if inventory_item_overlaps(
                    x, y, world_x[item], world_y[item], widths[item], heights[item]):
                return
            run_frame(direction, {0xFEFE: 0x02 if direction < 0 else 0x04})
        raise AssertionError(f"Could not walk into inventory item {item}: X={x}, Y={y}")

    def check_background(stack, expected_rooms=None):
        expected = inventory_background(
            beach, item_images, world_x, world_y,
            world_rooms if expected_rooms is None else expected_rooms, stack)
        actual = bytes(machine.banks[6][4:0x6004])
        assert actual == expected, "The authoritative room/HUD background differs from the item records"
        for page in (10, 14):
            visible_top = bytes(machine.banks[page][:48 * 128])
            assert visible_top == expected[:48 * 128], \
                f"Display page {page} lost the shared scenery or inventory HUD"
        return expected

    machine.frame()
    expected = check_background(inventory)

    # The starting position overlaps the axe fixture; the fresh Enter press
    # pushes it into slot zero and removes only its world drawing.
    assert inventory_item_overlaps(x, y, world_x[2], world_y[2], widths[2], heights[2])
    press_enter()
    inventory.append(2)
    world_rooms[2] = 255
    expected = check_background(inventory)

    for item in (1, 0):
        move_until_item(item, -1)
        run_frame()
        press_enter()
        inventory.append(item)
        world_rooms[item] = 255
        expected = check_background(inventory)

    assert inventory == [2, 1, 0], "HUD order must be oldest-left, newest-right"
    move_until_item(3, 1)
    run_frame()
    before_full_rejection = bytes(machine.banks[6][4:0x6004])
    press_enter()
    assert bytes(machine.banks[6][4:0x6004]) == before_full_rejection, \
        "A fourth nearby item must not evict or reorder the three carried items"
    assert machine.border == 5, "A full-stack rejection should show the amber feedback border"
    check_background(inventory)

    # Move clear of the fourth item, then pop chest, coin and axe in reverse
    # insertion order. Each successful pop commits a supported world position.
    while x + 17 >= world_x[3] * 2:
        run_frame(-1, {0xFEFE: 0x02})
    for expected_item in (0, 1, 2):
        run_frame()
        assert not any(
            world_rooms[item] == 1 and
            inventory_item_overlaps(
                x, y, world_x[item], world_y[item], widths[item], heights[item])
            for item in range(4)
        ), "Drop precedence requires a clear player rectangle"
        drop = safe_drop_position(
            collision, x, y, widths[expected_item], heights[expected_item])
        assert drop is not None, f"Expected a safe supported location for item {expected_item}"
        press_enter()
        world_x[expected_item], world_y[expected_item] = drop
        world_rooms[expected_item] = 1
        inventory.pop()
        expected = check_background(inventory)
        assert world_rooms[expected_item] == 1
        if inventory:
            # Move left far enough to avoid immediately picking up the item
            # just dropped; the next Enter therefore pops the new stack top.
            target = min(world_x[expected_item] * 2 - 20, x - 1)
            while x > target:
                run_frame(-1, {0xFEFE: 0x02})

    assert inventory == [], "The final pop should empty all three frames"
    while x < 180:
        run_frame(1, {0xFEFE: 0x04})
    run_frame()
    assert not any(
        world_rooms[item] == 1 and
        inventory_item_overlaps(
            x, y, world_x[item], world_y[item], widths[item], heights[item])
        for item in range(4)
    ), "The empty-stack check must be away from every visible item"
    before_empty_rejection = bytes(machine.banks[6][4:0x6004])
    press_enter()
    assert bytes(machine.banks[6][4:0x6004]) == before_empty_rejection, \
        "An empty-stack action must leave every world record and HUD frame unchanged"
    assert machine.border == 5

    symbols = {
        name: int(value)
        for name, value in re.findall(
            r"V(\w+)\s+p\d+\s+I(\d+)",
            (output / "program.symbols").read_text(),
        )
    }
    assembly = (output / "program.asm").read_text()
    runtime = (output / "sam-runtime.asm").read_text()
    assert symbols["sam_cli_image_end"] < 0xE000
    assert "CALL sam_draw_packed_outline" in assembly
    assert "sam_draw_packed_outline:" in runtime
    assert "CALL sam_compose_transparent_image" in assembly
    assert "sam_draw_rle_masked_sprite:" not in runtime
    print("PASS Blog 19: named item artwork, three-slot push/reject/LIFO pop, safe supports and empty rejection")


def verify_unsafe_inventory_drop(reference):
    output = ROOT / "tutorials" / "Blog19" / "21-2-UnsafeDrop" / "out"
    machine = PagedMachine(output)
    assets = reference / "assembly" / "blog10" / "assets"
    backgrounds = [
        (assets / f"screen-{room}.bin").read_bytes() for room in (1, 2)
    ]
    collisions = [
        (assets / f"screen-{room}-collision.bin").read_bytes()
        for room in (1, 2)
    ]
    item_images = [
        (reference / "assembly" / "blog18" / "assets" / "image-215.bin").read_bytes(),
        (reference / "assembly" / "blog19" / "assets" / "image-216.bin").read_bytes(),
        (reference / "assembly" / "blog19" / "assets" / "image-211.bin").read_bytes(),
        (reference / "assembly" / "blog19" / "assets" / "image-231.bin").read_bytes(),
    ]
    world_x = [24, 40, 60, 80]
    world_y = [111, 110, 111, 110]
    world_rooms = [1, 1, 255, 1]
    widths = [10, 11, 10, 10]
    heights = [16, 17, 16, 16]
    inventory = [2]
    x, y, velocity, grounded, phase = 34, 87, 0, True, 0

    def run_frame(direction=0, keys=None):
        nonlocal x, y, velocity, grounded, phase
        if direction:
            step = 1 if phase == 0 else 3
            phase = 1 - phase
            destination = max(0, min(230, x + direction * step))
            if grounded:
                x, y, grounded = move_grounded(
                    collisions[1], x, y, grounded, destination)
            else:
                x = move_airborne(collisions[1], x, y, destination)
        y, velocity, grounded, _ = apply_gravity(
            collisions[1], x, y, velocity, grounded)
        machine.frame(keys)
        assert machine.bank == 0

    def press_enter():
        run_frame(keys={0xBFFE: 0x01})
        run_frame()

    def expected_background():
        return inventory_background(
            backgrounds[1], item_images, world_x, world_y, world_rooms,
            inventory, active_room=2)

    def assert_scene():
        expected = expected_background()
        assert bytes(machine.banks[6][4:0x6004]) == expected
        for page in (10, 14):
            assert bytes(machine.banks[page][:48 * 128]) == expected[:48 * 128]
        return expected

    machine.frame()
    assert grounded and (x, y) == (34, 87)
    expected = assert_scene()
    initial_drop = safe_drop_position(collisions[1], x, y, widths[2], heights[2])
    assert initial_drop is None, "The fixture cliff position must not have a safe axe rectangle"
    press_enter()
    assert bytes(machine.banks[6][4:0x6004]) == expected, \
        "Unsafe placement must retain both the axe and its HUD icon"
    assert machine.border == 5, "Unsafe placement should signal rejection"

    # Keep walking until gravity carries the player to a supported beach area.
    # Do not let the fixture take the right exit while finding a safe location.
    safe = None
    for _ in range(90):
        safe = safe_drop_position(collisions[1], x, y, widths[2], heights[2]) if grounded else None
        if safe is not None:
            break
        run_frame(1, {0xFEFE: 0x04})
    assert safe is not None and grounded, \
        f"The player never reached a safe supported drop point: X={x}, Y={y}"
    run_frame()
    safe = safe_drop_position(collisions[1], x, y, widths[2], heights[2])
    assert safe is not None
    press_enter()
    inventory.clear()
    world_x[2], world_y[2] = safe
    world_rooms[2] = 2
    expected = assert_scene()
    assert machine.border != 5, "Successful placement should clear rejection feedback"
    assembly = (output / "program.asm").read_text()
    runtime = (output / "sam-runtime.asm").read_text()
    assert "CALL sam_any_solid_pixel_row" in assembly
    assert "sam_draw_rle_masked_sprite:" not in runtime
    print("PASS Blog 19: cliff drop keeps axe, supported-room drop commits item and clears HUD")


def verify_chest_collection(reference):
    output = ROOT / "tutorials" / "Blog18" / "20-1-CollectingChest" / "out"
    machine = PagedMachine(output)
    assets = reference / "assembly" / "blog10" / "assets"
    beach = (assets / "screen-1.bin").read_bytes()
    chest = (reference / "assembly" / "blog18" / "assets" / "image-215.bin").read_bytes()
    with_chest = compose_chest(beach, chest)
    source = reference / "assembly" / "blog6" / "tidizzy_cropped.asm"
    kind, (frames, _) = frame_source(source, reference)
    assert kind == "frames"

    actual = machine.frame()
    assert actual == scene(with_chest, frames[0][1], 120, 106), \
        "The exact source-derived chest must be composed below the starting sprite"
    assert bytes(machine.banks[6][4:0x6004]) == with_chest, \
        "The banked authoritative background must include the chest before collection"
    for page in (10, 14):
        pixels = bytes(machine.banks[page][:0x6000])
        assert pixels in (with_chest, scene(with_chest, frames[0][1], 120, 106))

    # A distant and an airborne press are consumed without granting the item.
    machine.frame({0xBFFE: 0x01})
    machine.frame()
    assert bytes(machine.banks[6][4:0x6004]) == with_chest
    airborne = PagedMachine(output)
    airborne.frame()
    airborne.frame({0x7FFE: 0x01, 0xBFFE: 0x01})
    airborne.frame()
    for _ in range(70):
        airborne.frame()
    assert bytes(airborne.banks[6][4:0x6004]) == with_chest, \
        "An Enter press during the jump must not be queued until landing"

    collision = (reference / "assembly" / "blog14" / "assets" / "screen-1-collision.bin").read_bytes()
    x, y, phase = 120, 106, 0
    for _ in range(33):
        step = 1 if phase == 0 else 3
        phase = 1 - phase
        x, y, grounded = move_grounded(collision, x, y, True, max(0, x - step))
        y, velocity, grounded, _ = apply_gravity(collision, x, y, 0, grounded)
        assert grounded and velocity == 0
        machine.frame({0xFEFE: 0x02})
    machine.frame()
    assert 31 <= x < 60 and 91 <= y < 123, f"Test player did not overlap the chest: {x}, {y}"

    actual = machine.frame({0xBFFE: 0x01})
    assert bytes(machine.banks[6][4:0x6004]) == beach, \
        "Fresh Enter while grounded, stopped and overlapping must remove the chest"
    plain = set((beach, *(scene(beach, frame[1], x, y) for frame in frames)))
    assert all(bytes(machine.banks[page][:0x6000]) in plain for page in (10, 14)), \
        "Both display buffers must contain the chest-free background (one may also contain Dizzy)"
    assert actual in plain, "The displayed post-collection frame must use the chest-free background"
    machine.frame({0xBFFE: 0x01})
    machine.frame({0xBFFE: 0x01})
    assert bytes(machine.banks[6][4:0x6004]) == beach, "Holding Enter must not recollect the chest"

    # Leave for screen 2, then return: persistent state must omit the chest.
    machine.frame()
    for _ in range(30):
        machine.frame({0xFEFE: 0x02})
    cliff = (assets / "screen-2.bin").read_bytes()
    assert bytes(machine.banks[6][4:0x6004]) == cliff, "The left exit must still load screen 2"
    for _ in range(90):
        machine.frame({0xFEFE: 0x04})
    assert bytes(machine.banks[6][4:0x6004]) == beach, \
        "Returning to the starting beach must not recreate the collected chest"
    assert bytes(machine.banks[12][4:0x6004]) == collision, \
        "Returning to the starting beach must restore its matching collision plane"

    symbols = {
        name: int(value)
        for name, value in re.findall(
            r"V(\w+)\s+p\d+\s+I(\d+)",
            (output / "program.symbols").read_text(),
        )
    }
    assert symbols["sam_cli_image_end"] < 0xE000
    assembly = (output / "program.asm").read_text()
    runtime = (output / "sam-runtime.asm").read_text()
    assert "CALL sam_compose_transparent_image" in assembly
    assert "sam_compose_transparent_image:" in runtime
    assert "sam_draw_rle_masked_sprite:" not in runtime
    print("PASS Blog 18: exact chest pixels, gated Enter pickup, both buffers and persistent return")


def main():
    settings = json.loads((ROOT / "tools.local.json").read_text(encoding="utf-8-sig"))
    reference = Path(settings["referenceRepo"])
    verify_sprite_edge_limits(reference)
    verify_beach(reference)
    verify_restoration(reference)
    verify_room_composition(reference)
    verify_horizontal_collision(reference)
    verify_ground_following(reference)
    verify_gravity_and_landing(reference)
    verify_jumping(reference)
    verify_connected_rooms(reference)
    verify_chest_collection(reference)
    verify_visible_inventory(reference)
    verify_unsafe_inventory_drop(reference)


if __name__ == "__main__":
    main()
