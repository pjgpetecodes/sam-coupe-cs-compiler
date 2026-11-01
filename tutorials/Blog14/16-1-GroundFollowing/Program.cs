using SamCoupe.Sdk;

// Blog13 blocked horizontal motion at a fixed height. This lesson searches
// nearby heights to follow shallow ground and reports whether the feet have
// support. Losing support does NOT make the player fall yet; Blog15 adds that.
static class Program
{
    // X/Y are the picture's top-left origin in PIXELS, not packed byte columns.
    // Grounded means a solid pixel exists directly below the inset body.
    // byte fits bounded coordinates, while bool records a true/false state.
    static byte PlayerX = 120;
    static byte PlayerY = 99;
    static bool Grounded = true;

    static void Main()
    {
        // Copy the clean bank-6 scene into both screen buffers (starts 10/14).
        // Each is a 24 KB MODE 4 picture; neither should begin with stale pixels.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.SetBorderColor(7);
        Sam.BeginDoubleBuffer(10, 14);
        CopyBackground();
        Sam.FlipScreen();
        CopyBackground();
        Sam.FlipScreen();

        // Sequence, image and timer are separate: animation chooses the motion
        // pictures, frame the image within it, delay the waits until advancing.
        // redraw means repaint; frameChanged additionally means reload pixels.
        byte frame = 0;
        byte animation = 0;
        byte movePhase = 0;
        byte delay = 5;
        // Y can now change as well as X. Each display page needs both of its
        // old coordinates: the next hidden page contains its own older sprite.
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
            // Read active-low keyboard bits through the SDK's bool result.
            // Request alternating one/three-pixel steps, clamped to 0..230.
            // Then ground following may accept a new X AND a nearby new Y.
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

            // White border means supported, colour 3 means unsupported.
            // This is diagnostic feedback, not gravity or a landing animation.
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

            // IDs 0/2/3 are idle/walks, 1/4/5 are jump pictures. These jumps
            // still only play an image sequence; they do not impart velocity.
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

            // Loop idle/walk frames, finish jump frames at idle, and restart
            // the five-wait timer. Movement can redraw between image changes.
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

            // Translate sequence frames into the shared image table, then make
            // an odd-X copy. A pixel shift crosses packed-byte boundaries, so
            // that copy spans 14 bytes instead of the even-X image's 13.
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

            // First restore the hidden page's OLD rectangle from clean scenery.
            // Then draw at the newly accepted position and flip the finished
            // page. Only afterwards update that page's X/Y history.
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

                // MODE 4 packs two four-bit colour pixels per byte: X / 2 is a
                // byte column, Y * 128 a row start. ushort is a 16-bit OFFSET.
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
        // Keep tentative accepted coordinates until testing has finished.
        // Test EACH horizontal pixel, even for a three-pixel request, so thin
        // obstacles cannot be skipped ("tunnelling"). Collision bank 12 must
        // stay mapped while HasSupport and BoxClear inspect its packed bytes.
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

            // Search three pixels upward through three downward, clamped to
            // valid sprite origins 0..170 (192 rows minus 22 sprite rows).
            // Small height differences are ground following, not a jump.
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

            // The first supported height with an empty inset body wins.
            // Search from top to bottom; support alone is insufficient because
            // the body could overlap a wall at that height.
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
            // With no nearby support, allow a clear sideways step at the old
            // Y and mark it unsupported. No vertical falling occurs here.
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
        // The ten-pixel inset body spans X+8..X+17, Y+4..Y+20.
        // Probe Y+21, the row just beneath it. ANY solid foot pixel supports it;
        // this is not a requirement that all ten feet pixels touch ground.
        return SolidRow((byte)(y + 21), (byte)(x + 8));
    }

    static bool BoxClear(byte x, byte y)
    {
        // Scan every row of the inset body, not just corners: a small solid
        // island in the middle must block it too. Caller has mapped bank 12.
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
        // Collision uses two four-bit pixel masks ("nibbles") per byte, like
        // the screen. Parity says high nibble for even X, low for odd X.
        // Check exactly ten pixels, never accidental neighbouring half-bytes.
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
        // A resident row buffer bridges scenery bank 6 and the hidden screen
        // mapping. Read a 128-byte row, restore its bank, then write the screen.
        // All 192 rows are copied only when initializing the full background.
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
        // Erase the old 22-row image with clean scenery, not black. Both source
        // and destination use the old Y*128+X/2 offset; width is bytes, 13 or 14
        // according to OLD X parity. Each next row starts another 128 bytes on.
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
        // Source bank 4 holds a table with a one-byte header and 572-byte frame
        // bodies: 13 columns * 22 rows * (mask byte + pixel byte). Copy only the
        // body to resident memory, leaving its header, then restore the bank.
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
