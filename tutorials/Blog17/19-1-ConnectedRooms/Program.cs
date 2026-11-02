using SamCoupe.Sdk;

// Blog16 gave one room physical walking/jumping. Here an explicit exit link
// replaces the active room's scenery AND collision mask together. Keep only
// one working room in RAM; compact banked sources describe the other rooms.
static class Program
{
    // Player origins are pixels, not packed byte columns. byte fits bounded
    // coordinates/IDs; sbyte velocity is negative UP, positive DOWN. Grounded
    // is feet support, while SpaceWasDown is a remembered keyboard bool.
    // One room id selects both the scenery and its matching collision plane.
    static byte CurrentRoom = 1;
    static byte PlayerX = 120;
    static byte PlayerY = 106;
    static bool Grounded = true;
    static sbyte VerticalVelocity = 0;
    static bool SpaceWasDown = false;

    static void Main()
    {
        // The 24 KB screens start at physical banks 10/14; bank 6 holds clean
        // scenery and 12 its packed solidity mask. Initialize BOTH screens,
        // otherwise returning to the other buffer could reveal old room pixels.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.SetBorderColor(7);
        Sam.BeginDoubleBuffer(10, 14);
        // Keep only one decompressed background and collision plane in RAM;
        // each room's compact source data stays in banked memory.
        BuildRoom(1);
        CopyBackground();
        Sam.FlipScreen();
        CopyBackground();
        Sam.FlipScreen();

        // Image animation/frame and five-wait delay are independent of physics.
        // redraw flags position/image repaint; frameChanged flags banked loading.
        byte frame = 0;
        byte animation = 0;
        byte movePhase = 0;
        byte delay = 5;
        // Each buffer has its OWN older player footprint. Save both X and Y
        // so restoration uses the page about to be drawn, not the visible page.
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
            bool moved = false;
            bool startedJump = false;
            bool leftDown = Sam.IsKeyPressed(0xFEFE, 0x02);
            bool rightDown = Sam.IsKeyPressed(0xFEFE, 0x04);
            // Keyboard matrix bits are active-low; remember Space so a held
            // key cannot start another jump after landing.
            bool spaceDown = Sam.IsKeyPressed(0x7FFE, 0x01);
            // A press "edge" means newly down, not held. Set -10 before this
            // update's gravity reduces it to -9; grounded-only edges prevent
            // repeated mid-air jumps or bouncing immediately on landing.
            if (spaceDown && !SpaceWasDown && Grounded)
            {
                VerticalVelocity = -10;
                Grounded = false;
                if (leftDown)
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

            byte desiredX = PlayerX;
            // Alternate 1/3 PIXELS, averaging two per update. Both steps change
            // X parity, so odd positions still need the shifted image; this
            // does NOT restrict the player to byte-aligned (even-X) origins.
            // Clamp 0..230 before unsigned coordinates can wrap.
            if (leftDown)
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
            else if (rightDown)
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

            // Resolve horizontal collision first, then check a grounded exit.
            // Room replacement precedes gravity, so all following physics uses
            // the destination's mask rather than the old room's terrain.
            bool enteredRoom = false;
            // Exits are explicit links, and only grounded movement can use them.
            if (Grounded && leftDown && PlayerX == 0 && CurrentRoom == 1)
            {
                EnterRoom(2, 230, 97);
                enteredRoom = true;
            }
            else if (Grounded && rightDown && PlayerX == 230 && CurrentRoom == 2)
            {
                EnterRoom(1, 0, 98);
                enteredRoom = true;
            }
            // Both screens were replaced: reset both histories to the new
            // spawn, plus animation/frame state. Old-room coordinates must
            // never be used to restore a rectangle in the destination scene.
            if (enteredRoom)
            {
                previousX10 = PlayerX;
                previousY10 = PlayerY;
                previousX14 = PlayerX;
                previousY14 = PlayerY;
                animation = 0;
                frame = 0;
                delay = 5;
                redraw = true;
                frameChanged = true;
                moved = true;
            }
            if (ApplyGravity())
                moved = true;

            // Report actual support via border 7/3 after physics; jump-picture
            // completion is not what determines a landing.
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

            // Ground input selects idle/walks (0/2/3). Preserve a just-started
            // jump's 4/5 pictures; in air the velocity, not this art, controls Y.
            byte nextAnimation = animation;
            if (!startedJump && Grounded)
            {
                if (leftDown)
                    nextAnimation = 2;
                else if (rightDown)
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

            // Loop idle/walks, finish jumps at idle on a separate image timer.
            // A changed position can repaint while retaining the same frame.
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

            // Translate animation-local frame numbers into the shared table.
            // Prepare a one-pixel-shifted copy once per image change. Two
            // four-bit pixels fit each byte; odd-X copies need 14 bytes, not 13.
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

            // Restore THIS hidden page's old rectangle, draw current pixels,
            // flip only after completion, then record its new X/Y and switch
            // history for the next hidden page. Reversing erase/draw loses art.
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

                // Pixel Y chooses a 128-byte row and X/2 a packed byte column.
                // ushort is an unsigned 16-bit OFFSET from the screen's start.
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

            // Helpers have restored data/screen mappings before this wait,
            // allowing the normal ROM interrupt handler to service frame timing.
            Sam.WaitForFrames(1);
            if (delay != 0)
                delay--;
        }
    }

    static void ConstrainGroundedX(byte desiredX)
    {
        // acceptedX/Y hold last-safe positions. Test every horizontal pixel,
        // preventing "tunnelling" across an obstacle between request endpoints.
        // All nested collision helpers assume bank 12 stays selected here.
        byte acceptedX = PlayerX;
        byte acceptedY = PlayerY;
        bool grounded = Grounded;
        bool movingRight = desiredX > PlayerX;
        bool blocked = false;
        // The active room's packed solidity nibbles occupy one reusable bank.
        Sam.MapMemoryPage(12);

        // Airborne horizontal movement keeps Y: don't snap a jump to a nearby
        // slope. Ground motion instead searches supported heights within +/-3
        // rows, requiring an empty body as well as feet support.
        if (!grounded)
        {
            while (acceptedX != desiredX && !blocked)
            {
                byte candidateX = acceptedX;
                if (movingRight)
                    candidateX++;
                else
                    candidateX--;

                if (BoxClearCandidate(candidateX, acceptedY, acceptedY, movingRight))
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

                byte firstY = acceptedY;
                // Restrict ground following to small slopes, never a distant
                // platform. 170 keeps all 22 picture rows on a 192-row screen.
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
                // Search highest candidate first; BOTH feet support and clear
                // body are required. If no support exists but the body is clear,
                // accept the sideways ledge exit and let gravity handle falling.
                byte candidateY = acceptedY;
                byte searchY = firstY;
                while (searchY <= lastY && !found)
                {
                    if (HasSupport(candidateX, searchY) &&
                        BoxClearCandidate(candidateX, searchY, acceptedY, movingRight))
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
                else if (BoxClearCandidate(candidateX, acceptedY, acceptedY, movingRight))
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
        // Downward acceleration adds one to signed velocity. Negative means
        // ascent (smaller Y); positive descent is capped at +4. Split either
        // speed into single-pixel probes so thin platforms/ceilings cannot be
        // skipped. An upward hit cancels speed, not grants Grounded; downward
        // support stops at the last clear body position. 48/170 are origin
        // bounds, not terrain. Return Y changes to request repainting.
        bool moved = false;
        Sam.MapMemoryPage(12);
        if (VerticalVelocity < 0)
        {
            // Apply acceleration before ascent. At -1 it becomes zero with
            // no upward step; the non-rising branch handles the next update.
            VerticalVelocity++;
            if (VerticalVelocity < 0)
            {
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
                        // A blocked upward candidate is not accepted. Stop
                        // ascent at the last safe Y without declaring a landing.
                        VerticalVelocity = 0;
                        remaining = 0;
                    }
                }

                if (PlayerY == 48)
                    VerticalVelocity = 0;
            }
        }
        else
        {
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
                // Remaining is this update's downward step budget, not stored
                // velocity. Landing cancels both velocity and remaining budget.
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
        // Inset body: X+8..X+17, Y+4..Y+20. ANY solid foot pixel on Y+21
        // supports it. This is separate from checking that the body is empty.
        return SolidRow((byte)(y + 21), (byte)(x + 8));
    }

    static bool BoxClear(byte x, byte y)
    {
        // Scan every inset-body row/pixel, not only corners; thin middle
        // protrusions must block us too. The caller mapped collision bank 12.
        byte firstX = (byte)(x + 8);
        byte firstY = (byte)(y + 4);
        ushort offset = (ushort)(firstY * 128 + firstX / 2);
        return !Sam.AnySolidPixelInPackedRectangle(
            TutorialCollision.CollisionMask, offset, (byte)(firstX & 1), 10, 17);
    }

    static bool BoxClearCandidate(byte x, byte y, byte acceptedY, bool movingRight)
    {
        if (y != acceptedY)
            return BoxClear(x, y);

        // The last accepted body was clear and candidates move one pixel at a
        // time, so at the same Y only the newly entered edge can collide.
        byte edgeX = (byte)(x + 8);
        if (movingRight)
            edgeX = (byte)(x + 17);
        return !Sam.AnySolidPixelInPackedScreenColumn(
            TutorialCollision.CollisionMask,
            edgeX,
            (byte)(y + 4),
            17);
    }

    static bool SolidRow(byte y, byte firstX)
    {
        // A nibble is four bits, one packed mask pixel. Even X starts in the
        // high nibble, odd in the low; row stride is 128 bytes like the screen.
        ushort offset = (ushort)(y * 128 + firstX / 2);
        byte parity = 0;
        if ((firstX & 1) != 0)
            parity = 1;
        // Probe only the ten inset body pixels; an odd first pixel starts in
        // the low nibble, so adjacent scenery must not become a collision.
        return Sam.AnySolidPixelInPackedRow(
            TutorialCollision.CollisionMask,
            offset,
            parity,
            10);
    }

    static void EnterRoom(byte room, byte x, byte y)
    {
        // These arguments are the destination ID and pixel spawn origin.
        // Reset velocity/support, load its matching collision and background,
        // then replace both screens. Main resets animation and page histories.
        // Keep the key latch: held Space should not count as another fresh edge.
        CurrentRoom = room;
        PlayerX = x;
        PlayerY = y;
        Grounded = true;
        VerticalVelocity = 0;
        LoadCollision(room);
        BuildRoom(room);
        // Replace the hidden bitmap first, flip when complete, then initialize
        // the other page so no old-room pixels can be restored later.
        CopyBackground();
        Sam.FlipScreen();
        CopyBackground();
        Sam.FlipScreen();
    }

    static void LoadCollision(byte room)
    {
        // "Runs" encode repeated values once with a repetition count, reducing
        // banked storage. Choose the same room ID as scenery; decode into bank
        // 12 so later body/foot probes see only the current room's solidity.
        byte[] runs = Room1CollisionRunsData.Room1Runs;
        if (room == 2)
            runs = Room2CollisionRunsData.Room2Runs;
        // The stream stores little-endian 16-bit run lengths and packed mask
        // bytes. The SDK kernel expands it into the single working mask bank.
        Sam.ExpandByteRuns(runs, 8, TutorialCollision.CollisionMask, 12);
    }

    static void BuildRoom(byte room)
    {
        // RLE means run-length encoding: expand repetitions into the exact
        // 192-row, 128-byte-stride clean image used for drawing/restoration.
        // byte[] selects an existing source, not a second working-room allocation.
        byte[] encoded = Room1BackgroundData.Room1Background;
        if (room == 2)
            encoded = Room2BackgroundData.Room2Background;
        // Decode one exact 24 KB MODE 4 image into page 6; the screen copier
        // then transfers rows to the hidden display page.
        Sam.DecodeRleBackground(encoded, 8, TutorialRooms.Background, 6);
    }

    static void CopyBackground()
    {
        // Source/destination offsets are zero, width is 128 BYTES, height 192
        // rows. The SDK stages resident rows because source bank 6 and the
        // hidden screen can occupy the same paging window at different times.
        // The source is banked scenery; the typed kernel stages rows before
        // paging the hidden screen, avoiding an alias between the two pages.
        Sam.CopyBankedBytesToScreen(TutorialRooms.Background, 6, 0, 0, 128, 192);
    }

    static void RestoreBackground(byte x, byte y, byte width)
    {
        // Copy 22 OLD sprite rows, using a screen-relative ushort byte offset.
        // width is 13/14 according to old X parity; the source/destination row
        // stride remains 128. Restore clean scenery, not a black rectangle.
        // Each display page keeps its own previous player position, so restore
        // only that page's old rectangle before drawing the next frame.
        ushort screenOffset = (ushort)(y * 128 + x / 2);
        Sam.CopyBankedBytesToScreen(
            TutorialRooms.Background,
            6,
            screenOffset,
            screenOffset,
            width,
            22);
    }

    static void LoadSprite(byte index)
    {
        // The frame table has one header byte, then 572-byte bodies:
        // 13 packed columns * 22 rows * (mask byte + pixel byte). A mask keeps
        // background bits; pixels add the artwork. Stage the body after the
        // resident header and restore bank 4 before drawing or ROM frame waits.
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
