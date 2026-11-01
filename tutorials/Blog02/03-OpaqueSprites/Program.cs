// Blog 2, lesson 3: opaque sprite rows; source: assembly\blog2\3.asm.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // Copy 16 packed bytes per row for 32 rows: 512 bytes describe 32x32 pixels.
        // TutorialData contains the supplied byte array: consecutive rows of palette indices.
        // Two four-bit halves ("nibbles") in each byte describe the left and right pixels.
        // The helper advances screen rows by 128 bytes, not by the sprite's 16-byte width.
        // After copying 16 bytes it skips the other 112 bytes of the screen row; otherwise
        // the next sprite row would be drawn beside the first rather than beneath it.
        // With no mask, every copied nibble replaces the background, including index zero.
        // "Opaque" means it replaces the entire rectangle. Offset 0 places it at the top left.
        Sam.DrawOpaqueSprite(TutorialData.OpaqueSprite, 0);
        // Keep the result visible; when a key finishes the wait, Main can return to BASIC.
        Sam.WaitForKey();
    }
}
