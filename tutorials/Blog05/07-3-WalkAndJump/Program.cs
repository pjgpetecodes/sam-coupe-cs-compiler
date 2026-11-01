// Blog 5, lesson 7-3: choose idle, roll and walk sequences; source: assembly\blog5\7-3.asm.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // Animation IDs: 0 idle, 1 roll (the earlier "jump"), 2 walk left, 3 walk right.
        // byte is unsigned 0..255. animation selects a sequence; frame counts a pose WITHIN it.
        // They must be separate because the same local pose number can mean different images.
        byte frame = 0;
        byte animation = 0;
        // Double buffering keeps one screen visible while we prepare another, avoiding partial poses.
        Sam.BeginDoubleBuffer();
        while (true)
        {
            // Keep a roll until completion; otherwise priority is Space, Z, X, then idle.
            // Keys are active-low: Space is bit 0 of row 7F; Z/X are bits 1/2 of row FE.
            // Input is held-state, not press-edge detection, so Space can retrigger a finished roll.
            // Port FE is the keyboard input; the high byte selects a row via a zero bit.
            // Masks 01/02/04 select column bits 0/1/2. IsKeyPressed converts 0 = held into true.
            // An edge would mean "released last time, pressed now"; this code remembers no such history.
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
                // A new sequence starts at its first image rather than inheriting the old counter.
                animation = nextAnimation;
                frame = 0;
            }

            // Table ranges: idle 0,1; roll 0,2..8; left 9..16; right 17..23.
            // sprite is the global image index: add the sequence's starting offset to frame.
            // Roll skips idle-only image 1. C# byte arithmetic is promoted to int, so (byte)
            // explicitly converts these known-small results back to an unsigned image index.
            byte sprite = frame;
            if (animation == 1 && frame != 0)
                sprite = (byte)(frame + 1);
            else if (animation == 2)
                sprite = (byte)(9 + frame);
            else if (animation == 3)
                sprite = (byte)(17 + frame);

            // Walking changes the image, not the screen position yet.
            // Decode 16 mask,pixel pairs per row, clear/draw 32 rows on the hidden page, then flip.
            // RLE (run-length encoding) stores repeated/literal packets to save RAM; decoding restores
            // the original pairs. Each screen byte holds two pixels; row stride is 128 bytes.
            // Transparency preserves old screen bits, so erase the old rectangle before drawing.
            Sam.ClearSprite(0, 16, 32);
            Sam.DrawRleMaskedSprite(Sam.SelectSpriteFrame(TutorialData.SpriteFrames, sprite), 0, 16, 32);
            Sam.FlipScreen();
            // HALT waits for the ROM interrupt handler; the runtime restores ROM before waiting.
            // Five 50 Hz waits request about 0.1 seconds per pose, plus rendering overhead.
            Sam.WaitForFrames(5);
            // Idle/walk loop; the eight-image roll returns to idle instead of looping.
            // The limits 2/8/8/7 are sequence LENGTHS: reset before the next draw would go past them.
            // No break exits the main loop: ending the roll changes state to idle for the next iteration.
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
