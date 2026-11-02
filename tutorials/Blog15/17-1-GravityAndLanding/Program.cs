using SamCoupe.Sdk;

// Blog14 detected lost ledge support without falling. Here unsupported players
// accelerate downward and land on solid support. Falling changes physical Y;
// the older jump-picture sequences still do not create an upward velocity.
static class Program
{
    // Pixel origins fit in byte. Grounded is support state, NOT speed:
    // FallSpeed is a separate nonnegative pixels-per-update count, capped at 4.
    // Starting unsupported at Y=70 deliberately demonstrates gravity.
    static byte PlayerX = 120;
    static byte PlayerY = 70;
    static bool Grounded = false;
    static byte FallSpeed = 0;

    static void Main()
    {
        // Initialize the clean background on both display buffers (10/14),
        // keeping bank 6 untouched for later small-rectangle restoration.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.SetBorderColor(7);
        Sam.BeginDoubleBuffer(10, 14);
        CopyBackground();
        Sam.FlipScreen();
        CopyBackground();
        Sam.FlipScreen();

        // animation/frame select the sequence/image; delay times image changes.
        // bool redraw/frameChanged separate repainting from banked image loads.
        byte frame = 0;
        byte animation = 0;
        byte movePhase = 0;
        byte delay = 5;
        // Remember each buffer's OLD X/Y. Falling makes Y history just as
        // necessary as X history when erasing a sprite from the hidden page.
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
            // Apply keyboard-driven horizontal movement BEFORE gravity. The
            // new X determines whether the feet still have support this update.
            // Clamp alternating 1/3-pixel requests to avoid byte wraparound.
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
            if (ApplyGravity())
                moved = true;

            // The border exposes supported (7) versus falling/unsupported (3).
            // Physics has already updated Grounded before this display decision.
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

            // 0/2/3 = idle/walk, 1/4/5 = visual jumps. Image selection is
            // independent of FallSpeed, so these jumps are not yet physical.
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

            // Loop idle/walks and finish jumps at idle on a five-wait cadence.
            // A falling position can redraw even when the same image is reused.
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

            // Translate sequence indices and stage one frame, then make an odd
            // pixel-X version. Two four-bit pixels occupy one byte; the shifted
            // frame straddles 14 bytes instead of the unshifted 13.
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

            // Restore the hidden page's OLD rectangle, draw its current image,
            // then flip the finished page. Update only that page's X/Y history
            // before choosing the other buffer for a future redraw.
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

                // Pixel Y selects a 128-byte row, X/2 selects its byte column.
                // ushort is a valid screen-relative 16-bit byte offset.
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
        // Take one horizontal pixel at a time to avoid tunnelling through thin
        // obstacles. Keep bank 12 selected during all collision probes and
        // commit accepted coordinates only after restoring its mapping.
        byte acceptedX = PlayerX;
        byte acceptedY = PlayerY;
        bool grounded = Grounded;
        bool movingRight = desiredX > PlayerX;
        bool blocked = false;
        Sam.MapMemoryPage(12);

        // In the air, test a clear body at the existing height. Do not snap
        // to nearby ground: that would unexpectedly pull a falling player up.
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

                // On ground, permit a three-row slope up/down, clamped to
                // 0..170. Search top-to-bottom for supported AND body-clear Y.
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
                // No nearby support means a ledge exit, not a blocked step.
                // Gravity later in the update supplies its downward motion.
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
        // Support cancels falling immediately. Otherwise acceleration adds one
        // to speed per update up to 4 pixels; it is speed, not an animation index.
        // Return whether Y moved so Main knows the screen needs repainting.
        bool moved = false;
        Sam.MapMemoryPage(12);
        if (HasSupport(PlayerX, PlayerY))
        {
            Grounded = true;
            FallSpeed = 0;
        }
        else
        {
            Grounded = false;
            if (FallSpeed < 4)
                FallSpeed++;

            // Break the speed into ONE-PIXEL steps. A final-position-only test
            // could skip a one-row platform ("tunnelling"). After each accepted
            // row, check support so landing stops exactly above the surface.
            byte remaining = FallSpeed;
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
                        FallSpeed = 0;
                        remaining = 0;
                    }
                    else
                    {
                        remaining--;
                    }
                }
                // A blocked candidate is never committed. Recheck whether
                // this safe position actually has feet support before claiming
                // a landing, and cancel the rest of this update's fall.
                else
                {
                    Grounded = HasSupport(PlayerX, PlayerY);
                    FallSpeed = 0;
                    remaining = 0;
                }
            }

            // 170 is the last screen-safe origin for a 22-row sprite. This
            // safety limit is NOT solid terrain: leave Grounded false there.
            if (PlayerY == 170)
            {
                Grounded = false;
                FallSpeed = 0;
            }
        }
        Sam.RestoreMemoryPage();
        return moved;
    }

    static bool HasSupport(byte x, byte y)
    {
        // Body is X+8..X+17, Y+4..Y+20. ANY solid pixel on row Y+21
        // supports it. These probes assume caller has mapped collision bank 12.
        return SolidRow((byte)(y + 21), (byte)(x + 8));
    }

    static bool BoxClear(byte x, byte y)
    {
        // The inset body excludes artwork margins. Scan all its rows/pixels,
        // not merely corners, so interior terrain cannot be overlooked.
        byte firstX = (byte)(x + 8);
        byte firstY = (byte)(y + 4);
        ushort offset = (ushort)(firstY * 128 + firstX / 2);
        return !Sam.AnySolidPixelInPackedRectangle(
            TutorialCollision.CollisionMask, offset, (byte)(firstX & 1), 10, 17);
    }

    static bool SolidRow(byte y, byte firstX)
    {
        // Packed mask pixels are four-bit nibbles. Even firstX starts at the
        // high nibble, odd at the low; scan exactly ten body/foot pixels using
        // the 128-byte row stride and an unsigned 16-bit offset.
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
        // Initialize a full 24 KB screen: stage each of 192 rows from bank 6
        // in resident memory, restore the bank, then copy to the hidden display.
        Sam.CopyBankedBytesToScreen(TutorialRooms.Background, 6, 0, 0, 128, 192);
    }

    static void RestoreBackground(byte x, byte y, byte width)
    {
        // Erase only the OLD 22-row sprite from this buffer, using clean
        // scenery rather than black. width is packed bytes (13 or 14), while
        // each next source/screen row begins 128 bytes after the previous one.
        ushort screenOffset = (ushort)(y * 128 + x / 2);
        Sam.CopyBankedBytesToScreen(
            TutorialRooms.Background, 6,
            screenOffset, screenOffset, width, 22);
    }

    static void LoadSprite(byte index)
    {
        // A one-byte header precedes 572-byte bodies: 13 columns * 22 rows *
        // two interleaved bytes (mask/pixels). Stage a body in resident memory
        // after its preserved header, then restore source bank 4.
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
