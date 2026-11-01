// Blog 4, lesson 6-1: alternate two embedded frames; source: assembly\blog4\6-1.asm.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // byte is an unsigned 0..255 counter; here it is a zero-based image index, only 0 or 1.
        // while (true) repeats indefinitely; these animation demos do not exit after drawing once.
        byte frame = 0;
        while (true)
        {
            // Erase the old 16-byte x 32-row rectangle before applying the new mask,pixel pairs.
            // This still draws on the visible screen; double buffering is introduced in 6-4.
            // A new frame's transparent mask preserves existing pixels, including old sprite pixels.
            // Clearing to background index 0 first prevents the old pose from showing through.
            // The descriptor selects an image without copying it. Each pair applies
            // (screen & mask) | pixel; packed bytes hold two pixels and screen rows stride by 128.
            Sam.ClearSprite(0, 16, 32);
            Sam.DrawMaskedSprite(Sam.SelectSpriteFrame(TutorialData.SpriteFrames, frame), 0, 16, 32);
            // Request five ROM interrupts; the runtime restores ROM before HALT.
            // An interrupt is a hardware signal that runs the ROM's handler; HALT waits for it.
            // At nominal 50 Hz, five waits request about a tenth of a second, plus drawing overhead.
            Sam.WaitForFrames(5);
            // Only source frames 0 and 1 alternate here.
            // This if/else toggles the next image after the current one has been shown and held.
            if (frame == 0)
                frame = 1;
            else
                frame = 0;
        }
    }
}
