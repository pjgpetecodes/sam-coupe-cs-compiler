// Blog 8, lesson 10-1: move independently of animation; source: assembly\blog8\10-1.asm.
// Banked cropped data stays unchanged; screen page numbers here belong to the C# runtime.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // byte is unsigned 0..255; frame is a local pose counter and animation selects its sequence.
        // Position is independent: the same pose can be drawn at many X values.
        byte frame = 0;
        byte animation = 0;
        byte background = 0;
        // X is a byte displacement from the existing row-5, byte-1 anchor, not a frame number.
        byte x = 0;
        byte delay = 5;
        // delay counts interrupt waits left before advancing a pose; displayingSecondPage is 0/1.
        byte displayingSecondPage = 0;
        // Each hidden page still contains its own last image, not necessarily the last displayed X.
        byte previousX4 = 0;
        byte previousX6 = 0;
        bool redraw = true;
        // bool holds true/false. Initially request a draw even though no movement has happened.

        // Retain the editor palette but start index zero at black.
        // CLUT means Colour Look-Up Table: a four-bit pixel index chooses one of sixteen colours.
        // Double buffering displays one RAM screen while the hidden second screen is prepared.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.SetPaletteEntry(0, 0);
        Sam.BeginDoubleBuffer();
        while (true)
        {
            // Active-low Z/X: row FE, bits 1/2. Z wins even if it cannot move at the left edge.
            // Port FE is the hardware input; a zero select bit in its high byte chooses a row.
            // Masks 02/04 select columns 1/2. Active-low means a held key reads zero, which
            // IsKeyPressed converts to bool true. This checks held state on every loop iteration.
            // A byte step is two MODE 4 pixels; displacement is bounded at 114:
            // 128 screen bytes - byte-1 anchor - 13 sprite bytes = 114.
            // Check before --/++ to avoid unsigned wrap or a rectangle crossing the right edge.
            // Translation follows held keys even during a non-interruptible roll/jump animation.
            bool moved = false;
            if (Sam.IsKeyPressed(0xFEFE, 0x02))
            {
                if (x != 0)
                {
                    x--;
                    moved = true;
                }
            }
            else if (Sam.IsKeyPressed(0xFEFE, 0x04) && x < 114)
            {
                x++;
                moved = true;
            }

            // Keys 1..5 are row F7 bits 0..4; releasing them retains the chosen background.
            // Each mask tests one column; the else-if chain gives the lowest numbered key priority.
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

            // Recolour CLUT entry zero instead of refilling the screen pixels.
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

            // IDs 0/1/2/3/4/5: idle, roll, left/right walk, left/right jump.
            // Space is 7FFE bit 0; Space+Z/X selects a jump, otherwise Space selects a roll.
            // Actions complete before new input is accepted; a held Space can retrigger (no edge latch).
            // An edge latch would remember that Space was released on the preceding iteration.
            // Instead keep the current ID for 1/4/5 until the sequence-length test ends the action.
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
                // Rewind the pose and its delay, and request a fresh image immediately.
                animation = nextAnimation;
                frame = 0;
                delay = 5;
                redraw = true;
            }

            // Animation advances after five single-interrupt waits, independently of movement redraws.
            // Idle/walk loop; roll/jump return to idle after their last image.
            // && joins an ID to its length, || accepts any matching completed sequence.
            // Testing lengths before drawing avoids selecting a pose beyond the sequence's end.
            // The main while loop never breaks; an action ends by resetting its ID to idle.
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
            }

            if (moved)
                redraw = true;

            // If neither position nor pose changed, leave the current screen displayed.
            // A redraw caused by movement still uses the current animation counter.
            if (redraw)
            {
                // Original frame ranges: idle 0,1; roll 0,2..8; walks 9..16/17..23; jumps 24..30/31..37.
                // Translate local frame to global table index by adding its sequence start.
                // Roll skips idle-only image 1. (byte) converts promoted int addition back to byte.
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

                // The banked array descriptor is usable only while its page-8 lower window is mapped.
                // Copy one 13-pair x 22-row frame (572 bytes); offset 1 skips/preserves count headers.
                // Resident code, stack and staging buffer stay visible throughout this copy.
                // A page is 16 KB of physical RAM; paging swaps which RAM the CPU sees at the same
                // lower addresses. LMPR (Lower Memory Page Register, port 250) maps pages 8/9 here.
                // The array descriptor identifies data but does not automatically keep it mapped.
                // Staging means copying into always-visible RAM: drawing will replace the lower
                // source window with screen RAM. Without this copy the source would disappear.
                // 1 + sprite*572 skips the frame-count byte, then locates the zero-based image.
                // ushort (0..65535) holds that byte offset; its cast converts the int calculation.
                Sam.MapMemoryPage(8);
                Sam.CopyBytes(
                    TutorialData.SpriteFrames,
                    (ushort)(1 + sprite * 572),
                    TutorialData.FrameBuffer,
                    1,
                    572);
                // Restore LMPR before the drawing helpers map screen RAM or a wait needs the ROM.
                Sam.RestoreMemoryPage();

                // Screen offset = row 5's 128-byte stride + byte-1 anchor + displacement.
                // Offset is screen-relative, not a CPU address. ushort avoids truncating 641+x to a byte.
                ushort offset = (ushort)(641 + x);
                // If page 4 is displayed, page 6 is hidden: erase page 6's own old position.
                // After a flip the roles reverse, so a single global previous X would leave trails.
                // For example, successive draws at X=0/1/2 use pages 6/4/6: page 6 must erase
                // X=0, not X=1. Update that page's history only AFTER clearing its old rectangle.
                if (displayingSecondPage == 0)
                {
                    Sam.ClearSprite((ushort)(641 + previousX6), 13, 22);
                    previousX6 = x;
                }
                else
                {
                    Sam.ClearSprite((ushort)(641 + previousX4), 13, 22);
                    previousX4 = x;
                }

                // Select the resident frame descriptor and draw 13 screen bytes x 22 rows.
                // Each source row has 26 mask/pixel bytes; each destination row strides by 128.
                // AND preserves background bits under mask 1s; OR inserts colour bits.
                // Clear before masking so transparent pixels do not preserve a previous pose.
                Sam.DrawMaskedSprite(
                    Sam.SelectSpriteFrame(TutorialData.FrameBuffer, 0),
                    offset,
                    13,
                    22);
                // Change the displayed page only after erase/draw, then track the new page roles.
                Sam.FlipScreen();
                // 1 - value toggles 0/1, matching the runtime's new displayed/hidden roles.
                displayingSecondPage = (byte)(1 - displayingSecondPage);
                redraw = false;
            }

            // Poll movement every iteration; the runtime restores ROM and retains CLUT through HALT.
            // ROM is built-in firmware; its interrupt handler wakes HALT. One nominal 50 Hz wait
            // requests 20 ms, plus loop/rendering costs. Decrement only a nonzero delay to prevent
            // unsigned 0 becoming 255; the next iteration handles an expired delay.
            Sam.WaitForFrames(1);
            if (delay != 0)
                delay--;
        }
    }
}
