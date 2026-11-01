// Blog 4, lesson 6-2: frames from the external include; source: assembly\blog4\6-2.asm.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // Keep the same zero-based byte counter (0..255) as 6-1; only indices 0 and 1 are used.
        // while (true) keeps playing indefinitely rather than returning after one image.
        byte frame = 0;
        while (true)
        {
            // TutorialData retains the first two system-palette frames from the source include.
            // Each is 16 mask,pixel pairs x 32 rows; screen rows have a 128-byte stride.
            // Interleaving places each mask before its colour byte. AND preserves background
            // bits selected by the mask and OR inserts the new colour bits.
            // Transparent parts would also preserve the previous pose, so erase that pose first.
            // The selected descriptor identifies the first/second image without copying its bytes.
            Sam.ClearSprite(0, 16, 32);
            Sam.DrawMaskedSprite(Sam.SelectSpriteFrame(TutorialData.SpriteFrames, frame), 0, 16, 32);
            // ROM-backed waits pace the animation; visible-page erase/draw can still flicker.
            // HALT waits for a hardware interrupt handled by ROM, which must be mapped back in.
            // Five 50 Hz waits request roughly 0.1 seconds; drawing costs are additional.
            Sam.WaitForFrames(5);
            // Moving the data out of the original assembly body does not change the 0,1 sequence.
            // The if/else chooses the other image for the next loop, after displaying this one.
            if (frame == 0)
                frame = 1;
            else
                frame = 0;
        }
    }
}
