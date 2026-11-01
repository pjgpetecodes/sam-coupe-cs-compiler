// Blog 3, lesson 4-2: demonstrate masking over stripes; source: assembly\blog3\4-2.asm.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // Alternating whole 128-byte rows use palette indices 2 and 4 for both pixels.
        // Each four-bit half of 22/44 is the same index, so every pixel in a row shares a colour.
        // Draw the background first: drawing it afterwards would overwrite the sprite.
        Sam.FillStripedBackground(0x22, 0x44);
        // Interleave the separate planes once, then apply (screen & mask) | pixel.
        // Transparent mask nibbles retain the stripes rather than painting a black box.
        // A nibble is four bits. AND (&) keeps background bits wherever the mask is 1,
        // then OR (|) inserts sprite bits. Transparent nibbles use mask F and pixel 0.
        // The two source "planes" are whole arrays of masks and colours; interleaving puts
        // each mask beside its matching colour in a resident (always accessible) RAM buffer.
        Sam.BuildInterleavedSprite(TutorialData.SpritePixels, TutorialData.SpriteMasks);
        // 16 screen bytes x 32 rows is 32x32 pixels; source rows contain 16 mask,pixel pairs.
        // Source rows therefore occupy 32 bytes; the screen still needs a 128-byte row stride.
        // The first argument, 0, is the top-left byte offset. It is not a frame number.
        Sam.DrawBuiltMaskedSprite(0, 16, 32);
        // Wait so that the preserved stripes can be inspected before Main returns.
        Sam.WaitForKey();
    }
}
