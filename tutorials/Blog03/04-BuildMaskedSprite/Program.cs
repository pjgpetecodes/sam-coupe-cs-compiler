// Blog 3, lesson 4: build transparent sprite data; source: assembly\blog3\4.asm.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // Combine the separate 512-byte planes into resident mask,pixel pairs.
        // A "plane" is one array covering the whole sprite: one for colour, one for masking.
        // Interleaving alternates them as mask0,pixel0,mask1,pixel1,... in a RAM buffer
        // that remains accessible ("resident"). Drawing can then consume each pair together.
        Sam.BuildInterleavedSprite(TutorialData.SpritePixels, TutorialData.SpriteMasks);
        // Each destination byte becomes (screen & mask) | pixel: F mask nibbles preserve it.
        // & is bitwise AND: a mask bit 1 keeps a screen bit, while 0 clears it.
        // | is bitwise OR: it inserts the sprite bits after the old bits have been cleared.
        // For a transparent pixel, its four-bit mask is F and its colour nibble is 0;
        // for an opaque pixel the mask is 0. This differs from treating every colour 0 as transparent.
        // Width 16 means 32 pixels; 32 rows of pairs use 32 source bytes per row,
        // while destination rows remain 128 bytes apart.
        // The arguments are screen byte offset 0, width in screen bytes, and height in rows.
        // Two pixels fit in each screen byte, but the source additionally needs one mask byte.
        Sam.DrawBuiltMaskedSprite(0, 16, 32);
        // Pause for inspection before returning; the prepared buffer is reused by the draw helper.
        Sam.WaitForKey();
    }
}
