using SamCoupe.Sdk;

// This variant uses the same Blog14 ground-following rules at a different
// starting Y. Walk off a ledge to see support disappear in the border colour.
// Like 16-1, it reports an unsupported player but does not apply gravity yet.
static class Program
{
    // byte coordinates are bounded 0..255 pixel origins; Grounded is a bool,
    // meaning some solid terrain touches the inset body's next row below.
    static byte PlayerX = 120;
    static byte PlayerY = 106;
    static bool Grounded = true;

    static void Main()
    {
        // Initialize BOTH screen buffers from the clean background. Banks
        // 10/14 are display starts; bank 6 is the scene kept for restoration.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.SetBorderColor(7);
        Sam.BeginDoubleBuffer(10, 14);
        CopyBackground();
        Sam.FlipScreen();
        CopyBackground();
        Sam.FlipScreen();

        // animation selects a sequence, frame a picture, delay a frame-wait
        // countdown. Repainting and loading a different image are separate jobs.
        byte frame = 0;
        byte animation = 0;
        byte movePhase = 0;
        byte delay = 5;
        // Keep old X AND Y for each page: the hidden buffer may contain a
        // sprite older than the one visible now. drawingPage14 selects history.
        byte previousX10 = PlayerX;
        byte previousY10 = PlayerY;
        byte previousX14 = PlayerX;
        byte previousY14 = PlayerY;
        byte displayedGrounded = 1;
        bool drawingPage14 = true;
        bool redraw = true;
        bool frameChanged = true;

        while (true)
        {
            // Active-low keyboard reads become true for a press in the SDK.
            // Alternate 1/3-pixel requests and clamp before byte arithmetic can
            // wrap. Ground following can adjust height as it accepts each X.
            bool moved = false;
            byte desiredX = PlayerX;
            if (Sam.IsKeyPressed(0xFEFE, 0x02))
            {
                if (movePhase == 0)
                {
                    if (PlayerX != 0)
                        desiredX--;
                }
                else if (PlayerX >= 3)
                    desiredX = (byte)(PlayerX - 3);
                else
                    desiredX = 0;
                movePhase = (byte)(1 - movePhase);
            }
            else if (Sam.IsKeyPressed(0xFEFE, 0x04))
            {
                if (movePhase == 0)
                {
                    if (PlayerX < 230)
                        desiredX++;
                }
                else if (PlayerX <= 227)
                    desiredX = (byte)(PlayerX + 3);
                else
                    desiredX = 230;
                movePhase = (byte)(1 - movePhase);
            }
            if (desiredX != PlayerX)
            {
                byte oldX = PlayerX;
                byte oldY = PlayerY;
                ConstrainGroundedX((byte)desiredX);
                moved = PlayerX != oldX || PlayerY != oldY;
            }

            // Border 7 shows support; border 3 shows its loss at a ledge.
            // This visual diagnostic changes state only, not vertical speed.
            if (Grounded != (displayedGrounded != 0))
            {
                if (Grounded)
                {
                    Sam.SetBorderColor(7);
                    displayedGrounded = 1;
                }
                else
                {
                    Sam.SetBorderColor(3);
                    displayedGrounded = 0;
                }
            }

            // Idle/walk IDs are 0/2/3; jump-picture IDs are 1/4/5. Completing
            // these jump sequences still does not move the player's Y.
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

            // Advance on the five-wait cadence: loop idle/walks, return completed
            // jumps to idle. Moving can trigger redraw before the next image.
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

            // Convert animation-local frames to banked table indices. Build a
            // one-pixel-shifted copy only for an image change; reuse it for odd
            // X, where 26 pixels cover 14 packed bytes instead of 13.
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

            // Restore THIS hidden page's old footprint, draw the current one,
            // flip once complete, then record its new X/Y and switch pages.
            // Drawing first would let restoration erase part of the new sprite.
            if (redraw)
            {
                byte oldX = previousX10;
                byte oldY = previousY10;
                if (drawingPage14)
                {
                    oldX = previousX14;
                    oldY = previousY14;
                }
                byte oldWidth = 13;
                if ((oldX & 1) != 0)
                    oldWidth = 14;
                RestoreBackground(oldX, oldY, oldWidth);

                // A row is 128 bytes; each byte is two MODE 4 pixel colours.
                // Coordinates are pixels, this ushort is a screen byte offset.
                ushort offset = (ushort)(PlayerY * 128 + PlayerX / 2);
                if ((PlayerX & 1) == 0)
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
                {
                    previousX14 = PlayerX;
                    previousY14 = PlayerY;
                }
                else
                {
                    previousX10 = PlayerX;
                    previousY10 = PlayerY;
                }
                drawingPage14 = !drawingPage14;
                redraw = false;
            }

            Sam.WaitForFrames(1);
            if (delay != 0)
                delay--;
        }
    }

    static void ConstrainGroundedX(byte desiredX)
    {
        // acceptedX/Y hold only clear positions. Move one candidate pixel at
        // a time so a request cannot tunnel through a narrow solid obstacle.
        // Bank 12 is mapped for all support/body tests, restored before return.
        byte acceptedX = PlayerX;
        byte acceptedY = PlayerY;
        bool grounded = Grounded;
        bool movingRight = desiredX > PlayerX;
        bool blocked = false;
        Sam.MapMemoryPage(12);

        while (acceptedX != desiredX && !blocked)
        {
            byte candidateX = acceptedX;
            if (movingRight)
                candidateX++;
            else
                candidateX--;

            // Search up to three rows above/below the current origin; clamp to
            // 0..170 so the 22-row sprite stays within a 192-row screen.
            byte firstY = acceptedY;
            if (firstY >= 3)
                firstY = (byte)(firstY - 3);
            else
                firstY = 0;
            byte lastY = acceptedY;
            if (lastY <= 167)
                lastY = (byte)(lastY + 3);
            else
                lastY = 170;

            // Find the highest nearby supported position whose body is clear.
            // The terrain can touch the feet without occupying body pixels.
            bool found = false;
            byte candidateY = acceptedY;
            byte searchY = firstY;
            while (searchY <= lastY && !found)
            {
                if (HasSupport(candidateX, searchY) &&
                    BoxClear(candidateX, searchY))
                {
                    candidateY = (byte)searchY;
                    found = true;
                }
                searchY++;
            }

            if (found)
            {
                acceptedX = (byte)candidateX;
                acceptedY = candidateY;
                grounded = true;
            }
            // A clear step with no support is a ledge exit. Keep its old Y and
            // mark Grounded false; the next lesson supplies downward movement.
            else if (BoxClear(candidateX, acceptedY))
            {
                acceptedX = (byte)candidateX;
                grounded = false;
            }
            else
            {
                blocked = true;
            }
        }

        Sam.RestoreMemoryPage();
        PlayerX = acceptedX;
        PlayerY = acceptedY;
        Grounded = grounded;
    }

    static bool HasSupport(byte x, byte y)
    {
        // Body bounds are X+8..X+17 and Y+4..Y+20; test the row at Y+21.
        // Any one of the ten foot pixels suffices, so partial ledge contact
        // supports the body. The caller already selected collision bank 12.
        return SolidRow((byte)(y + 21), (byte)(x + 8));
    }

    static bool BoxClear(byte x, byte y)
    {
        // Examine all ten body pixels on EVERY row, not just the corners,
        // excluding transparent margins of the larger 26-by-22-pixel picture.
        byte row = (byte)(y + 4);
        byte lastRow = (byte)(y + 20);
        while (row <= lastRow)
        {
            if (SolidRow(row, (byte)(x + 8)))
                return false;
            row++;
        }
        return true;
    }

    static bool SolidRow(byte y, byte firstX)
    {
        // Each mask byte has two four-bit nibbles. Pixel parity selects the
        // first high/even or low/odd nibble, and the SDK scans ten exact pixels.
        // Multiplication produces a byte offset, not an absolute RAM pointer.
        ushort offset = (ushort)(y * 128 + firstX / 2);
        byte parity = 0;
        if ((firstX & 1) != 0)
            parity = 1;
        return Sam.AnySolidPixelInPackedRow(
            TutorialCollision.CollisionMask,
            offset,
            parity,
            10);
    }

    static void CopyBackground()
    {
        // Stage a whole 128-byte row from bank 6 in resident GraphicsRow,
        // restore its mapping, then access the hidden screen. This avoids
        // reading from a bank that screen paging has hidden. Copy 192 rows.
        for (int row = 0; row < 192; row++)
        {
            Sam.MapMemoryPage(6);
            Sam.CopyBytes(
                TutorialRooms.Background,
                (ushort)(row * 128),
                TutorialRooms.GraphicsRow,
                0,
                128);
            Sam.RestoreMemoryPage();
            Sam.CopyBytesToScreen(
                TutorialRooms.GraphicsRow,
                0,
                (ushort)(row * 128),
                128);
        }
    }

    static void RestoreBackground(byte x, byte y, byte width)
    {
        // Restore only the OLD sprite's 22 rows using clean scenery. width is
        // 13 packed bytes, or 14 for an odd origin's shifted image. Advance
        // source/destination by 128 bytes per row, not by the cropped width.
        ushort screenOffset = (ushort)(y * 128 + x / 2);
        for (int row = 0; row < 22; row++)
        {
            ushort offset = (ushort)(screenOffset + row * 128);
            Sam.MapMemoryPage(6);
            Sam.CopyBytes(
                TutorialRooms.Background,
                offset,
                TutorialRooms.GraphicsRow,
                0,
                width);
            Sam.RestoreMemoryPage();
            Sam.CopyBytesToScreen(
                TutorialRooms.GraphicsRow,
                0,
                offset,
                width);
        }
    }

    static void LoadSprite(byte index)
    {
        // Skip the frame table's one-byte header. Each 572-byte body contains
        // 13 * 22 interleaved mask/pixel pairs; preserve the destination header.
        // Resident staging keeps the selected image accessible after bank 4
        // is restored, ready for even/odd drawing and ROM-serviced waits.
        Sam.MapMemoryPage(4);
        Sam.CopyBytes(
            TutorialData.SpriteFrames,
            (ushort)(1 + index * 572),
            TutorialData.FrameBuffer,
            1,
            572);
        Sam.RestoreMemoryPage();
    }
}
