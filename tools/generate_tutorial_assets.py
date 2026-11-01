#!/usr/bin/env python3
"""Generate C# byte-array fields from the read-only tutorial sources."""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
TUTORIALS = [
    ("Blog02/01-PrintingText", "blog2/1.asm", 0, False, False),
    ("Blog02/02-ScreenWrites", "blog2/2.asm", 0, False, False),
    ("Blog02/03-OpaqueSprites", "blog2/3.asm", 0, False, False),
    ("Blog02/03-2-OpaqueSpriteCopy", "blog2/3-2.asm", 0, False, False),
    ("Blog03/04-BuildMaskedSprite", "blog3/4.asm", 0, False, False),
    ("Blog03/04-2-StripedBackground", "blog3/4-2.asm", 0, False, False),
    ("Blog03/05-InterleavedSprite", "blog3/5.asm", 1, False, False),
    ("Blog03/05-2-StripedBackground", "blog3/5-2.asm", 1, False, False),
    ("Blog04/06-1-TwoFrames", "blog4/6-1.asm", 2, False, False),
    ("Blog04/06-2-ExternalFrames", "blog4/6-2.asm", 2, False, False),
    ("Blog04/06-3-AnimationTable", "blog4/6-3.asm", 9, False, False),
    ("Blog04/06-4-DoubleBuffer", "blog4/6-4.asm", 9, False, False),
    ("Blog05/07-1-KeyboardJump", "blog5/7-1.asm", 9, True, False),
    ("Blog05/07-2-SpaceJump", "blog5/7-2.asm", 9, True, False),
    ("Blog05/07-3-WalkAndJump", "blog5/7-3.asm", 24, True, False),
    ("Blog06/08-1-OriginalPalette", "blog6/8-1.asm", 24, True, True),
    ("Blog06/08-2-ChangeBackground", "blog6/8-2.asm", 24, True, True),
    ("Blog06/08-3-CroppedSidewaysJump", "blog6/8-3.asm", 0, True, True),
    ("Blog07/09-1-BankedSpriteData", "blog7/9-1.asm", 0, False, True),
    ("Blog08/10-1-ByteStepMovement", "blog8/10-1.asm", 0, False, True),
    ("Blog09/11-1-PixelPositioning", "blog9/11-1.asm", 0, False, True),
]

BANKED_PAGES = {
    "Blog07/09-1-BankedSpriteData": 8,
    "Blog08/10-1-ByteStepMovement": 8,
    "Blog09/11-1-PixelPositioning": 8,
}

BINARY_TUTORIALS = {
    "Blog10/12-1-StaticPalm": ("blog10/assets/graphics/image-6.bin", [("PalmGraphic", 8, 41 * 77 + 2)]),
    "Blog10/12-2-StartingBeach": (
        "blog10/assets/screen-1.bin",
        [("BeachScreenTop", 8, 0x3000), ("BeachScreenBottom", 10, 0x3000)],
    ),
}

INVENTORY_ITEM_ASSETS = {
    "Blog19/21-1-VisibleInventory": [
        ("ChestOffset", "blog18/assets/image-215.bin", 10, 16),
        ("CoinOffset", "blog19/assets/image-216.bin", 11, 17),
        ("AxeOffset", "blog19/assets/image-211.bin", 10, 16),
        ("ScrollOffset", "blog19/assets/image-231.bin", 10, 16),
    ],
}

WATER_TUTORIALS = {
    "Blog22/24-1-AnimatedWater": (
        "assets/screen-1-waterless.bin",
        "Room1Background",
    ),
}

BEE_TUTORIALS = {
    "Blog25/27-1-MovingBee",
    "Blog26/28-1-BeeHazard",
    "Blog27/29-1-CompleteRoute",
}


def parse_byte(token: str) -> int:
    token = token.strip()
    if token.startswith("&"):
        return int(token[1:], 16)
    if token.lower().startswith("0x"):
        return int(token, 16)
    if token.lower().startswith("0b"):
        return int(token, 2)
    return int(token, 10)


def data_after_label(text: str, label: str, limit: int | None = None) -> list[int]:
    match = re.search(rf"(?im)^\s*{re.escape(label)}\s*:", text)
    if match is None:
        raise ValueError(f"Sprite label '{label}' was not found.")
    tail = text[match.end() :]
    values: list[int] = []
    for line in tail.splitlines():
        directive = re.search(r"\bDEFB\s+([^;]+)", line, re.IGNORECASE)
        if directive:
            values.extend(parse_byte(token) for token in directive.group(1).split(","))
            if limit is not None and len(values) >= limit:
                break
    if limit is not None:
        values = values[:limit]
    return values


def frame_data(text: str, label_pattern: str, expected_bytes: int) -> list[tuple[str, list[int]]]:
    matches = list(re.finditer(rf"(?im)^\s*({label_pattern})\s*:", text))
    frames: list[tuple[str, list[int]]] = []
    for index, match in enumerate(matches):
        end = matches[index + 1].start() if index + 1 < len(matches) else len(text)
        values: list[int] = []
        for line in text[match.end() : end].splitlines():
            directive = re.search(r"\bDEFB\s+([^;]+)", line, re.IGNORECASE)
            if directive:
                values.extend(parse_byte(token) for token in directive.group(1).split(","))
        if values:
            if len(values) != expected_bytes:
                raise ValueError(
                    f"{match.group(1)} has {len(values)} bytes; expected {expected_bytes}."
                )
            frames.append((match.group(1), values))
    return frames


def rle(values: list[int]) -> list[int]:
    encoded: list[int] = []
    start = 0
    while start < len(values):
        end = start + 1
        while end < len(values) and values[end] == values[start] and end - start < 127:
            end += 1
        if end - start >= 3:
            encoded.extend((end - start, values[start]))
            start = end
        else:
            end = start
            while end < len(values) and end - start < 127:
                if end + 2 < len(values) and values[end] == values[end + 1] == values[end + 2]:
                    break
                end += 1
            encoded.extend((0x80 | (end - start), *values[start:end]))
            start = end
    encoded.append(0)
    return encoded


def tutorial_palette(reference_root: Path) -> list[int]:
    path = reference_root / "assembly" / "blog6" / "sam_palette.asm"
    text = path.read_text(encoding="utf-8")
    values = data_after_label(text, "SAM_Palette", 16)
    if len(values) != 16:
        raise ValueError(f"{path} must contain 16 CLUT entries.")
    return values


def tutorial_room_data(reference_root: Path) -> list[tuple[int, bytes, list[int]]]:
    """Flatten each clipped room's used graphics and translate its source offsets."""
    assets = reference_root / "assembly" / "blog12" / "assets"
    graphics_text = (assets / "room-graphics.asm").read_text(encoding="utf-8")
    graphics: dict[str, bytes] = {}
    for line in graphics_text.splitlines():
        label = re.match(r"\s*(Room_Image_\d+)\s*:", line, re.IGNORECASE)
        if label:
            current = label.group(1)
            continue
        image = re.search(r'MDAT\s+"graphics\\image-(\d+)\.bin"', line, re.IGNORECASE)
        if image:
            name = f"Room_Image_{image.group(1)}"
            if name != current:
                raise ValueError(f"{name} MDAT does not follow its matching room label.")
            graphics[name] = (assets / "graphics" / f"image-{image.group(1)}.bin").read_bytes()
    if not graphics:
        raise ValueError(f"No MDAT graphics were found in {assets / 'room-graphics.asm'}.")

    text = re.sub(r";[^\r\n]*", "", (assets / "room-data.asm").read_text(encoding="utf-8"))
    headers = list(re.finditer(r"(?im)^\s*Room_(\d+):\s*DEFB\s+(\d+)", text))
    rooms: list[tuple[int, bytes, list[int]]] = []
    for index, header in enumerate(headers):
        room_number = int(header.group(1))
        count = int(header.group(2))
        end = headers[index + 1].start() if index + 1 < len(headers) else len(text)
        body = text[header.end():end]
        matches = list(re.finditer(
            r"DEFW\s+(Room_Image_\d+)\s*(?:\+\s*(\d+))?\s+"
            r"DEFW\s+(\d+)\s+DEFB\s+(\d+)\s*,\s*(\d+)\s*,\s*(\d+)",
            body,
            re.IGNORECASE,
        ))
        if len(matches) != count:
            raise ValueError(f"Room {room_number} declares {count} blocks but has {len(matches)} records.")

        used_names = list(dict.fromkeys(match.group(1) for match in matches))
        offsets: dict[str, int] = {}
        room_graphics = bytearray()
        for name in used_names:
            if name not in graphics:
                raise ValueError(f"Room {room_number} references missing {name} data.")
            offsets[name] = len(room_graphics)
            room_graphics.extend(graphics[name])
        if len(room_graphics) > 0x7FFE:
            raise ValueError(f"Room {room_number} graphics require {len(room_graphics)} bytes; maximum is 32766.")

        records = [count]
        for match in matches:
            name = match.group(1)
            source_offset = offsets[name] + int(match.group(2) or 0)
            destination, width, height, stride = map(int, match.groups()[2:])
            if source_offset + max(0, height - 1) * stride + width > len(room_graphics):
                raise ValueError(f"Room {room_number} has a clipped source range outside {name}.")
            records.extend((
                source_offset & 0xFF, source_offset >> 8,
                destination & 0xFF, destination >> 8,
                width, height, stride,
            ))
        rooms.append((room_number, bytes(room_graphics), records))
    if not rooms:
        raise ValueError("No room records were found.")
    return rooms


def render_room_data(reference_root: Path) -> str:
    fields = []
    for room_number, graphics, records in tutorial_room_data(reference_root):
        fields.append(binary_field(f"Room{room_number}Graphics", graphics, 8))
        fields.append(csharp_field(f"Room{room_number}Records", records, row_bytes=1))
    fields.extend([
        binary_field("Background", bytes(0x6000), 6),
        csharp_field("GraphicsRow", [0] * 128),
        csharp_field("BlankRow", [0] * 128),
    ])
    return "internal static class TutorialRooms\n{\n" + "\n\n".join(fields) + "\n}\n"


def render_room_background(values: bytes, class_name: str = "TutorialRooms") -> str:
    if len(values) != 0x6000:
        raise ValueError(f"A MODE 4 room background must contain 24576 bytes, got {len(values)}.")
    fields = [
        binary_field("Background", values, 6),
        csharp_field("GraphicsRow", [0] * 128),
    ]
    return f"internal static class {class_name}\n{{\n" + "\n\n".join(fields) + "\n}\n"


def compose_room_background(
    reference_root: Path,
    room_number: int,
    skipped_destinations: set[int] | None = None,
) -> bytes:
    """Compose one room's source records, optionally leaving animated strips clear."""
    room = next(
        (entry for entry in tutorial_room_data(reference_root) if entry[0] == room_number),
        None,
    )
    if room is None:
        raise ValueError(f"Room {room_number} has no source draw records.")
    _, graphics, records = room
    skipped = skipped_destinations or set()
    screen = bytearray(0x6000)
    count = records[0]
    cursor = 1
    for _ in range(count):
        source = records[cursor] | records[cursor + 1] << 8
        destination = records[cursor + 2] | records[cursor + 3] << 8
        width, height, stride = records[cursor + 4 : cursor + 7]
        cursor += 7
        if destination in skipped:
            continue
        for row in range(height):
            source_row = source + row * stride
            destination_row = destination + row * 128
            for column in range(width):
                pixel = graphics[source_row + column]
                offset = destination_row + column
                previous = screen[offset]
                high = pixel & 0xF0 if pixel & 0xF0 else previous & 0xF0
                low = pixel & 0x0F if pixel & 0x0F else previous & 0x0F
                screen[offset] = high | low
    return bytes(screen)


def render_water_frames(asset_directory: Path) -> str:
    """Build two banked, transparent sprite sizes from the four source wave frames."""
    frames: list[bytes] = []
    for image_number in range(55, 59):
        source = asset_directory / f"image-{image_number}.bin"
        image = source.read_bytes()
        if image[:2] != bytes((20, 4)) or len(image) != 82:
            raise ValueError(f"{source} must be a 20-byte by 4-row packed image.")
        frames.append(image[2:])

    def interleaved(first_column: int) -> list[int]:
        values = [len(frames)]
        for frame in frames:
            for row in range(4):
                for column in range(first_column, first_column + 10):
                    pixel = frame[row * 20 + column]
                    mask = (0xF0 if pixel & 0xF0 == 0 else 0) | (0x0F if pixel & 0x0F == 0 else 0)
                    values.extend((mask, pixel))
        return values

    left_buffer = [1] + [0] * 80
    right_buffer = [1] + [0] * 80
    left_buffer.extend([0] * (-len(left_buffer) % 16))
    right_buffer.extend([0] * (-len(right_buffer) % 16))
    fields = [
        binary_field("LeftFrames", bytes(interleaved(0)), 8),
        binary_field("RightFrames", bytes(interleaved(10)), 8),
        csharp_field("LeftFrameBuffer", left_buffer),
        csharp_field("RightFrameBuffer", right_buffer),
    ]
    return "internal static class WaterFramesData\n{\n" + "\n\n".join(fields) + "\n}\n"


def render_bee_frames(asset_directory: Path) -> str:
    """Convert the four source bee pictures into resident mask/pixel frames."""
    frames: list[tuple[str, list[int]]] = []
    for image_number in range(327, 331):
        source = asset_directory / f"image-{image_number}.bin"
        image = source.read_bytes()
        if image[:2] != bytes((11, 22)) or len(image) != 244:
            raise ValueError(f"{source} must be an 11-byte by 22-row packed image.")
        values: list[int] = []
        for pixel in image[2:]:
            mask = (0xF0 if pixel & 0xF0 == 0 else 0) | \
                   (0x0F if pixel & 0x0F == 0 else 0)
            values.extend((mask, pixel))
        frames.append((f"Bee_{image_number}", values))

    lines = ["internal static class BeeFramesData",
             "{",
             "    // Four 11-byte by 22-row frames; each source colour byte becomes",
             "    // a mask/pixel pair so zero nibbles preserve the existing scene.",
             "    internal static readonly byte[] Frames = new byte[]",
             "    {",
             "        4, // Frame count"]
    for index, (name, values) in enumerate(frames):
        lines.extend(["", f"        #region Frame {index:02d} - {name}",
                      "        // 11 screen bytes x 22 rows; each byte is mask then pixel."])
        lines.extend(sprite_rows(values, 22))
        lines.append("        #endregion")
    lines.extend(["    };", "}", ""])
    return "\n".join(lines)


def frame_source(source: Path, reference_root: Path):
    text = source.read_text(encoding="utf-8")
    cropped = "cropped" in text.lower()
    if "Sprite1_Pixels:" in text and "Sprite1_Mask:" in text:
        pixel_text = text.split("Sprite1_Pixels:", 1)[1].split("Sprite1_Mask:", 1)[0]
        mask_text = text.split("Sprite1_Mask:", 1)[1].split("Sprite1_Interleaved:", 1)[0]
        pixels = [parse_byte(token) for line in pixel_text.splitlines()
                  if (m := re.search(r"\bDEFB\s+([^;]+)", line, re.I))
                  for token in m.group(1).split(",")]
        masks = [parse_byte(token) for line in mask_text.splitlines()
                 if (m := re.search(r"\bDEFB\s+([^;]+)", line, re.I))
                 for token in m.group(1).split(",")]
        if len(pixels) != 512 or len(masks) != 512:
            raise ValueError(f"{source} must contain 512 separate pixel and mask bytes.")
        return "separate", (pixels, masks)

    frame_labels = r"(?im)^\s*(?:TIDizzy_\d+_Interleaved|Dizzy_System_Interleaved_Image\d+)\s*:"
    if not re.search(frame_labels, text):
        includes = re.findall(r'(?im)^\s*INCLUDE\s+["\']([^"\']+)["\']', text)
        for include_name in includes:
            include = (source.parent / include_name).resolve()
            if not include.exists():
                include = (reference_root / "assembly" / source.parent.name / include_name).resolve()
            if include.exists():
                candidate = include.read_text(encoding="utf-8")
                if re.search(frame_labels, candidate):
                    text = candidate
                    break

    if re.search(r"(?im)^\s*TIDizzy_\d+_Interleaved\s*:", text):
        frames = frame_data(text, r"TIDizzy_\d+_Interleaved", 572 if cropped else 1024)
    elif re.search(r"(?im)^\s*Dizzy_System_Interleaved_Image\d+\s*:", text):
        frames = frame_data(text, r"Dizzy_System_Interleaved_Image\d+", 1024)
    elif re.search(r"(?im)^\s*Sprite2_Interleaved\s*:", text):
        frames = []
        for label in ("Sprite1_Interleaved", "Sprite2_Interleaved"):
            frames.append((label, data_after_label(text, label, 1024)))
        for label, values in frames:
            if len(values) != 1024:
                raise ValueError(f"{label} contains {len(values)} bytes; expected 1024.")
    elif re.search(r"(?im)^\s*Sprite1_Interleaved\s*:", text):
        frames = [("Sprite1_Interleaved", data_after_label(text, "Sprite1_Interleaved", 1024))]
        if len(frames[0][1]) != 1024:
            raise ValueError("Sprite1_Interleaved must contain 1024 bytes.")
    else:
        values = data_after_label(text, "Sprite1_1", 512)
        if len(values) != 512:
            raise ValueError(f"Opaque sprite contains {len(values)} bytes; expected 512.")
        return "opaque", values
    return "frames", (frames, text)


def byte_row(values: list[int], indent: str = "        ") -> str:
    return indent + ", ".join(f"0x{value:02X}" for value in values) + ","


def sprite_rows(values: list[int], row_bytes: int) -> list[str]:
    if len(values) % row_bytes:
        raise ValueError(f"Sprite has {len(values)} bytes; rows must contain {row_bytes} bytes.")
    return [byte_row(values[start:start + row_bytes]) + f" // Row {start // row_bytes:02d}"
            for start in range(0, len(values), row_bytes)]


def csharp_field(name: str, values: list[int], row_bytes: int = 16) -> str:
    if not values:
        raise ValueError(f"{name} cannot be empty.")
    lines = [f"    internal static readonly byte[] {name} = new byte[]"]
    lines.append("    {")
    lines.extend(sprite_rows(values, row_bytes))
    lines.append("    };")
    return "\n".join(lines)


def binary_field(name: str, values: bytes, banked_page: int) -> str:
    if not values:
        raise ValueError(f"{name} cannot be empty.")
    lines = [f"    [SamCoupe.Sdk.BankedData({banked_page})]",
             f"    internal static readonly byte[] {name} = new byte[]", "    {"]
    lines.extend(byte_row(list(values[start:start + 16])) + f" // Offset 0x{start:04X}"
                 for start in range(0, len(values), 16))
    lines.append("    };")
    return "\n".join(lines)


def render_inventory_item_assets(reference_root: Path) -> str:
    offsets: list[tuple[str, int]] = []
    pixels = bytearray()
    for field_name, source_name, width, height in INVENTORY_ITEM_ASSETS["Blog19/21-1-VisibleInventory"]:
        image = (reference_root / "assembly" / Path(source_name)).read_bytes()
        if image[:2] != bytes((width, height)) or len(image) != 2 + width * height:
            raise ValueError(f"{source_name} is not the expected {width}x{height} packed image.")
        offsets.append((field_name, len(pixels)))
        pixels.extend(image[2:])

    fields = [f"    internal static readonly ushort {name} = {offset};" for name, offset in offsets]
    fields.append(binary_field("Pixels", bytes(pixels), 8))
    return "internal static class TutorialItemAssets\n{\n" + "\n\n".join(fields) + "\n}\n"


def collision_runs(values: bytes) -> list[int]:
    encoded: list[int] = []
    start = 0
    while start < len(values):
        end = start + 1
        while end < len(values) and values[end] == values[start] and end - start < 0xFFFF:
            end += 1
        count = end - start
        encoded.extend((count & 0xFF, count >> 8, values[start]))
        start = end
    encoded.extend((0, 0))
    return encoded


def frame_field(frames: list[tuple[str, list[int]]], compress: bool,
                banked_page: int | None = None) -> str:
    values = frame_container(frames, compress)
    lines = []
    if banked_page is not None:
        lines.append(f"    [SamCoupe.Sdk.BankedData({banked_page})]")
    lines.extend(["    internal static readonly byte[] SpriteFrames = new byte[]", "    {",
             f"        0x{len(frames):02X}, // Frame count"]
                 )
    if compress:
        lines.append("        // Directory: mask offset, pixel offset (little-endian, payload-relative).")
        for index, (name, _) in enumerate(frames):
            lines.append(byte_row(values[1 + 4 * index:5 + 4 * index]) + f" // Frame {index:02d}: {name}")

    for index, (name, pixels) in enumerate(frames):
        if len(pixels) not in (1024, 572):
            raise ValueError(f"{name} has unsupported interleaved length {len(pixels)}.")
        width, height = (13, 22) if len(pixels) == 572 else (16, 32)
        lines.extend(["", f"        #region Frame {index:02d} - {name}",
                      f"        // {width} screen bytes x {height} rows; {len(pixels)} mask/pixel bytes.",
                      "        // Each pair is mask, pixel."])
        if not compress:
            lines.extend(sprite_rows(pixels, width * 2))
        else:
            lines.append("        // Decoded rows (reference view; only the packet streams below are emitted):")
            for row in range(height):
                start = row * width * 2
                masks = pixels[start:start + width * 2:2]
                colours = pixels[start + 1:start + width * 2:2]
                lines.append(byte_row(masks, f"        // Row {row:02d} mask:  "))
                lines.append(byte_row(colours, f"        // Row {row:02d} pixel: "))
            for plane_name, plane in (("Mask", pixels[::2]), ("Pixel", pixels[1::2])):
                stream = rle(plane)
                lines.append(f"        // {plane_name} packet stream: {len(stream)} encoded bytes -> {width * height} decoded bytes.")
                cursor = 0
                while cursor < len(stream):
                    packet = stream[cursor]
                    count = 1 if packet == 0 else (1 + (packet & 0x7F) if packet & 0x80 else 2)
                    packet_bytes = stream[cursor:cursor + count]
                    annotation = ("End plane" if packet == 0 else
                                  f"{'Literal' if packet & 0x80 else 'Repeat'} {packet & 0x7F}")
                    for start in range(0, count, width):
                        lines.append(byte_row(packet_bytes[start:start + width]) +
                                     (f" // {annotation}" if start == 0 else " // Packet continued"))
                    cursor += count
        lines.append("        #endregion")
    lines.append("    };")
    return "\n".join(lines)


def frame_container(frames: list[tuple[str, list[int]]], compress: bool) -> list[int]:
    if not 1 <= len(frames) <= 255:
        raise ValueError("A frame data set must contain 1 to 255 frames.")
    if not compress:
        return [len(frames), *(value for _, values in frames for value in values)]

    encoded: list[tuple[list[int], list[int]]] = [
        (rle(values[::2]), rle(values[1::2])) for _, values in frames
    ]
    cursor = 1 + 4 * len(frames)
    directory: list[int] = []
    streams: list[int] = []
    for masks, pixels in encoded:
        mask_offset = cursor
        streams.extend(masks)
        cursor += len(masks)
        pixel_offset = cursor
        streams.extend(pixels)
        cursor += len(pixels)
        if cursor > 0xFFFF:
            raise ValueError("Compressed frame data and directory must fit in 65535 bytes.")
        directory.extend((mask_offset & 0xFF, mask_offset >> 8, pixel_offset & 0xFF, pixel_offset >> 8))
    return [len(frames), *directory, *streams]


def render_source(kind: str, payload, frames_limit: int, compress: bool,
                  include_palette: bool, reference_root: Path,
                  banked_page: int | None = None,
                  include_odd_frame_buffer: bool = False) -> str:
    fields: list[str] = []
    if kind == "opaque":
        fields.append(csharp_field("OpaqueSprite", payload))
    elif kind == "separate":
        pixels, masks = payload
        fields.append(csharp_field("SpritePixels", pixels))
        fields.append(csharp_field("SpriteMasks", masks))
    else:
        frames, _ = payload
        if frames_limit:
            frames = frames[:frames_limit]
        fields.append(frame_field(frames, compress, banked_page))
        if banked_page is not None:
            frame_size = len(frames[0][1])
            fields.append(csharp_field("FrameBuffer", [1, *([0] * frame_size)], 1))
            if include_odd_frame_buffer:
                fields.append(csharp_field("OddFrameBuffer", [1, *([0] * (14 * 22 * 2))], 1))
    if include_palette:
        fields.append(csharp_field("Palette", tutorial_palette(reference_root)))
    return "internal static class TutorialData\n{\n" + "\n\n".join(fields) + "\n}\n"


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--reference-repo", type=Path,
                        help="Reference repository (defaults to tools.local.json)")
    parser.add_argument("--source", type=Path,
                        help="Reference assembly example whose sprite data is required")
    parser.add_argument("--output", type=Path,
                        help="C# source file to generate")
    parser.add_argument("--frames", type=int, default=0,
                        help="Keep only the first N frames (zero means all)")
    parser.add_argument("--rle", action="store_true",
                        help="Split interleaved frames into compressed mask/pixel planes")
    parser.add_argument("--bank-page", type=int,
                        help="Emit frame data into this physical lower-memory page")
    parser.add_argument("--odd-frame-buffer", action="store_true",
                        help="Include the 14-byte odd-pixel staging array")
    parser.add_argument("--palette", action="store_true",
                        help="Include the original Blog 6 CLUT values")
    parser.add_argument("--all", action="store_true", help="Generate every tutorial's local C# data")
    parser.add_argument("--program", type=Path, help="Generate missing data for a known tutorial folder")
    parser.add_argument("--binary-field", help="Generate one explicitly named raw binary field")
    parser.add_argument("--rle-field", help="Generate one explicitly named resident byte-run field")
    parser.add_argument("--packet-rle-field", help="Generate one explicitly named packet-RLE banked field")
    parser.add_argument("--class-name", default="TutorialData",
                        help="C# class name for generated binary fields")
    parser.add_argument("--room-data", action="store_true",
                        help="Generate flattened C# graphics and records for the four tutorial rooms")
    parser.add_argument("--room-background", type=int,
                        help="Generate one precomposed MODE 4 screen as a banked room background")
    parser.add_argument("--water-frames", action="store_true",
                        help="Generate C# sprite frames for the Blog 22 animated sea")
    parser.add_argument("--bee-frames", action="store_true",
                        help="Generate resident C# mask/pixel frames for the Blog 25 bee")
    parser.add_argument("--tools", type=Path, default=ROOT / "tools.local.json")
    args = parser.parse_args()
    try:
        reference_root = args.reference_repo
        if reference_root is None:
            settings = json.loads(args.tools.read_text(encoding="utf-8-sig"))
            reference_root = Path(settings["referenceRepo"])
            if not reference_root.is_absolute():
                reference_root = args.tools.resolve().parent / reference_root
        if args.binary_field:
            if args.source is None or args.output is None or args.bank_page is None:
                parser.error("--binary-field requires --source, --output and --bank-page")
            source = args.source if args.source.is_absolute() else reference_root / args.source
            values = source.read_bytes()
            generated = binary_field(args.binary_field, values, args.bank_page)
            output = f"internal static class {args.class_name}\n{{\n" + generated + "\n}\n"
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(output, encoding="ascii")
            print(f"Wrote {args.output}: {len(values)} binary bytes")
            return 0
        if args.rle_field:
            if args.source is None or args.output is None:
                parser.error("--rle-field requires --source and --output")
            source = args.source if args.source.is_absolute() else reference_root / args.source
            values = collision_runs(source.read_bytes())
            field = (
                binary_field(args.rle_field, bytes(values), args.bank_page)
                if args.bank_page is not None
                else csharp_field(args.rle_field, values)
            )
            output = f"internal static class {args.class_name}\n{{\n" + \
                field + "\n}\n"
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(output, encoding="ascii")
            print(f"Wrote {args.output}: {len(values)} run-encoded bytes")
            return 0
        if args.packet_rle_field:
            if args.source is None or args.output is None or args.bank_page is None:
                parser.error("--packet-rle-field requires --source, --output and --bank-page")
            source = args.source if args.source.is_absolute() else reference_root / args.source
            values = bytes(rle(list(source.read_bytes())))
            generated = binary_field(args.packet_rle_field, values, args.bank_page)
            output = f"internal static class {args.class_name}\n{{\n" + generated + "\n}\n"
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(output, encoding="ascii")
            print(f"Wrote {args.output}: {len(values)} packet-RLE bytes")
            return 0
        if args.room_data:
            if args.output is None:
                parser.error("--room-data requires --output")
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(render_room_data(reference_root), encoding="ascii")
            print(f"Wrote {args.output}: four C# room record sets and selected graphics")
            return 0
        if args.room_background is not None:
            if args.output is None:
                parser.error("--room-background requires --output")
            source = reference_root / "assembly" / "blog10" / "assets" / f"screen-{args.room_background}.bin"
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(
                render_room_background(source.read_bytes(), args.class_name),
                encoding="ascii",
            )
            print(f"Wrote {args.output}: 24576 background bytes")
            return 0
        if args.water_frames:
            if args.output is None:
                parser.error("--water-frames requires --output")
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(
                render_water_frames(reference_root / "assembly" / "blog22" / "assets"),
                encoding="ascii",
            )
            print(f"Wrote {args.output}: four source-derived wave frames")
            return 0
        if args.bee_frames:
            if args.output is None:
                parser.error("--bee-frames requires --output")
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(
                render_bee_frames(reference_root / "assembly" / "blog25" / "assets"),
                encoding="ascii",
            )
            print(f"Wrote {args.output}: four source-derived bee frames")
            return 0
        if args.all or args.program:
            for folder, (asset_name, field_name) in WATER_TUTORIALS.items():
                destination = ROOT / "tutorials" / Path(folder)
                if args.program and destination.resolve() != args.program.resolve():
                    continue
                asset = destination / asset_name
                if not asset.is_file():
                    continue
                target = destination / "Room1Background.g.cs"
                if not target.exists():
                    values = rle(list(asset.read_bytes()))
                    target.write_text(
                        "internal static class Room1BackgroundData\n{\n"
                        + binary_field(field_name, bytes(values), 8)
                        + "\n}\n",
                        encoding="ascii",
                    )
                    print(f"Generated water-free room background: {folder}")
                water_target = destination / "WaterFrames.g.cs"
                if not water_target.exists():
                    water_target.write_text(
                        render_water_frames(destination / "assets"),
                        encoding="ascii",
                    )
                    print(f"Generated C# water frames: {folder}")
            for folder in sorted(BEE_TUTORIALS):
                destination = ROOT / "tutorials" / Path(folder)
                if args.program and destination.resolve() != args.program.resolve():
                    continue
                target = destination / "BeeFrames.g.cs"
                if not target.exists():
                    target.write_text(
                        render_bee_frames(destination / "assets"),
                        encoding="ascii",
                    )
                    print(f"Generated C# bee frames: {folder}")
            for folder, (source_name, banked_fields) in BINARY_TUTORIALS.items():
                destination = ROOT / "tutorials" / Path(folder)
                if args.all or (args.program and destination.resolve() == args.program.resolve()):
                    target = destination / "TutorialData.g.cs"
                    if args.program and target.exists():
                        continue
                    source = reference_root / "assembly" / Path(source_name)
                    values = source.read_bytes()
                    expected_total = sum(expected_length for _, _, expected_length in banked_fields)
                    if len(values) != expected_total:
                        raise ValueError(f"{source} has {len(values)} bytes; expected {expected_total}.")
                    target.parent.mkdir(parents=True, exist_ok=True)
                    fields = []
                    offset = 0
                    for field_name, bank_page, expected_length in banked_fields:
                        fields.append(binary_field(field_name, values[offset:offset + expected_length], bank_page))
                        offset += expected_length
                    target.write_text(
                        "internal static class TutorialData\n{\n" +
                        "\n\n".join(fields) + "\n\n" +
                        csharp_field("RowBuffer", [0] * 128) + "\n\n" +
                        csharp_field("Palette", tutorial_palette(reference_root)) + "\n}\n",
                        encoding="ascii")
                    print(f"Generated local C# binary data: {folder}")
            for folder in INVENTORY_ITEM_ASSETS:
                destination = ROOT / "tutorials" / Path(folder)
                if args.program and destination.resolve() != args.program.resolve():
                    continue
                target = destination / "TutorialItemAssets.g.cs"
                if args.program and target.exists():
                    continue
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text(render_inventory_item_assets(reference_root), encoding="ascii")
                print(f"Generated local C# item rows: {folder}")
            for folder, source_name, count, compress, palette in TUTORIALS:
                destination = ROOT / "tutorials" / Path(folder)
                if args.program and destination.resolve() != args.program.resolve():
                    continue
                if folder.startswith(("Blog02/01-", "Blog02/02-")):
                    continue
                target = destination / "TutorialData.g.cs"
                if args.program and target.exists():
                    continue
                source = reference_root / "assembly" / Path(source_name)
                kind, payload = frame_source(source, reference_root)
                target.write_text(render_source(kind, payload, count, compress, palette, reference_root,
                                                BANKED_PAGES.get(folder),
                                                folder == "Blog09/11-1-PixelPositioning"),
                                  encoding="ascii")
                print(f"Generated local C# data: {folder}")
            return 0
        if args.source is None or args.output is None:
            parser.error("Supply --all, --program FOLDER, or both --source and --output")
        source = args.source if args.source.is_absolute() else reference_root / args.source
        kind, payload = frame_source(source.resolve(), reference_root.resolve())
        output = render_source(
            kind,
            payload,
            args.frames,
            args.rle,
            args.palette,
            reference_root.resolve(),
            args.bank_page,
            args.odd_frame_buffer,
        )
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(output, encoding="ascii")
        print(f"Wrote {args.output}: {kind}, {len(output)} C# characters")
        return 0
    except (OSError, ValueError) as error:
        print(f"Asset extraction failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
