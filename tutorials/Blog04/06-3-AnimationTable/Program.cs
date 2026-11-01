// Blog 4, lesson 6-3: follow a nine-frame animation table; source: assembly\blog4\6-3.asm.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // A frame table lists the images in playback order. Its first entry is index 0.
        // byte holds an unsigned 0..255 index; while (true) keeps replaying the sequence.
        byte frame = 0;
        while (true)
        {
            // Draw 16 mask,pixel pairs x 32 rows after clearing the previous rectangle.
            // A typed frame descriptor selects the original table entry without copying it.
            // Mask,pixel interleaving lets the helper AND the screen with a mask, then OR the colour.
            // Since transparent pixels preserve whatever is already there, clear the old pose first.
            // Sixteen packed screen bytes represent 32 pixels; destination rows stride by 128.
            Sam.ClearSprite(0, 16, 32);
            Sam.DrawMaskedSprite(Sam.SelectSpriteFrame(TutorialData.SpriteFrames, frame), 0, 16, 32);
            // Five interrupt waits per image, then advance in the original 0..8 table order.
            // The runtime restores ROM before HALT waits for its handler; five 50 Hz waits
            // request about 0.1 seconds, not a guarantee of the whole loop's execution time.
            Sam.WaitForFrames(5);
            frame++;
            // ++ advances to the next index. Nine is past the last entry, so wrap to zero
            // before the next draw: no invalid frame 9 is ever selected.
            if (frame == 9)
                frame = 0;
        }
    }
}
