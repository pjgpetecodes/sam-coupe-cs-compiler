// Blog 4, lesson 6-4: finish each frame off-screen; source: assembly\blog4\6-4.asm.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // frame is a zero-based image index stored in byte (unsigned 0..255), not a screen address.
        byte frame = 0;
        // Startup clears the first 24 KB MODE 4 screen; this clears/selects the hidden second screen.
        // Double buffering means displaying one RAM screen while assembling the next image in another.
        // Each screen is 192 rows x 128 bytes = 24 KB; a page is a 16 KB physical RAM block.
        Sam.BeginDoubleBuffer();
        while (true)
        {
            // Erase/draw only the hidden 16-byte x 32-row rectangle, with 128-byte screen strides.
            // Transparency preserves background bits using (screen & mask) | pixel, so clear
            // the hidden page's old pose first. Sixteen packed bytes describe 32 pixels per row.
            // while (true) keeps repeating; the visible page is never partly erased by this work.
            Sam.ClearSprite(0, 16, 32);
            Sam.DrawMaskedSprite(Sam.SelectSpriteFrame(TutorialData.SpriteFrames, frame), 0, 16, 32);
            // Flip VMPR only after drawing is complete; the former visible page becomes hidden.
            // VMPR is the Video Memory Page Register (port 252): it chooses which RAM screen is shown.
            Sam.FlipScreen();
            // The runtime restores ROM for HALT; repeat the original nine-frame table.
            // HALT waits for the ROM's interrupt handler. Five 50 Hz waits request roughly 0.1 seconds
            // of display time; clearing/drawing and compiler overhead add to that elapsed time.
            Sam.WaitForFrames(5);
            frame++;
            // Increment the zero-based index, wrapping 9 to 0 so the next loop repeats images 0..8.
            if (frame == 9)
                frame = 0;
        }
    }
}
