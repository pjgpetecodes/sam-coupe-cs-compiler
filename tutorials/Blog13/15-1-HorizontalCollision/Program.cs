using SamCoupe.Sdk;

// Blog12 composed the room visually. This lesson adds a separate packed
// collision mask: scenery can be visible without being solid. Horizontal
// movement tests the inset body's entire leading edge at every pixel step.
static class Program
{
    static void Main()
    {
        // Compose room 2 and initialize both hidden/visible buffers from its
        // clean image. Screen banks 10/14 differ from background/graphics banks.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.SetBorderColor(7);
        Sam.BeginDoubleBuffer(10, 14);
        BuildRoom(TutorialRooms.Room2Graphics, TutorialRooms.Room2Records);
        CopyBackground();
        Sam.FlipScreen();
        CopyBackground();
        Sam.FlipScreen();

        // byte fits small counters and 0..255 pixel coordinates; spriteY stays
        // fixed in this horizontal-only example. animation selects a sequence,
        // frame its image, delay the waits until advancing, bool flags the work.
        byte frame = 0;
        byte animation = 0;
        byte x = 120;
        byte spriteY = 87;
        byte movePhase = 0;
        byte delay = 5;
        // Each screen still has a sprite from its own last redraw. Store two
        // old X values so restoration does not use the other screen's position.
        byte previousX10 = 0;
        byte previousX14 = 0;
        bool drawingPage14 = true;
        bool redraw = true;
        bool frameChanged = true;

        while (true)
        {
            // Ask for 1/3-pixel steps, but calculate desiredX as signed int:
            // subtracting near zero must not wrap an unsigned byte. Clamp the
            // request first, then let collision accept only legal intermediate X.
            // IsKeyPressed converts the keyboard's active-low bits to a bool.
            bool moved = false;
            byte step = 1;
            if (movePhase != 0)
                step = 3;
            int desiredX = x;
            if (Sam.IsKeyPressed(0xFEFE, 0x02))
            {
                desiredX -= step;
                movePhase = (byte)(1 - movePhase);
            }
            else if (Sam.IsKeyPressed(0xFEFE, 0x04))
            {
                desiredX += step;
                movePhase = (byte)(1 - movePhase);
            }
            if (desiredX < 0)
                desiredX = 0;
            if (desiredX > 230)
                desiredX = 230;
            if (desiredX != x)
            {
                byte oldX = x;
                x = ConstrainX(x, (byte)desiredX, spriteY);
                moved = x != oldX;
            }

            // Idle 0 and walk 2/3 loop; jumps 1/4/5 play to completion. Here
            // jumping still changes pictures, not physical height or velocity.
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

            // Five frame waits separate image changes, independently of
            // collision movement. A new animation resets its frame and timer.
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

            // Select the body in the shared table, then prepare an odd-X copy.
            // Two four-bit pixels share a byte; shifting by one pixel extends
            // the packed width from 13 to 14 bytes. Reuse copies while moving.
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

            // Restore this hidden page's old sprite rectangle before drawing
            // the current one. Flip only once it is complete, record its X,
            // and switch which page's history will be used next.
            if (redraw)
            {
                byte oldX = previousX10;
                if (drawingPage14)
                    oldX = previousX14;
                byte oldWidth = 13;
                if ((oldX & 1) != 0)
                    oldWidth = 14;
                RestoreBackground(oldX, spriteY, oldWidth);

                // Pixel Y selects a 128-byte screen row, pixel X / 2 a byte
                // column. ushort is a screen-relative offset, not a RAM pointer.
                ushort offset = (ushort)(spriteY * 128 + x / 2);
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

            Sam.WaitForFrames(1);
            if (delay != 0)
                delay--;
        }
    }

    static void BuildRoom(byte[] graphics, byte[] records)
    {
        // Build an untouched restoration image on bank 6, beginning with colour zero.
        // Graphics and scene placement records are separate from collision.
        Sam.MapMemoryPage(6);
        for (int row = 0; row < 192; row++)
            Sam.CopyBytes(TutorialRooms.BlankRow, 0, TutorialRooms.Background, (ushort)(row * 128), 128);
        Sam.RestoreMemoryPage();

        // Each seven-byte placement is source/destination little-endian words,
        // byte width, row height, and source stride; the first byte is a count.
        // Width says how much to copy, stride how far to the next source row.
        int blockCount = records[0];
        for (int block = 0; block < blockCount; block++)
        {
            int record = 1 + block * 7;
            ushort source = ReadWord(records, record);
            ushort destination = ReadWord(records, record + 2);
            byte width = records[record + 4];
            byte height = records[record + 5];
            byte stride = records[record + 6];

            // The native kernel stages rows between banks and preserves each
            // background nibble whose source pixel is transparent colour zero.
            Sam.ComposeTransparentImage(
                graphics, 8,
                TutorialRooms.Background, 6,
                source, destination, width, height, stride);
        }
    }

    static ushort ReadWord(byte[] data, int index)
    {
        // Recover a 16-bit offset from low byte then high byte (little endian).
        return (ushort)(data[index] | data[index + 1] << 8);
    }

    static void CopyBackground()
    {
        // Copy 24 KB as 192 resident-staged rows of 128 bytes, restoring the
        // background bank before the SDK accesses the hidden screen bank.
        Sam.CopyBankedBytesToScreen(TutorialRooms.Background, 6, 0, 0, 128, 192);
    }

    static void RestoreBackground(byte x, byte y, byte width)
    {
        // Use the page's OLD coordinates, not the newly accepted collision X.
        // Restore 22 rows, including the extra packed byte when old X was odd.
        // Clean scenery must replace the sprite; black clearing would lose it.
        ushort screenOffset = (ushort)(y * 128 + x / 2);
        Sam.CopyBankedBytesToScreen(
            TutorialRooms.Background, 6,
            screenOffset, screenOffset, width, 22);
    }

    static void LoadSprite(byte index)
    {
        // One header byte precedes the table. Each body is 13 * 22 mask/pixel
        // pairs = 572 bytes, copied into resident memory after its own header.
        // Restore source bank 4 before drawing or allowing a ROM frame wait.
        Sam.MapMemoryPage(4);
        Sam.CopyBytes(
            TutorialData.SpriteFrames,
            (ushort)(1 + index * 572),
            TutorialData.FrameBuffer,
            1,
            572);
        Sam.RestoreMemoryPage();
    }

    static byte ConstrainX(byte currentX, byte desiredX, byte spriteY)
    {
        // acceptedX is the last known safe origin. Test one candidate pixel at
        // a time even for a three-pixel request: checking only the final X can
        // "tunnel" through a thin obstacle without noticing it.
        // The collision body is inset from the picture: X+4..X+21, Y+4..Y+20.
        // Transparent artwork margins therefore do not act as solid body.
        byte acceptedX = currentX;
        bool movingRight = desiredX > currentX;
        bool blocked = false;
        Sam.MapMemoryPage(12);

        while (acceptedX != desiredX && !blocked)
        {
            int candidateX = acceptedX;
            int leadingX = candidateX + 4;
            if (movingRight)
            {
                candidateX++;
                leadingX = candidateX + 21;
            }
            else
            {
                candidateX--;
                leadingX = candidateX + 4;
            }

            // Only the newly entered leading column needs testing, but scan
            // EVERY body row, not just the corners: a small wall protrusion
            // could touch the middle while both corners remain clear. The
            // native column probe checks only the selected pixel's nibble.
            blocked = Sam.AnySolidPixelInPackedScreenColumn(
                TutorialCollision.CollisionMask,
                (byte)leadingX,
                (byte)(spriteY + 4),
                17);

            // Commit only a clear candidate. Once blocked, keep acceptedX
            // at the last safe pixel, restore the bank and return that origin.
            if (!blocked)
                acceptedX = (byte)candidateX;
        }

        Sam.RestoreMemoryPage();
        return acceptedX;
    }
}
