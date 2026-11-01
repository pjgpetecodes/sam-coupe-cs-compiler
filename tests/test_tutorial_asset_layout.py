"""Sprite source layout tests use only synthetic, non-proprietary bytes."""
import importlib.util
import re
import unittest
from pathlib import Path

MODULE = Path(__file__).resolve().parents[1] / "tools" / "generate_tutorial_assets.py"
SPEC = importlib.util.spec_from_file_location("tutorial_assets", MODULE)
assets = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(assets)


def initializer_bytes(source):
    source = re.sub(r"//[^\n]*|#[^\n]*", "", source)
    return [int(value, 16) for value in re.findall(r"0x([0-9A-F]{2})", source)]


class SpriteLayoutTests(unittest.TestCase):
    def test_banked_binary_field_preserves_bytes_and_selected_page(self):
        values = bytes(range(256)) * 2
        source = assets.binary_field("BeachScreen", values, 4)
        self.assertIn("[SamCoupe.Sdk.BankedData(4)]", source)
        self.assertEqual(list(values), initializer_bytes(source))

    def test_precomposed_room_background_has_exact_mode4_footprint(self):
        values = bytes(range(256)) * 96
        source = assets.render_room_background(values)
        self.assertIn("internal static class TutorialRooms", source)
        self.assertIn("[SamCoupe.Sdk.BankedData(6)]", source)
        decoded = initializer_bytes(source)
        self.assertEqual(list(values), decoded[:0x6000])
        self.assertEqual([0] * 128, decoded[0x6000:])
        with self.assertRaises(ValueError):
            assets.render_room_background(values[:-1])

    def test_opaque_and_separate_planes_use_16_byte_rows(self):
        values = list(range(256)) * 2
        source = assets.csharp_field("SpritePixels", values)
        rows = [line for line in source.splitlines() if "// Row" in line]
        self.assertEqual(32, len(rows))
        self.assertTrue(all(len(re.findall(r"0x[0-9A-F]{2}", row)) == 16 for row in rows))
        self.assertEqual(values, initializer_bytes(source))
        with self.assertRaises(ValueError):
            assets.csharp_field("Invalid", [0] * 17)

    def test_named_uncompressed_frames_use_interleaved_screen_rows(self):
        frames = [("Idle01", [0xFF, 0x11] * 512), ("Jump02", [0, 0x22] * 512)]
        source = assets.frame_field(frames, False)
        self.assertIn("#region Frame 00 - Idle01", source)
        self.assertIn("#region Frame 01 - Jump02", source)
        rows = [line for line in source.splitlines() if "// Row" in line]
        self.assertEqual(64, len(rows))
        self.assertTrue(all(len(re.findall(r"0x[0-9A-F]{2}", row)) == 32 for row in rows))
        self.assertEqual(assets.frame_container(frames, False), initializer_bytes(source))

    def test_cropped_compressed_frames_show_decoded_rows_without_changing_packets(self):
        frames = [("Sideways26", list(range(256)) * 2 + list(range(60))),
                  ("Sideways27", [0xFF, 0] * 286)]
        source = assets.frame_field(frames, True)
        rows = [line for line in source.splitlines() if "// Row" in line]
        self.assertEqual(88, len(rows))
        self.assertTrue(all(len(re.findall(r"0x[0-9A-F]{2}", row)) == 13 for row in rows))
        self.assertIn("13 screen bytes x 22 rows; 572 mask/pixel bytes.", source)
        self.assertIn("Mask packet stream:", source)
        self.assertIn("Pixel packet stream:", source)
        self.assertEqual(assets.frame_container(frames, True), initializer_bytes(source))
        with self.assertRaises(ValueError):
            assets.frame_field([("Short", [0] * 571)], True)


if __name__ == "__main__":
    unittest.main()
