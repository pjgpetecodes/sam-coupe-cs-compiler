// Blog 7, lesson 9-1: stage banked frames before drawing; source: assembly\blog7\9-1.asm.
// This port banks the same cropped frames at page 8; its runtime owns the screen-page choices.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // byte is unsigned 0..255. frame counts a pose within animation; background remembers
        // a separate colour choice. None of these counters is itself a sprite memory address.
        byte frame = 0;
        byte animation = 0;
        byte background = 0;
        // Keep the editor's palette, with black at index zero until a background key is held.
        // CLUT means Colour Look-Up Table: sixteen entries turn four-bit pixel indices into colours.
        // Double buffering shows one RAM screen while another is prepared, hiding incomplete images.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.SetPaletteEntry(0, 0);
        Sam.BeginDoubleBuffer();
        while (true)
        {
            // Active-low row F7, bits 0..4 selects keys 1..5; selection persists after release.
            // Port FE is the keyboard input address. Its high byte selects rows with zero bits;
            // masks 01/02/04/08/10 select individual columns. Active-low is 0 = held, 1 = released,
            // so the SDK converts a cleared bit into bool true. Else-if gives key 1 first priority.
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

            // These values replace CLUT entry zero, recolouring both screens without repainting them.
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

            // IDs: idle 0, roll 1, walks 2/3, sideways jumps 4/5.
            // Space (7FFE bit 0) rolls; Space+Z/X (FEFE bits 1/2) jumps; Z has priority.
            // Active-low held-state tests are not press edges: actions finish, then may retrigger.
            // A press edge would compare old and new key states; no such history is stored here.
            // Keep nextAnimation unchanged for IDs 1/4/5. The && comparisons require all three
            // exclusions before accepting a new action, protecting a running roll/jump from interruption.
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
                // Rewind the local pose counter only when the sequence actually changes.
                animation = nextAnimation;
                frame = 0;
            }

            // Preserve the cropped table: idle 0,1; roll 0,2..8; walks 9..16/17..23;
            // jumps 24..30/31..37 (source image 25 was omitted from the cropped include).
            // Add each sequence's starting index to its local counter; roll skips idle image 1.
            // C# promotes byte arithmetic to int; (byte) converts the bounded global index back.
            byte sprite = frame;
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

            // BankedData(8) makes the array's descriptor refer to the lower mapped window.
            // Banked data lives in physical RAM outside the CPU's current 64 KB address view.
            // A page is 16 KB; paging swaps which physical pages appear at the same CPU addresses.
            // A descriptor identifies the array's location; it does NOT keep that bank mapped for us.
            // Code, stack and FrameBuffer remain resident while LMPR maps sprite pages 8/9.
            // LMPR is the Lower Memory Page Register (port 250), selecting the lower 32 KB window.
            // Resident means always visible. Staging one frame here is essential: drawing maps
            // screen RAM into that same lower window, so the original banked source would disappear.
            // Skip the payload's count byte: each frame is 13 pairs x 22 rows = 572 bytes.
            // Destination offset 1 leaves FrameBuffer's one-frame count intact.
            // Source offset 1 + sprite*572 finds that image after the header, without including the
            // compiler's separate array-length prefix. ushort is unsigned 16-bit (0..65535);
            // the cast converts C#'s int offset calculation to the CopyBytes offset parameter.
            Sam.MapMemoryPage(8);
            Sam.CopyBytes(
                TutorialData.SpriteFrames,
                (ushort)(1 + sprite * 572),
                TutorialData.FrameBuffer,
                1,
                572);
            // Restore the complete mapping before screen helpers or ROM-backed HALT waits.
            Sam.RestoreMemoryPage();

            // The frame descriptor now refers to the resident copy, not the banked source.
            // Offset 641 = 5*128+1; draw 13 packed bytes x 22 rows with 128-byte screen strides.
            // Mask,pixel pairs combine as (screen & mask) | pixel on the hidden page.
            // AND keeps old screen bits under mask 1s; OR inserts sprite colour bits. Each byte
            // contains two four-bit pixels ("nibbles"). Clear the old pose first because transparent
            // pixels otherwise preserve it. FrameBuffer now contains exactly one image, so select index 0.
            Sam.ClearSprite(641, 13, 22);
            Sam.DrawMaskedSprite(
                Sam.SelectSpriteFrame(TutorialData.FrameBuffer, 0),
                641,
                13,
                22);
            // Display only the completed frame. Runtime waits retain the CLUT across ROM interrupts.
            // ROM is built-in firmware; HALT waits for its interrupt handler, requiring ROM mapped back.
            // Five 50 Hz waits request about 0.1 seconds per pose; copying/drawing add elapsed time.
            Sam.FlipScreen();
            Sam.WaitForFrames(5);

            // Idle/walk loop; rolls and sideways jumps play once, then reset to idle.
            // Increment after drawing the last pose, then compare against the sequence LENGTH.
            // && joins each ID/length pair and || accepts any completed action. The while loop
            // never breaks: ending an action changes state to idle for the following iteration.
            frame++;
            if (animation == 0 && frame == 2)
                frame = 0;
            else if (animation == 2 && frame == 8)
                frame = 0;
            else if (animation == 3 && frame == 7)
                frame = 0;
            else if (animation == 1 && frame == 8 ||
                     animation == 4 && frame == 7 ||
                     animation == 5 && frame == 7)
            {
                animation = 0;
                frame = 0;
            }
        }
    }
}
