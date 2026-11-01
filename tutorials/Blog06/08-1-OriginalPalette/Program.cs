// Blog 6, lesson 8-1: restore the editor's colours; source: assembly\blog6\8-1.asm.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // byte (0..255) is enough for sequence IDs and pose counters. frame is local to animation,
        // not a screen address; a global sprite-table index is calculated later.
        byte frame = 0;
        byte animation = 0;
        // Pixel nibbles are indices, not RGB values. CLUT port 248 selects entries via its high byte.
        // A nibble is four bits (0..15). CLUT means Colour Look-Up Table: sixteen hardware
        // entries translate those indices to displayed colours. ROM means built-in firmware.
        // Keep the blank background black even though the supplied palette's entry zero is cyan.
        // Load the whole palette, then override just entry 0. Clearing pixels writes index 0,
        // so their visible colour depends on this entry, not on the value of the clear byte alone.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.SetPaletteEntry(0, 0);
        Sam.BeginDoubleBuffer();
        while (true)
        {
            // IDs: idle 0, roll 1, left walk 2, right walk 3. A roll cannot be interrupted.
            // Space (7FFE bit 0) outranks Z/X (FEFE bits 1/2); pressed hardware bits are zero.
            // These are level tests, so a held Space can retrigger after the roll completes.
            // The high byte of port FE chooses a keyboard row using a zero select bit; masks
            // 01/02/04 test columns 0/1/2. Active-low means 0 = pressed; the SDK returns a bool.
            // The first branch keeps an ongoing roll; otherwise else-if gives Space, then Z priority.
            byte nextAnimation = 0;
            if (animation == 1)
                nextAnimation = 1;
            else if (Sam.IsKeyPressed(0x7FFE, 0x01))
                nextAnimation = 1;
            else if (Sam.IsKeyPressed(0xFEFE, 0x02))
                nextAnimation = 2;
            else if (Sam.IsKeyPressed(0xFEFE, 0x04))
                nextAnimation = 3;
            if (nextAnimation != animation)
            {
                // Change the sequence and rewind its local pose counter together.
                animation = nextAnimation;
                frame = 0;
            }

            // Original table order: idle 0,1; roll 0,2..8; left 9..16; right 17..23.
            // Add the sequence's start to its local counter; roll instead skips idle-only image 1.
            // C# adds byte operands as int; (byte) explicitly converts a known valid index back.
            byte sprite = frame;
            if (animation == 1 && frame != 0)
                sprite = (byte)(frame + 1);
            else if (animation == 2)
                sprite = (byte)(9 + frame);
            else if (animation == 3)
                sprite = (byte)(17 + frame);

            // Decode a frame, then erase/draw the hidden 16-byte x 32-row rectangle and flip.
            // Source rows contain mask,pixel pairs; destination rows remain 128 bytes apart.
            // RLE (run-length encoding) expands repeat/literal packets. A descriptor identifies
            // the selected image; drawing ANDs with its mask then ORs its pixel colour bits.
            // Transparent masks preserve old contents, so erase before drawing, and flip only
            // when complete. Double buffering hides both clearing and partial drawing from view.
            Sam.ClearSprite(0, 16, 32);
            Sam.DrawRleMaskedSprite(Sam.SelectSpriteFrame(TutorialData.SpriteFrames, sprite), 0, 16, 32);
            Sam.FlipScreen();
            // ROM interrupts can reset the CLUT; the runtime retains/reapplies our palette after HALT.
            // An interrupt runs firmware periodically; HALT waits for it. Five 50 Hz waits request
            // about 0.1 seconds, not including rendering time. Palette preservation avoids flashing.
            Sam.WaitForFrames(5);
            // Only the roll is non-looping; its final image returns the state to idle.
            // Increment after displaying a pose. Sequence lengths are idle 2, roll 8, left 8, right 7.
            // Reset before next selection; the endless while loop continues, rather than breaking out.
            frame++;
            if (animation == 0 && frame == 2)
                frame = 0;
            else if (animation == 1 && frame == 8)
            {
                animation = 0;
                frame = 0;
            }
            else if (animation == 2 && frame == 8)
                frame = 0;
            else if (animation == 3 && frame == 7)
                frame = 0;
        }
    }
}
