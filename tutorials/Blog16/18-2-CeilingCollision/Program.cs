using SamCoupe.Sdk;

// The physical-jump example already checks body clearance during ascent.
// This variant adds a visible, solid ceiling so that rule can be demonstrated:
// hit its underside, cancel upward velocity, then fall rather than pass through.
static class Program
{
    // Pixel origins fit byte; signed sbyte velocity distinguishes UP (negative,
    // decreasing Y) from DOWN (positive). Grounded means support, not zero speed.
    // SpaceWasDown remembers the prior update for press-edge detection.
    static byte PlayerX = 120;
    static byte PlayerY = 106;
    static bool Grounded = true;
    static sbyte VerticalVelocity = 0;
    static bool SpaceWasDown = false;
    // Artwork and collision are DIFFERENT planes. 0x77 paints two colour-7
    // pixels per packed byte, whereas 0xFF marks both collision pixels solid.
    // readonly fixes each array reference, not the values in mutable scene RAM.
    static readonly byte[] CeilingPixels = new byte[]
    {
        0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77,
        0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77,
        0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77,
        0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77, 0x77
    };
    static readonly byte[] CeilingMask = new byte[]
    {
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
    };

    static void Main()
    {
        // Add the ceiling to the clean scene AND mask before initializing
        // both screens. Updating only pixels would make a ghost obstacle;
        // updating only the mask would create an invisible wall.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.SetBorderColor(7);
        Sam.BeginDoubleBuffer(10, 14);
        AddTestCeiling();
        CopyBackground();
        Sam.FlipScreen();
        CopyBackground();
        Sam.FlipScreen();

        // Picture sequence/frame and five-wait timer are independent of
        // velocity. Repainting a new position need not load another image.
        byte frame = 0;
        byte animation = 0;
        byte movePhase = 0;
        byte delay = 5;
        // Each buffer retains its own last sprite: keep its old X and Y
        // separately so the correct hidden footprint is restored during jumps.
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
            // A press edge means Space is down NOW but was up last update.
            // Consume held/rejected presses too, avoiding repeat jumps at landing.
            // A grounded fresh press sets -10; this update's gravity reduces
            // it to -9 before moving. Keyboard matrix bits are active-low.
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

            // Request alternating 1/3 horizontal pixels, safely clamped to
            // 0..230. Resolve horizontal/body collision before vertical physics.
            // startedJump stops ground-animation choice replacing jump frame 0.
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

            // Border 7 means foot support; border 3 means airborne. A ceiling
            // hit sets velocity zero without making the player grounded.
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

            // Ground input picks idle/walks; jump images 4/5 continue in air.
            // Finishing their picture sequence does not finish the trajectory.
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

            // Advance images on a five-wait cadence, loop idle/walks and finish
            // jump pictures at idle. Physical movement uses every update.
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

            // Translate local frame indices to the shared table and prepare
            // even/odd-X images only on an image change. Two pixels occupy a
            // packed byte, so a one-pixel shift adds one byte to the footprint.
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

            // Restore this hidden screen's OLD sprite, draw its current one,
            // flip only the completed picture, then record its new X/Y and
            // choose the other page. Never restore using the other page's history.
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

                // Convert pixels to a screen-relative byte offset: 128 bytes
                // per row, X/2 for packed column. ushort is unsigned 16-bit.
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
        // Keep last-safe candidate origins and move one pixel at a time.
        // Checking only the final requested X could tunnel through thin walls.
        // Bank 12 remains selected for all support/body probes in this method.
        byte acceptedX = PlayerX;
        byte acceptedY = PlayerY;
        bool grounded = Grounded;
        bool movingRight = desiredX > PlayerX;
        bool blocked = false;
        Sam.MapMemoryPage(12);

        // In air, keep Y fixed for horizontal testing: snapping onto a nearby
        // slope would interfere with the jump's vertical velocity.
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

                // Ground motion searches supported clear-body positions within
                // three rows above/below. Clamp to screen-safe 0..170 origins.
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
        // Signed velocity increases by one toward DOWN each update. This slows
        // ascent to zero, then accelerates descent to the +4 speed limit.
        // bool moved reports coordinate changes, not an animation change.
        bool moved = false;
        Sam.MapMemoryPage(12);
        if (VerticalVelocity < 0)
        {
            VerticalVelocity++;
            if (VerticalVelocity < 0)
            {
                // Test EVERY upward pixel, even at high jump speed. That
                // prevents tunnelling past our thin three-row test ceiling.
                // Negation makes the still-negative speed a positive counter.
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
                        // Do not commit blocked candidateY. Keep the last clear
                        // position, cancel ascent and consume the remaining steps.
                        // No Grounded=true: hitting the ceiling is not landing.
                        VerticalVelocity = 0;
                        remaining = 0;
                    }
                }

                // The upper origin bound is separate from the collision mask.
                if (PlayerY == 48)
                    VerticalVelocity = 0;
            }
        }
        else
        {
            // Non-rising motion may land. Check support after every accepted
            // downward pixel so a platform stops us immediately; blocked body
            // motion also rechecks feet before asserting Grounded.
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

                // Screen-safe lower limit is not a solid floor.
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

    static void AddTestCeiling()
    {
        // At Y=85, packed byte X=55 (pixel X=110), write 32 bytes = 64 pixels
        // across three rows. offset is a byte offset, advanced by screen stride
        // 128, not the ceiling width. Copy resident source rows into scene bank
        // 6, then repeat the EXACT placement in collision bank 12.
        ushort offset = 85 * 128 + 55;
        byte row = 0;
        Sam.MapMemoryPage(6);
        while (row < 3)
        {
            Sam.CopyBytes(CeilingPixels, 0, TutorialRooms.Background, offset, 32);
            offset += 128;
            row++;
        }
        Sam.RestoreMemoryPage();

        offset = 85 * 128 + 55;
        row = 0;
        Sam.MapMemoryPage(12);
        while (row < 3)
        {
            Sam.CopyBytes(CeilingMask, 0, TutorialCollision.CollisionMask, offset, 32);
            offset += 128;
            row++;
        }
        Sam.RestoreMemoryPage();
    }

    static bool HasSupport(byte x, byte y)
    {
        // Body is inset to X+8..X+17, Y+4..Y+20. ANY solid pixel on Y+21
        // supplies feet support; touching a ceiling above does not qualify.
        return SolidRow((byte)(y + 21), (byte)(x + 8));
    }

    static bool BoxClear(byte x, byte y)
    {
        // Scan all body rows and all ten pixels, not just the corners. This
        // catches ceiling contact and small mid-body protrusions. Caller maps
        // collision bank 12 before using this helper.
        byte firstX = (byte)(x + 8);
        byte firstY = (byte)(y + 4);
        ushort offset = (ushort)(firstY * 128 + firstX / 2);
        return !Sam.AnySolidPixelInPackedRectangle(
            TutorialCollision.CollisionMask, offset, (byte)(firstX & 1), 10, 17);
    }

    static bool SolidRow(byte y, byte firstX)
    {
        // A nibble is four bits, one packed mask pixel. Even X uses the high
        // nibble, odd X the low. Probe exactly ten pixels with 128-byte stride
        // so unrelated pixels in neighbouring half-bytes cannot block us.
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
        // Stage each bank-6 row in resident GraphicsRow before mapping/writing
        // the hidden screen. This avoids hiding our source with screen paging.
        // 192 rows * 128 bytes copies the full 24 KB image including the ceiling.
        Sam.CopyBankedBytesToScreen(TutorialRooms.Background, 6, 0, 0, 128, 192);
    }

    static void RestoreBackground(byte x, byte y, byte width)
    {
        // Clean scenery, including the ceiling, replaces only the OLD sprite's
        // 22-row footprint. width is bytes: 13 even-X or 14 shifted odd-X.
        // Source/destination rows advance 128 bytes, not this cropped width.
        ushort screenOffset = (ushort)(y * 128 + x / 2);
        Sam.CopyBankedBytesToScreen(
            TutorialRooms.Background, 6,
            screenOffset, screenOffset, width, 22);
    }

    static void LoadSprite(byte index)
    {
        // Header byte then 572-byte bodies: 13 columns * 22 rows * two bytes
        // (mask, pixels). Preserve the resident frame header while loading its
        // body from bank 4, and restore mapping before drawing or ROM waits.
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
