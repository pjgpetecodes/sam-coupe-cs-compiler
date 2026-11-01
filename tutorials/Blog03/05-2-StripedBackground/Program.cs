// Blog 3, lesson 5-2: supplied interleaved data over stripes; source: assembly\blog3\5-2.asm.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // Repeat palette index 2 or 4 across alternating 128-byte screen rows.
        // Two pixels share each byte, so 22 and 44 colour both halves alike.
        // Background must be drawn first to avoid overwriting the masked result.
        Sam.FillStripedBackground(0x22, 0x44);
        // The typed frame descriptor selects the supplied mask,pixel pairs directly.
        // A descriptor identifies an array and a zero-based image index; 0 means the first frame.
        // Interleaved means mask0,pixel0,mask1,pixel1,..., unlike the two separate arrays in lesson 4.
        // Each of 32 rows has 16 pairs (32 pixels); (screen & mask) | pixel retains the stripes.
        // AND preserves old bits under mask 1s, then OR adds colour. A transparent four-bit
        // pixel ("nibble") has mask F and colour 0; it keeps the background rather than erasing it.
        // Source rows occupy 32 bytes, while screen rows stride by 128. Offset 0 is top left.
        Sam.DrawMaskedSprite(Sam.SelectSpriteFrame(TutorialData.SpriteFrames, 0), 0, 16, 32);
        // Pause before returning so the transparent edges over the stripes remain visible.
        Sam.WaitForKey();
    }
}
