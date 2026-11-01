// Blog 9, lesson 11-1: handle odd MODE 4 pixel alignment; source: assembly\blog9\11-1.asm.
// Reuse Blog 8's banked cropped frames and per-screen erasure state, with a shifted resident form.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // byte is unsigned 0..255. animation identifies a sequence; frame counts a pose within it.
        // Position, pose, and timing must be independent so moving need not change the image.
        byte frame = 0;
        byte animation = 0;
        byte background = 0;
        // X is now a pixel displacement; its low bit chooses the packed-nibble alignment.
        // MODE 4 stores two pixels per byte: high four bits for the left, low four for the right.
        // A four-bit half is a nibble. Parity means whether X is even or odd.
        byte x = 0;
        byte movePhase = 0;
        byte delay = 5;
        byte displayingSecondPage = 0;
        // Keep previous pixel positions per page; their parity also determines the old erase width.
        byte previousX4 = 0;
        byte previousX6 = 0;
        byte sprite = 0;
        bool redraw = true;
        // Movement can reuse both prepared forms; only an animation change needs another bank copy.
        bool frameChanged = true;
        // bool stores true/false. redraw requests screen work; frameChanged requests bank/shift work.
        // Both begin true so the first iteration prepares and displays a pose without any input.

        // Keep the editor palette, starting with black at index zero.
        // CLUT (Colour Look-Up Table) turns four-bit pixel indices into sixteen displayed colours.
        // Double buffering means a visible and a hidden RAM screen, avoiding incomplete visible frames.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.SetPaletteEntry(0, 0);
        Sam.BeginDoubleBuffer();
        while (true)
        {
            // Alternate requested 1- and 3-pixel steps: both switch alignment, averaging Blog 8's two.
            // Clamp at 0/228 rather than wrapping byte arithmetic; this is not collision or gravity.
            // The byte-1 anchor reserves two pixels: 256 - 2 - 26 sprite pixels = 228 displacement.
            // Active-low Z/X (FEFE bits 1/2) translate independently of animation; Z wins.
            // Port FE reads keyboard hardware. The high byte selects a row using a zero bit;
            // masks 02/04 test columns 1/2. A zero key bit is pressed; the SDK returns bool true.
            // movePhase 0/1 chooses step 1/3; 1-value toggles it after a held direction is handled.
            // Bounds checks happen BEFORE arithmetic. (byte) converts C#'s int result back only
            // after it is known to fit; at an edge the final partial step clamps rather than wrapping.
            bool moved = false;
            byte step = 1;
            if (movePhase != 0)
                step = 3;
            if (Sam.IsKeyPressed(0xFEFE, 0x02))
            {
                if (x >= step)
                {
                    x = (byte)(x - step);
                    moved = true;
                }
                else if (x != 0)
                {
                    x = 0;
                    moved = true;
                }
                movePhase = (byte)(1 - movePhase);
            }
            else if (Sam.IsKeyPressed(0xFEFE, 0x04))
            {
                if (x <= 228 - step)
                {
                    x = (byte)(x + step);
                    moved = true;
                }
                else if (x != 228)
                {
                    x = 228;
                    moved = true;
                }
                movePhase = (byte)(1 - movePhase);
            }

            // Keys 1..5 select a persistent CLUT-zero colour via active-low row F7 bits 0..4.
            // Each mask tests one column; else-if gives the lowest numbered held key priority.
            if (Sam.IsKeyPressed(0xF7FE, 0x01))
                background = 0;
            else if (Sam.IsKeyPressed(0xF7FE, 0x02))
                background = 1;
            else if (Sam.IsKeyPressed(0xF7FE, 0x04))
                background = 2;
            else if (Sam.IsKeyPressed(0xF7FE, 0x08))
                background = 3;
            else if (Sam.IsKeyPressed(0xF7FE, 0x10))
                background = 4;

            // Change the palette lookup, not either screen's stored background indices.
            byte backgroundColour = 0;
            if (background == 1)
                backgroundColour = 0x11;
            else if (background == 2)
                backgroundColour = 0x22;
            else if (background == 3)
                backgroundColour = 0x44;
            else if (background == 4)
                backgroundColour = 0x7F;
            Sam.SetPaletteEntry(0, backgroundColour);

            // IDs 0..5: idle, roll, left/right walk, left/right jump.
            // Space (7FFE bit 0) rolls; Space+Z/X jumps. Actions finish even after keys are released.
            // This is held-state selection, not an edge latch; held Space may retrigger afterwards.
            // An edge latch would compare the key with its previous state. Here the && comparisons
            // block new action choices for IDs 1/4/5; translation still follows direction independently.
            byte nextAnimation = animation;
            if (animation != 1 && animation != 4 && animation != 5)
            {
                if (Sam.IsKeyPressed(0x7FFE, 0x01))
                {
                    if (Sam.IsKeyPressed(0xFEFE, 0x02))
                        nextAnimation = 4;
                    else if (Sam.IsKeyPressed(0xFEFE, 0x04))
                        nextAnimation = 5;
                    else
                        nextAnimation = 1;
                }
                else if (Sam.IsKeyPressed(0xFEFE, 0x02))
                    nextAnimation = 2;
                else if (Sam.IsKeyPressed(0xFEFE, 0x04))
                    nextAnimation = 3;
                else
                    nextAnimation = 0;
            }
            if (nextAnimation != animation)
            {
                // Reset the local pose/timer and both work flags when changing a sequence.
                animation = nextAnimation;
                frame = 0;
                delay = 5;
                redraw = true;
                frameChanged = true;
            }

            // Advance imagery every five waits; moving between waits still requests an immediate redraw.
            // Idle/walk loop, while roll/jump reset to idle after their final image.
            // A limit is a sequence's LENGTH, not its last index. && joins each ID with its limit;
            // || accepts any completed sequence. Reset now so the next draw never selects an invalid pose.
            // There is no break out of the main loop: action completion switches its state to idle.
            if (delay == 0)
            {
                frame++;
                if (animation == 0 && frame == 2 ||
                    animation == 2 && frame == 8 ||
                    animation == 3 && frame == 7)
                {
                    frame = 0;
                }
                else if (animation == 1 && frame == 8 ||
                         animation == 4 && frame == 7 ||
                         animation == 5 && frame == 7)
                {
                    animation = 0;
                    frame = 0;
                }
                delay = 5;
                redraw = true;
                frameChanged = true;
            }

            if (moved)
                redraw = true;

            // Only a new pose needs both resident representations rebuilt. Moving can reuse them,
            // even when it switches alignment, because even and odd forms are already prepared.
            if (frameChanged)
            {
                // Source table: idle 0,1; roll 0,2..8; walks 9..16/17..23; jumps 24..30/31..37.
                // Add the sequence's starting offset to frame; roll instead skips idle-only image 1.
                // (byte) converts C#'s promoted int arithmetic back to a bounded zero-based index.
                sprite = frame;
                if (animation == 1 && frame != 0)
                    sprite = (byte)(frame + 1);
                else if (animation == 2)
                    sprite = (byte)(9 + frame);
                else if (animation == 3)
                    sprite = (byte)(17 + frame);
                else if (animation == 4)
                    sprite = (byte)(24 + frame);
                else if (animation == 5)
                    sprite = (byte)(31 + frame);

                // Map the page-8 bank descriptor, then stage 13 pairs x 22 rows = 572 bytes.
                // Offset 1 skips the source count and leaves the resident one-frame count unchanged.
                // The code, stack and destination remain resident while lower memory is paged.
                // A page is 16 KB of physical RAM. LMPR (Lower Memory Page Register, port 250)
                // changes which RAM appears at the lower CPU addresses; pages 8/9 hold this array.
                // Its descriptor identifies banked data, not an automatically retained mapping.
                // Staging into always-visible RAM is necessary because drawing will replace that
                // lower window with screen RAM. 1+sprite*572 skips the count and locates the image.
                // ushort is unsigned 16-bit (0..65535), large enough for this calculated byte offset.
                Sam.MapMemoryPage(8);
                Sam.CopyBytes(
                    TutorialData.SpriteFrames,
                    (ushort)(1 + sprite * 572),
                    TutorialData.FrameBuffer,
                    1,
                    572);
                Sam.RestoreMemoryPage();
                // Restore the bank before transforming resident buffers or mapping screen RAM.
                // Shift mask AND pixel nibbles right by one pixel, carrying across byte boundaries.
                // Odd rows need 14 pairs (616 bytes total), not 13: neither edge may be truncated.
                // Padding mask nibbles are F and pixel nibbles 0, preserving the outside neighbours.
                // For source nibbles AB,CD the shifted row begins 0A,BC,D0 in the colour plane.
                // The matching mask inserts F at the first/last outside nibble, so AND preserves
                // neighbouring background pixels and OR adds nothing there. Shift the masks too:
                // moving colours alone would clear/preserve the wrong pixels.
                // Odd "rows" here means odd-aligned rows, not alternating vertical row numbers.
                Sam.ShiftMaskedSpriteOnePixel(
                    TutorialData.FrameBuffer,
                    TutorialData.OddFrameBuffer);
                frameChanged = false;
            }

            if (redraw)
            {
                // High nibble is the left pixel: divide X by two for the byte displacement.
                // Keep the existing row-5, byte-1 anchor; parity is handled by the frame choice below.
                // Integer division discards the remainder: X=3 gives byte displacement 1.
                // (x & 1) isolates the lowest bit; zero means even, nonzero means odd.
                // ushort preserves the full offset 641+X/2 instead of truncating it to eight bits.
                ushort offset = (ushort)(641 + x / 2);
                // Erase the hidden page's own old X, using its OLD parity, not the new position's.
                // An odd-aligned old frame covered 14 screen bytes, so clearing only 13 leaves trails.
                // If page 4 is displayed, page 6 is hidden, and vice versa. Its old X may be from
                // two redraws ago, not the latest displayed pose. Use & 1 on that old position,
                // clear it with the correct width, THEN store the new X for that page.
                if (displayingSecondPage == 0)
                {
                    byte oldWidth = 13;
                    if ((previousX6 & 1) != 0)
                        oldWidth = 14;
                    Sam.ClearSprite((ushort)(641 + previousX6 / 2), oldWidth, 22);
                    previousX6 = x;
                }
                else
                {
                    byte oldWidth = 13;
                    if ((previousX4 & 1) != 0)
                        oldWidth = 14;
                    Sam.ClearSprite((ushort)(641 + previousX4 / 2), oldWidth, 22);
                    previousX4 = x;
                }

                // Even X uses 13 original mask,pixel pairs per row; odd X uses 14 shifted pairs.
                // Both typed descriptors point at resident buffers, with 128-byte destination strides.
                // A descriptor identifies an array/image; index 0 is the single prepared frame.
                // Drawing (screen & mask) | pixel keeps transparent edge nibbles and inserts colours.
                if ((x & 1) == 0)
                {
                    Sam.DrawMaskedSprite(
                        Sam.SelectSpriteFrame(TutorialData.FrameBuffer, 0),
                        offset,
                        13,
                        22);
                }
                else
                {
                    Sam.DrawMaskedSprite(
                        Sam.SelectSpriteFrame(TutorialData.OddFrameBuffer, 0),
                        offset,
                        14,
                        22);
                }
                // Display the completed hidden frame, then swap the per-page erasure roles.
                Sam.FlipScreen();
                // Toggle 0/1 to follow which page is now displayed; clear the completed work request.
                displayingSecondPage = (byte)(1 - displayingSecondPage);
                redraw = false;
            }

            // ROM-safe interrupt waits pace the loop and retain the selected palette.
            // ROM is built-in firmware; HALT needs its interrupt handler mapped back in.
            // One 50 Hz wait requests 20 ms, plus copying/shifting/drawing overhead when needed.
            // Guard delay before -- to prevent unsigned zero wrapping to 255.
            Sam.WaitForFrames(1);
            if (delay != 0)
                delay--;
        }
    }

}
