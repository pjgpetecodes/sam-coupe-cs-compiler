using SamCoupe.Sdk;

// Blog15 introduced downward acceleration. Now a fresh Space press gives an
// upward velocity: the jump trajectory comes from physics, not the picture
// sequence. The same body tests stop upward movement against solid scenery.
static class Program
{
    // X/Y are byte pixel origins. sbyte is signed (-128..127), unlike byte:
    // negative velocity moves UP (smaller Y), positive moves DOWN. Grounded
    // means feet support; SpaceWasDown remembers last update's keyboard state.
    static byte PlayerX = 120;
    static byte PlayerY = 106;
    static bool Grounded = true;
    static sbyte VerticalVelocity = 0;
    static bool SpaceWasDown = false;

    static void Main()
    {
        // Initialize both display buffers from the clean scene before drawing
        // the player. Physical banks 10/14 are screen starts, 6 is background.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.SetBorderColor(7);
        Sam.BeginDoubleBuffer(10, 14);
        CopyBackground();
        Sam.FlipScreen();
        CopyBackground();
        Sam.FlipScreen();

        // animation and frame select pictures, not vertical motion. delay
        // counts frame waits; bool redraw/frameChanged separate screen repaint
        // from loading/reshifting a different image.
        byte frame = 0;
        byte animation = 0;
        byte movePhase = 0;
        byte delay = 5;
        // Store each page's own old X/Y. Its hidden sprite can be older than
        // the currently visible one, especially during a physical jump.
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
            // An "edge" is a change from released to pressed. Require that
            // edge AND Grounded so holding Space cannot bounce on every landing.
            // The SDK interprets active-low keyboard bits as true for presses.
            // Set velocity before movement/gravity; gravity then reduces -10
            // to -9 in this very update. startedJump preserves its first picture.
            bool moved = false;
            bool startedJump = false;
            bool spaceDown = Sam.IsKeyPressed(0x7FFE, 0x01);
            if (spaceDown && !SpaceWasDown && Grounded)
            {
                VerticalVelocity = -10;
                Grounded = false;
                if (Sam.IsKeyPressed(0xFEFE, 0x02))
                    animation = 4;
                else
                    animation = 5;
                frame = 0;
                delay = 5;
                redraw = true;
                frameChanged = true;
                startedJump = true;
            }
            SpaceWasDown = spaceDown;

            // Horizontal requests alternate 1/3 pixels and clamp to 0..230.
            // Collision accepts individual pixels. In flight it tests the same
            // Y instead of snapping to nearby ground; gravity then updates Y.
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
            if (ApplyGravity())
                moved = true;

            // Show supported (7) versus airborne (3) after physics, not based
            // on which walking or jumping picture happens to be displayed.
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

            // Ground input chooses idle/walk pictures unless a jump just began.
            // Airborne jumps keep 4/5 while their sequence lasts. Image changes
            // never set velocity: finishing a picture sequence cannot land us.
            byte nextAnimation = animation;
            if (!startedJump && Grounded)
            {
                if (Sam.IsKeyPressed(0xFEFE, 0x02))
                    nextAnimation = 2;
                else if (Sam.IsKeyPressed(0xFEFE, 0x04))
                    nextAnimation = 3;
                else
                    nextAnimation = 0;
            }
            else if (!Grounded && animation != 4 && animation != 5)
                nextAnimation = 0;

            if (nextAnimation != animation)
            {
                animation = nextAnimation;
                frame = 0;
                delay = 5;
                redraw = true;
                frameChanged = true;
            }

            // Advance picture sequences on a separate five-wait timer. Idle/
            // walks loop; a completed jump sequence returns to idle pictures.
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

            // Translate animation-local indices to table frames. Stage only
            // changed images, then shift one pixel for odd X. A shifted image
            // spans an extra packed byte (14 instead of 13) at either margin.
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

            // Restore the hidden page's old 22-row footprint, draw its new
            // sprite, flip the finished screen, then record that page's X/Y.
            // Restoring after drawing would erase the new picture.
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

                // MODE 4 holds two four-bit pixel colours in one byte. Convert
                // pixel origin to byte offset: 128 per row plus X/2. ushort is
                // an unsigned 16-bit screen offset, not an absolute RAM pointer.
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
        // Keep last-safe coordinates. One-pixel tests prevent "tunnelling":
        // a multi-pixel request must not jump past an intervening thin wall.
        // HasSupport/BoxClear assume collision bank 12 stays mapped here.
        byte acceptedX = PlayerX;
        byte acceptedY = PlayerY;
        bool grounded = Grounded;
        bool movingRight = desiredX > PlayerX;
        bool blocked = false;
        Sam.MapMemoryPage(12);

        // Airborne motion keeps the current height for horizontal tests.
        // Ground following is inappropriate while a jump controls vertical Y.
        if (!grounded)
        {
            while (acceptedX != desiredX && !blocked)
            {
                byte candidateX = acceptedX;
                if (movingRight)
                    candidateX++;
                else
                    candidateX--;

                if (BoxClear(candidateX, acceptedY))
                    acceptedX = candidateX;
                else
                    blocked = true;
            }
        }
        else
        {
            while (acceptedX != desiredX && !blocked)
            {
                byte candidateX = acceptedX;
                if (movingRight)
                    candidateX++;
                else
                    candidateX--;

                // Grounded motion searches from three rows above through three
                // below for a body-clear, supported slope position. Clamp to
                // 0..170 so the whole 22-row picture remains screen-safe.
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

                bool found = false;
                byte candidateY = acceptedY;
                byte searchY = firstY;
                while (searchY <= lastY && !found)
                {
                    if (HasSupport(candidateX, searchY) &&
                        BoxClear(candidateX, searchY))
                    {
                        candidateY = searchY;
                        found = true;
                    }
                    searchY++;
                }

                if (found)
                {
                    acceptedX = candidateX;
                    acceptedY = candidateY;
                    grounded = true;
                }
                else if (BoxClear(candidateX, acceptedY))
                {
                    acceptedX = candidateX;
                    grounded = false;
                }
                else
                {
                    blocked = true;
                }
            }
        }

        Sam.RestoreMemoryPage();
        PlayerX = acceptedX;
        PlayerY = acceptedY;
        Grounded = grounded;
    }

    static bool ApplyGravity()
    {
        // Increase signed velocity by one to model downward acceleration.
        // Upward movement slows to zero; falling accelerates only up to +4.
        // Return actual Y movement so Main can redraw even with unchanged art.
        bool moved = false;
        Sam.MapMemoryPage(12);
        if (VerticalVelocity < 0)
        {
            VerticalVelocity++;
            if (VerticalVelocity < 0)
            {
                // Negate the still-negative velocity to get a positive step
                // count. Test each upward pixel before accepting it, not just
                // the final Y: a thin ceiling could lie between the endpoints.
                byte remaining = (byte)(-VerticalVelocity);
                while (remaining != 0 && PlayerY > 48)
                {
                    byte candidateY = (byte)(PlayerY - 1);
                    if (BoxClear(PlayerX, candidateY))
                    {
                        PlayerY = candidateY;
                        moved = true;
                        remaining--;
                    }
                    else
                    {
                        // Keep the last clear Y and cancel upward speed. This
                        // is a ceiling/body hit, not a grounded landing.
                        VerticalVelocity = 0;
                        remaining = 0;
                    }
                }

                // 48 is this demo's upper-origin limit, not a solid mask pixel.
                // Stopping there also cancels ascent without granting support.
                if (PlayerY == 48)
                    VerticalVelocity = 0;
            }
        }
        else
        {
            // Only the non-rising branch treats feet support as a landing.
            // Otherwise each positive-speed pixel checks body and support,
            // stopping immediately at the first safe supported position.
            if (HasSupport(PlayerX, PlayerY))
            {
                Grounded = true;
                VerticalVelocity = 0;
            }
            else
            {
                Grounded = false;
                if (VerticalVelocity < 4)
                    VerticalVelocity++;

                byte remaining = (byte)VerticalVelocity;
                while (remaining != 0 && PlayerY < 170)
                {
                    byte candidateY = (byte)(PlayerY + 1);
                    if (BoxClear(PlayerX, candidateY))
                    {
                        PlayerY = candidateY;
                        moved = true;
                        if (HasSupport(PlayerX, PlayerY))
                        {
                            Grounded = true;
                            VerticalVelocity = 0;
                            remaining = 0;
                        }
                        else
                        {
                            remaining--;
                        }
                    }
                    else
                    {
                        Grounded = HasSupport(PlayerX, PlayerY);
                        VerticalVelocity = 0;
                        remaining = 0;
                    }
                }

                // The last picture-safe Y is a bound, not a floor: do not
                // claim Grounded merely because downward drawing stops here.
                if (PlayerY == 170)
                {
                    Grounded = false;
                    VerticalVelocity = 0;
                }
            }
        }
        Sam.RestoreMemoryPage();
        return moved;
    }

    static bool HasSupport(byte x, byte y)
    {
        // The inset body spans X+8..X+17 and Y+4..Y+20. Row Y+21 is directly
        // underneath; ANY one of its ten foot pixels can provide support.
        return SolidRow((byte)(y + 21), (byte)(x + 8));
    }

    static bool BoxClear(byte x, byte y)
    {
        // Check every body row, excluding picture margins. Full row coverage
        // catches middle protrusions a corner-only collision test would miss.
        // Bank 12 must already be mapped by this helper's caller.
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
        // Packed masks store two four-bit nibbles per byte. Parity chooses the
        // first pixel's high/even or low/odd nibble; inspect exactly ten pixels
        // at a 128-byte-stride offset, not adjacent packed neighbours.
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
        // Bridge bank 6 and the hidden display via a resident 128-byte staging
        // row. Restore the source mapping before screen access; copy 192 rows
        // for a complete 24 KB MODE 4 initialization.
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
        // Restore ONLY the old sprite rectangle from clean scenery, not black.
        // width is bytes (13 or 14); rows still advance by the full screen
        // stride 128, even though only a small cropped segment is copied.
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
        // Skip a one-byte table header. A body is 13 columns * 22 rows *
        // (mask byte + pixel byte) = 572 bytes; the mask preserves scenery.
        // Stage in resident FrameBuffer after its own header, then restore bank
        // 4 so later drawing and ROM-serviced frame waits use normal mappings.
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
