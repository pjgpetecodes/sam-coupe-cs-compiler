// Blog 5, lesson 7-1: any key starts the roll/jump sequence; source: assembly\blog5\7-1.asm.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // Idle alternates frames 0,1; the eight-frame roll uses 0,2,3,4,5,6,7,8.
        // byte is unsigned 0..255. idle and jump count positions within their own sequences,
        // while frame below identifies the actual image in TutorialData's zero-based table.
        // bool stores true/false: jumping remembers the current action between loop iterations.
        byte idle = 0;
        byte jump = 0;
        bool jumping = false;
        // Double buffering displays one screen while another is erased/drawn, hiding partial frames.
        Sam.BeginDoubleBuffer();
        while (true)
        {
            byte frame;
            // while (true) repeats forever. Both branches assign frame before it is used to draw.
            // Check input only in the idle branch: no later key change can break a running action.
            if (jumping)
            {
                // Skip idle-only frame 1. Once started, the roll finishes even after key release.
                // jump 0 selects image 0; jump 1..7 select images 2..8 by adding one.
                // Increment the sequence position, not frame, to determine when playback is complete.
                frame = jump;
                if (frame != 0)
                    frame++;
                jump++;
                if (jump == 8)
                {
                    // Reset the state for NEXT iteration; frame already contains the final image,
                    // so resetting jump here does not skip the last pose that will be drawn below.
                    jump = 0;
                    jumping = false;
                    idle = 0;
                }
            }
            else
            {
                frame = idle;
                idle++;
                if (idle == 2)
                    idle = 0;
                // High byte 00 selects all rows of port FE; any zero bit in 1F means a held key.
                // A port is the hardware input address. The high byte's zero row-select bits
                // choose keyboard rows; 00 selects all of them. Mask 1F tests the five key columns.
                // Active-low means 0 = pressed, 1 = released; the SDK converts that to a bool.
                // This is a level test, not a press edge: a held key can retrigger after completion.
                if (Sam.IsKeyPressed(0x00FE, 0x1F))
                {
                    jumping = true;
                    // The idle image was already selected this iteration; the roll starts next time.
                    jump = 0;
                }
            }

            // Decode one compressed frame to mask,pixel pairs: 16 bytes x 32 rows (32x32 pixels).
            // Erase/draw the hidden rectangle, then flip; destination row stride is 128 bytes.
            // RLE (run-length encoding) saves RAM by storing repeated/literal byte packets.
            // Decoding restores the original masks and colours; each pair applies
            // (screen & mask) | pixel. A descriptor tells the helper which stored image to decode.
            Sam.ClearSprite(0, 16, 32);
            Sam.DrawRleMaskedSprite(Sam.SelectSpriteFrame(TutorialData.SpriteFrames, frame), 0, 16, 32);
            Sam.FlipScreen();
            // The runtime restores ROM before HALT; five waits hold each selected image.
            // ROM is built-in firmware; HALT waits for its interrupt handler. Five 50 Hz
            // interrupts request roughly 0.1 seconds, with drawing/decoding time added on top.
            Sam.WaitForFrames(5);
        }
    }
}
