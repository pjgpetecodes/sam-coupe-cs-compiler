using SamCoupe.Sdk;

// Blog10 introduced a fixed beach. Now move Blog09's pixel-positioned sprite
// over it without trails: erase its OLD rectangle using untouched scenery,
// then draw its NEW rectangle. Each display buffer needs its own old position.
static class Program
{
    static void Main()
    {
        // The scene occupies 24 KB, spanning more than one physical 16 KB page.
        // 10 and 14 name the two screen starts, not pixel coordinates. Copy
        // the background to both buffers so either can be drawn into next.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.SetBorderColor(7);
        Sam.BeginDoubleBuffer(10, 14);
        CopyBackground();
        Sam.FlipScreen();
        CopyBackground();
        Sam.FlipScreen();

        // byte is an unsigned 0..255 value, enough for X and frame counters.
        // animation chooses a sequence; frame chooses an image within it.
        // delay counts frame waits, while redraw/frameChanged are bool flags:
        // moving requires repainting but not necessarily reloading image data.
        byte frame = 0;
        byte animation = 0;
        byte x = 0;
        byte movePhase = 0;
        byte delay = 5;
        // A hidden page still contains the sprite from its last turn, which
        // may be older than the currently visible sprite. Never erase it using
        // only the newest global X. drawingPage14 tracks the next drawing page.
        byte previousX10 = 0;
        byte previousX14 = 0;
        bool drawingPage14 = true;
        bool redraw = true;
        bool frameChanged = true;

        while (true)
        {
            // Keyboard row/mask pairs select active-low matrix bits; the SDK
            // reports true when pressed. Left wins if both directions are held.
            // Alternating 1 and 3 pixels averages 2 per update, with clamps
            // preventing unsigned X from wrapping at either screen edge.
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
                if (x <= 230 - step)
                {
                    x = (byte)(x + step);
                    moved = true;
                }
                else if (x != 230)
                {
                    x = 230;
                    moved = true;
                }
                movePhase = (byte)(1 - movePhase);
            }

            // 0 = idle, 1 = straight jump, 2/3 = walk left/right, 4/5 = sideways
            // jump. These jumps are animation sequences only: Y stays 115.
            // A jump sequence is allowed to finish before input selects another.
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
                animation = nextAnimation;
                frame = 0;
                delay = 5;
                redraw = true;
                frameChanged = true;
            }

            // Walking/idle sequences loop; jumps return to idle at their end.
            // Reload the five-wait delay only when advancing an animation frame.
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

            // Convert sequence-relative frame numbers to the shared frame
            // table's indices. Shift once per image change, not once per move.
            // A nibble is four bits: MODE 4 stores two pixel colours per byte.
            // The shifted image handles an odd X and needs one extra byte.
            if (frameChanged)
            {
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

                LoadSprite(sprite);
                Sam.ShiftMaskedSpriteOnePixel(
                    TutorialData.FrameBuffer,
                    TutorialData.OddFrameBuffer);
                frameChanged = false;
            }

            // Update order matters: restore this hidden page's old footprint,
            // draw the current sprite, flip only the finished screen, then
            // record its new X and select the other page for the next redraw.
            if (redraw)
            {
                byte oldX = previousX10;
                if (drawingPage14)
                    oldX = previousX14;
                byte oldWidth = 13;
                if ((oldX & 1) != 0)
                    oldWidth = 14;
                RestoreBackground(oldX, oldWidth);

                // Coordinates are pixels but the screen offset is bytes.
                // x / 2 selects a packed byte; x & 1 selects its pixel parity.
                // ushort is the SDK's unsigned 16-bit offset, not a pointer.
                ushort offset = (ushort)(115 * 128 + x / 2);
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

                Sam.FlipScreen();
                if (drawingPage14)
                    previousX14 = x;
                else
                    previousX10 = x;
                drawingPage14 = !drawingPage14;
                redraw = false;
            }

            // The wait happens with normal memory mapping restored by the
            // drawing helpers, allowing the ROM interrupt to service HALT.
            Sam.WaitForFrames(1);
            if (delay != 0)
                delay--;
        }
    }

    static void CopyBackground()
    {
        // Page 6 holds the clean beach. Stage a row in resident memory before
        // accessing the hidden screen, since banked data and screen RAM cannot
        // safely be treated as simultaneously mapped sources in one window.
        // 192 rows * 128 bytes is the full 24 KB MODE 4 image.
        for (int row = 0; row < 192; row++)
        {
            Sam.MapMemoryPage(6);
            Sam.CopyBytes(
                TutorialData.BeachBackground,
                (ushort)(row * 128),
                TutorialData.BackgroundRow,
                0,
                128);
            Sam.RestoreMemoryPage();
            Sam.CopyBytesToScreen(
                TutorialData.BackgroundRow,
                0,
                (ushort)(row * 128),
                128);
        }
    }

    static void RestoreBackground(byte x, byte width)
    {
        // Copy only the 22-row old sprite rectangle, not the whole beach.
        // width is 13 bytes for even X or 14 for the shifted odd-X footprint.
        // Using the clean banked image brings back scenery under transparent
        // sprite pixels too; clearing to black would destroy the beach.
        ushort screenOffset = (ushort)(115 * 128 + x / 2);
        for (int row = 0; row < 22; row++)
        {
            ushort offset = (ushort)(screenOffset + row * 128);
            Sam.MapMemoryPage(6);
            Sam.CopyBytes(
                TutorialData.BeachBackground,
                offset,
                TutorialData.BackgroundRow,
                0,
                width);
            Sam.RestoreMemoryPage();
            Sam.CopyBytesToScreen(
                TutorialData.BackgroundRow,
                0,
                offset,
                width);
        }
    }

    static void LoadSprite(byte index)
    {
        // The table's first byte is a header. Each frame body contains 13
        // packed columns * 22 rows * 2 bytes (mask then pixels) = 572 bytes.
        // Leave the destination header intact by writing at offset 1. A mask
        // preserves background bits; its paired pixels supply visible colours.
        // Stage in resident FrameBuffer before restoring the source bank.
        Sam.MapMemoryPage(8);
        Sam.CopyBytes(
            TutorialData.SpriteFrames,
            (ushort)(1 + index * 572),
            TutorialData.FrameBuffer,
            1,
            572);
        Sam.RestoreMemoryPage();
    }
}
