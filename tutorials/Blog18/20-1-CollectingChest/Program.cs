using SamCoupe.Sdk;

// Blog17 connected two rooms. Now one chest can be collected on a fresh Enter
// press while grounded and standing still. Its persistent bool controls room
// composition: returning to the room cannot bring back an already collected chest.
static class Program
{
    // byte origins/room IDs are small unsigned values; sbyte velocity is UP
    // when negative and DOWN when positive. Grounded means feet support.
    // Space/Enter latches remember keys, ChestCollected remembers world state.
    // One room id selects both the scenery and its matching collision plane.
    static byte CurrentRoom = 1;
    static byte PlayerX = 120;
    static byte PlayerY = 106;
    static bool Grounded = true;
    static sbyte VerticalVelocity = 0;
    static bool SpaceWasDown = false;
    static bool EnterWasDown = false;
    static bool ChestCollected = false;

    static void Main()
    {
        // Screen starts 10/14 are physical RAM banks, not coordinates.
        // Initialize both buffers from the same clean scene (background bank 6).
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

        // animation/frame choose pictures; delay is the five-wait image timer.
        // redraw and frameChanged separate repainting from reloading sprite data.
        byte frame = 0;
        byte animation = 0;
        byte movePhase = 0;
        byte delay = 5;
        // Track X/Y belonging to each page's old sprite, not just the currently
        // visible player. The other page may still have an older footprint.
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
            // A press edge is newly down after being released. Require support
            // so holding Space cannot auto-jump on landing. The initial -10 is
            // reduced by this update's gravity before moving the jump.
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
            // Alternate 1/3 PIXEL steps to average two per update; both change
            // parity, so odd X needs a shifted image, not byte-only positioning.
            // Clamps keep the unsigned origin within screen-safe 0..230.
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

            // Horizontal collision, then room replacement, then gravity, then
            // collection: the interaction sees final coordinates/current room.
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
            // Reset BOTH page histories after replacing their room pixels.
            // Old-room coordinates cannot describe a sprite in this new scene.
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
            // On collection the clean scene changes, not just the current
            // visible screen. Recompose and copy both buffers so restoration
            // cannot resurrect the chest from an old background.
            if (TryCollectChest())
            {
                // Rebuild both buffers after changing the world-item state;
                // otherwise the old chest could reappear on the other page.
                BuildRoom(CurrentRoom);
                CopyBackground();
                Sam.FlipScreen();
                CopyBackground();
                Sam.FlipScreen();
                previousX10 = PlayerX;
                previousY10 = PlayerY;
                previousX14 = PlayerX;
                previousY14 = PlayerY;
                redraw = true;
            }

            // Border 7/3 reports supported/airborne after physical movement.
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

            // Idle/walk pictures follow ground input. Jump 4/5 can continue in
            // the air, but image completion never determines physical landing.
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

            // Image sequences advance every five waits: idle/walks loop, jumps
            // finish at idle. Movement can repaint between image changes.
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

            // Translate sequence-local frames, load once, and prepare even/
            // odd-X copies. Two pixels share a packed byte; shifting one pixel
            // expands the footprint from 13 to 14 bytes.
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

            // Erase this hidden page's OLD rectangle from clean scenery, draw
            // the NEW player, flip only the finished image, then update that
            // page's X/Y history and select the other hidden page.
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

                // Convert pixels to byte offset: row stride 128 plus X/2.
                // ushort is an unsigned 16-bit screen offset, not a RAM pointer.
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

            // All helpers restore normal memory accessibility before the
            // frame wait, so the ROM interrupt can service its timing.
            Sam.WaitForFrames(1);
            if (delay != 0)
                delay--;
        }
    }

    static void ConstrainGroundedX(byte desiredX)
    {
        // One candidate pixel at a time prevents tunnelling through thin walls.
        // Air motion tests the body at fixed Y; ground motion can search +/-3
        // heights for supported, clear slope positions. Keep bank 12 mapped
        // while the nested body/foot helpers inspect its collision bytes.
        byte acceptedX = PlayerX;
        byte acceptedY = PlayerY;
        bool grounded = Grounded;
        bool movingRight = desiredX > PlayerX;
        bool blocked = false;
        // The active room's packed solidity nibbles occupy one reusable bank.
        Sam.MapMemoryPage(12);

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

                byte firstY = acceptedY;
                // A +/-3-row search follows only a small slope; clamp picture
                // origins to 0..170 so all 22 sprite rows remain on-screen.
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
                // Highest supported AND body-clear candidate wins. If there
                // is no nearby support but old-Y body space is clear, accept
                // the ledge exit and leave gravity to lower the player.
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
        // Velocity is signed pixels per update: negative rises, positive falls.
        // Add one for acceleration; descent caps at +4. Test every intervening
        // pixel so thin ceilings/platforms cannot be skipped. Upward collision
        // cancels speed without claiming support; descent checks support after
        // every accepted row. Bounds Y=48/170 are drawing limits, not terrain.
        // bool moved asks for a redraw only if physical Y actually changed.
        bool moved = false;
        Sam.MapMemoryPage(12);
        if (VerticalVelocity < 0)
        {
            // Acceleration happens before upward steps. -1 becomes zero
            // without a step; descent/support is considered next update.
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
                        // Keep the last clear origin, not candidateY. An
                        // upward hit cancels speed but is not feet support.
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
                // This is only the current update's step budget. A supported
                // landing clears it and velocity, preventing further fall.
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
        // Body is X+8..X+17, Y+4..Y+20. One solid foot pixel on the row
        // beneath it (Y+21) is sufficient support; body clearance is separate.
        return SolidRow((byte)(y + 21), (byte)(x + 8));
    }

    static bool BoxClear(byte x, byte y)
    {
        // Ignore artwork margins, but scan ALL inset-body rows and pixels:
        // corners alone miss middle protrusions. Caller has selected bank 12.
        byte firstX = (byte)(x + 8);
        byte firstY = (byte)(y + 4);
        ushort offset = (ushort)(firstY * 128 + firstX / 2);
        return !Sam.AnySolidPixelInPackedRectangle(
            TutorialCollision.CollisionMask, offset, (byte)(firstX & 1), 10, 17);
    }

    static bool SolidRow(byte y, byte firstX)
    {
        // Four-bit mask pixels are "nibbles": even X chooses high, odd low.
        // The SDK scans ten exact pixels beginning at this 128-byte-stride offset.
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
        // Change ID/spawn, reset velocity, choose matching mask and scenery,
        // replace both buffers. Do NOT clear ChestCollected: it belongs to
        // the persistent world, not the temporary active-room bitmap.
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
        // Run-length encoding stores a repeat count and value instead of every
        // repeated mask byte. Expand the selected room into working bank 12,
        // independent of the chest artwork drawn in the scenery background.
        byte[] runs = Room1CollisionRunsData.Room1Runs;
        if (room == 2)
            runs = Room2CollisionRunsData.Room2Runs;
        // The stream stores little-endian 16-bit run lengths and packed mask
        // bytes. The SDK kernel expands it into the single working mask bank.
        Sam.ExpandByteRuns(runs, 8, TutorialCollision.CollisionMask, 12);
    }

    static void BuildRoom(byte room)
    {
        // Decode the room's compact RLE (run-length encoded) scene to bank 6.
        // Overlay the chest only when still in the world. The clean composed
        // image is the authority for BOTH initial screens and later restoration.
        byte[] encoded = Room1BackgroundData.Room1Background;
        if (room == 2)
            encoded = Room2BackgroundData.Room2Background;
        // Decode one exact 24 KB MODE 4 image into page 6; the screen copier
        // then transfers rows to the hidden display page.
        Sam.DecodeRleBackground(encoded, 8, TutorialRooms.Background, 6);
        if (room == 1 && !ChestCollected)
        {
            // The extracted image keeps its two-byte dimensions header; skip
            // it, then overlay ten packed bytes across sixteen screen rows.
            Sam.ComposeTransparentImage(
                TutorialChest.ChestImage,
                8,
                TutorialRooms.Background,
                6,
                2,
                (ushort)(111 * 128 + 24),
                10,
                16,
                10);
        }
    }

    static bool TryCollectChest()
    {
        // An Enter edge counts once even if rejected; releasing and pressing
        // again is required. Gate on grounded, correct room, uncollected chest
        // and no horizontal key held. This lesson stores only one collected
        // bool, not a visible multi-item inventory or a drop operation yet.
        bool enterDown = Sam.IsKeyPressed(0xBFFE, 0x01);
        bool freshPress = enterDown && !EnterWasDown;
        // Consume every press, even if the player is airborne or too far away.
        EnterWasDown = enterDown;
        if (!freshPress || !Grounded || CurrentRoom != 1 || ChestCollected)
            return false;
        if (Sam.IsKeyPressed(0xFEFE, 0x02) || Sam.IsKeyPressed(0xFEFE, 0x04))
            return false;

        // Algebraically compare the byte origin against the exact overlap
        // interval; these guards avoid four 32-bit additions per update.
        // These half-open intervals accept player origins X=31..59, Y=91..122.
        // They express the implemented overlap test directly; do not compare
        // screen byte columns with the player's pixel coordinates.
        if (PlayerX < 31)
            return false;
        if (PlayerX >= 60)
            return false;
        if (PlayerY < 91)
            return false;
        if (PlayerY >= 123)
            return false;

        ChestCollected = true;
        return true;
    }

    static void CopyBackground()
    {
        // Width 128 is BYTES, height 192 is ROWS: one full 24 KB image.
        // Resident row staging avoids reading a bank hidden by screen paging.
        // The source is banked scenery; the typed kernel stages rows before
        // paging the hidden screen, avoiding an alias between the two pages.
        Sam.CopyBankedBytesToScreen(TutorialRooms.Background, 6, 0, 0, 128, 192);
    }

    static void RestoreBackground(byte x, byte y, byte width)
    {
        // Copy the old 22-row sprite footprint from the currently composed
        // scene; width 13/14 depends on OLD X parity. Both offsets use Y*128+X/2
        // and both row strides remain 128 despite the small cropped width.
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
        // One table-header byte precedes 572-byte bodies: 13 columns * 22 rows
        // * two interleaved bytes (mask, pixels). Preserve the resident header,
        // stage only the body, then restore bank 4 before drawing or ROM waits.
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
