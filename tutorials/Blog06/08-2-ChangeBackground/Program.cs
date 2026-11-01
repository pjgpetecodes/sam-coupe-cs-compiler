// Blog 6, lesson 8-2: recolour the background through the CLUT; source: assembly\blog6\8-2.asm.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // byte holds unsigned 0..255 values. Keep the animation ID, local pose counter, and
        // persistent background selection separate: choosing a colour must not restart a sequence.
        byte frame = 0;
        byte animation = 0;
        byte background = 0;
        // Load the editor palette but deliberately start with black at index zero.
        // CLUT means Colour Look-Up Table: each four-bit pixel index (a nibble) selects one of
        // sixteen hardware colours. A zero screen byte contains two index-zero pixels.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.SetPaletteEntry(0, 0);
        Sam.BeginDoubleBuffer();
        while (true)
        {
            // Row F7 of port FE holds keys 1..5 at bits 0..4, active-low.
            // Else-if order gives the lowest numbered held key priority; no key keeps the selection.
            // The high byte chooses the keyboard row using a zero select bit, the low byte FE
            // addresses its input port. Masks 01,02,04,08,10 test one key-column bit each.
            // Active-low means a pressed bit is 0; the SDK converts it to bool true for the if.
            if (Sam.IsKeyPressed(0xF7FE, 0x01))
                background = 0;
            else if (Sam.IsKeyPressed(0xF7FE, 0x02))
                background = 1;
            else if (Sam.IsKeyPressed(0xF7FE, 0x04))
                background = 2;
            else if (Sam.IsKeyPressed(0xF7FE, 0x08))
                background = 3;
            else if (Sam.IsKeyPressed(0xF7FE, 0x10))
                background = 4;

            // These are SAM CLUT values, not packed pixel bytes. Recolour index zero only,
            // so all background pixels change without filling either screen again.
            byte backgroundColour = 0;
            if (background == 1)
                backgroundColour = 0x11;
            else if (background == 2)
                backgroundColour = 0x22;
            else if (background == 3)
                backgroundColour = 0x44;
            else if (background == 4)
                backgroundColour = 0x7F;
            Sam.SetPaletteEntry(0, backgroundColour);

            // IDs 0/1/2/3 mean idle/roll/left/right. Finish a roll before accepting another choice.
            // Space is 7FFE bit 0, Z/X are FEFE bits 1/2; held Space can retrigger, without an edge latch.
            byte nextAnimation = 0;
            // Default to idle, then choose only the first matching branch: Space outranks Z and X.
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
                // Restart only on a change of sequence; otherwise holding a walk continues its poses.
                animation = nextAnimation;
                frame = 0;
            }

            // Preserve source frame order: idle 0,1; roll 0,2..8; left 9..16; right 17..23.
            // Translate the local frame counter to a global image index with 9/17 offsets.
            // Roll skips idle-only image 1. (byte) narrows C#'s int arithmetic after computing
            // these bounded indices; it is not a way to make an out-of-range index valid.
            byte sprite = frame;
            if (animation == 1 && frame != 0)
                sprite = (byte)(frame + 1);
            else if (animation == 2)
                sprite = (byte)(9 + frame);
            else if (animation == 3)
                sprite = (byte)(17 + frame);

            // Decode mask,pixel pairs, then clear/draw 16 screen bytes x 32 rows on the hidden page.
            // RLE (run-length encoding) saves RAM using repeated/literal packets. Drawing combines
            // (screen & mask) | pixel; & keeps masked old bits, | inserts the new colour.
            // Clear first because transparency would preserve the previous pose. Two pixels share
            // each screen byte, and rows stride by 128; flip only after the hidden image is complete.
            Sam.ClearSprite(0, 16, 32);
            Sam.DrawRleMaskedSprite(Sam.SelectSpriteFrame(TutorialData.SpriteFrames, sprite), 0, 16, 32);
            Sam.FlipScreen();
            // Runtime ROM-safe waits also preserve the selected palette across interrupt CLUT resets.
            // ROM is built-in firmware; HALT waits for its interrupt handler. Five 50 Hz waits
            // request roughly 0.1 seconds per pose, plus rendering overhead.
            Sam.WaitForFrames(5);
            // Idle/walk loop; roll completes once and returns to idle.
            // Each limit is the number of poses (2/8/8/7), not the last valid index.
            // The while loop never breaks; resetting animation/frame selects idle next time.
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
