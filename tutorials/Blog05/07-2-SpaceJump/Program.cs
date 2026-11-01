// Blog 5, lesson 7-2: restrict roll/jump input to Space; source: assembly\blog5\7-2.asm.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // Idle uses frames 0,1; the non-looping roll uses 0,2,3,4,5,6,7,8.
        // byte holds 0..255. idle/jump count positions in separate sequences, while frame is
        // the actual zero-based image index. bool jumping remembers whether the action is running.
        byte idle = 0;
        byte jump = 0;
        bool jumping = false;
        // Keep a visible and a hidden RAM screen; prepare complete images only on the hidden one.
        Sam.BeginDoubleBuffer();
        while (true)
        {
            byte frame;
            // The endless while loop selects one pose each time. Both branches assign frame.
            // Once jumping is true, input is not tested again until the action has finished.
            if (jumping)
            {
                // Frame 1 belongs to idle, not the roll. Key release does not interrupt this sequence.
                // Sequence positions 0..7 map to table images 0,2..8; adding one skips idle image 1.
                frame = jump;
                if (frame != 0)
                    frame++;
                jump++;
                if (jump == 8)
                {
                    // frame still holds image 8 for this draw; reset only the NEXT iteration's state.
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
                // Port FE row 7F contains Space at bit 0; the hardware bit is active-low.
                // Port means hardware input address. 7F has one zero row-select bit, selecting
                // the B/N/M/Symbol/Space row. Mask 01 tests only bit 0, ignoring the other keys.
                // Active-low is 0 = pressed, 1 = released; IsKeyPressed turns this into true/false.
                // No press-edge latch is used: holding Space can start another roll after it ends.
                if (Sam.IsKeyPressed(0x7FFE, 0x01))
                {
                    jumping = true;
                    // This iteration already chose an idle image; action position zero starts next time.
                    jump = 0;
                }
            }

            // Expand the selected compressed frame to mask,pixel pairs, then draw only off-screen.
            // Sixteen packed bytes x 32 rows is 32x32 pixels, with a 128-byte screen row stride.
            // RLE means run-length encoding: repeated/literal packets are expanded into original
            // mask,pixel pairs. AND preserves background bits under mask 1s, then OR adds colour.
            // The selected descriptor identifies the data/image; clearing removes the previous pose.
            Sam.ClearSprite(0, 16, 32);
            Sam.DrawRleMaskedSprite(Sam.SelectSpriteFrame(TutorialData.SpriteFrames, frame), 0, 16, 32);
            Sam.FlipScreen();
            // ROM must be mapped back for the interrupt-driven waits; the runtime handles that.
            // HALT waits for built-in ROM firmware to handle the interrupt. Five waits at 50 Hz
            // request roughly a tenth of a second; decoding and drawing add their own elapsed time.
            Sam.WaitForFrames(5);
        }
    }
}
