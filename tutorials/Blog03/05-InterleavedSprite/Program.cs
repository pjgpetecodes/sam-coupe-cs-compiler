// Blog 3, lesson 5: draw already-interleaved data; source: assembly\blog3\5.asm.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // SelectSpriteFrame returns a typed data/index descriptor, not a copy of the pixels.
        // A descriptor is a small value telling the helper which array and which image to use.
        // Index 0 selects the first image because C# frame indices start at zero.
        // The supplied frame already holds mask,pixel pairs, so no build step is needed.
        // Sixteen pairs per row draw 32 pixels; 32 source bytes map to a 128-byte screen stride.
        // Drawing still combines each pair as (screen & mask) | pixel.
        // AND keeps screen bits under 1 mask bits; OR adds the sprite colour bits.
        // Each pixel is a four-bit nibble: mask F/pixel 0 leaves that background pixel alone.
        // Offset 0 is top left; width 16 counts packed screen bytes, not pixels or source bytes.
        Sam.DrawMaskedSprite(Sam.SelectSpriteFrame(TutorialData.SpriteFrames, 0), 0, 16, 32);
        // Leaving the frame visible until a key is pressed completes this static drawing lesson.
        Sam.WaitForKey();
    }
}
