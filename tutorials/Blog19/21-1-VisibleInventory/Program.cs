using SamCoupe.Sdk;

// Blog18 remembered one collected chest. Now four world items share a visible
// three-slot inventory. Pickups push IDs; drops pop the newest ID only after a
// safe supported placement is found. The HUD is part of the clean background.
static class Program
{
    // X/Y are pixel origins; byte holds bounded IDs/coordinates. sbyte velocity
    // is negative UP, positive DOWN; Grounded describes feet support. Key bools
    // remember presses across updates, not physical movement or inventory state.
    // One room id selects both the scenery and its matching collision plane.
    static byte CurrentRoom = 1;
    static byte PlayerX = 120;
    static byte PlayerY = 106;
    static bool Grounded = true;
    static sbyte VerticalVelocity = 0;
    static bool SpaceWasDown = false;
    static bool EnterWasDown = false;
    static byte InventoryCount = 0;
    static byte ActionFlash = 0;

    // World records stay separate from the carried stack. Room 255 means an
    // item is in inventory; the three slot identifiers are ordered oldest-first.
    // Parallel arrays describe the SAME item at the same index: chest 0,
    // coin 1, axe 2, scroll 3. ItemX/ItemWidths are packed BYTES (two pixels per
    // byte), ItemY/ItemHeights are rows; ItemPixelWidths is for collision probes.
    // readonly fixes array references, but elements change on pickup/drop.
    // 255 is a sentinel: a special value meaning carried/empty, not a room ID.
    static readonly byte[] ItemX = new byte[] { 24, 40, 60, 80 };
    static readonly byte[] ItemY = new byte[] { 111, 110, 111, 110 };
    static readonly byte[] ItemRooms = new byte[] { 1, 1, 1, 1 };
    static readonly byte[] ItemWidths = new byte[] { 10, 11, 10, 10 };
    static readonly byte[] ItemPixelWidths = new byte[] { 20, 22, 20, 20 };
    static readonly byte[] ItemHeights = new byte[] { 16, 17, 16, 16 };
    static readonly byte[] InventorySlots = new byte[] { 255, 255, 255 };

    static void Main()
    {
        // Load matching scenery/mask before initializing BOTH screen buffers.
        // Screen starts 10/14, background 6 and collision 12 are physical banks.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.SetBorderColor(7);
        Sam.BeginDoubleBuffer(10, 14);
        // Keep only one decompressed background and collision plane in RAM;
        // each room's compact source data stays in banked memory.
        LoadCollision(CurrentRoom);
        BuildRoom(CurrentRoom);
        CopyBackground();
        Sam.FlipScreen();
        CopyBackground();
        Sam.FlipScreen();

        // animation/frame select art, delay counts five waits between images.
        // redraw/frameChanged separate repaint from banked frame loading.
        byte frame = 0;
        byte animation = 0;
        byte movePhase = 0;
        byte delay = 5;
        // Each page retains its own older player image. Keep its X/Y history,
        // otherwise erasing the hidden page at the visible page's position trails.
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
            // Newly pressed ("edge"), not held, Space can launch when supported.
            // Initial -10 is reduced by gravity in this update. Consume key
            // state every update so landing with held Space never relaunches.
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
            // Alternate 1/3 PIXELS, averaging two per update. Each changes
            // parity: odd X needs shifted pixels, not byte-only positioning.
            // Clamp to 0..230 before unsigned coordinates can wrap.
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

            // Resolve horizontal motion, destination room, gravity, then item
            // action. An action uses the final position and current room's mask.
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
            // Both bitmaps changed rooms: discard old-room X/Y history for
            // BOTH pages and restart the animation at the destination spawn.
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
            // A successful pickup/drop changes authoritative world records and
            // HUD slots. Recompose both screens before drawing the player again.
            // A failed action must not remove any carried item.
            if (TryInventoryAction())
            {
                RefreshRoom();
                redraw = true;
                ActionFlash = 0;
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

            // Failure flashes border 5 for a countdown of updates. Otherwise
            // border 7/3 reports support/airborne; feedback does not change physics.
            if (ActionFlash != 0)
            {
                Sam.SetBorderColor(5);
                ActionFlash--;
            }
            else if (Grounded != (displayedGrounded != 0))
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

            // Ground input chooses idle/walks; preserve just-started jump 4/5.
            // Pictures follow state, never decide velocity, support or landing.
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

            // Idle/walks loop; completed jump pictures return to idle every
            // five-wait image cadence, independent of physical movement.
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

            // Map animation-local frames to the shared table, then prepare
            // even/odd-X images once. Two four-bit pixels occupy a byte; shifted
            // images straddle 14 packed bytes instead of 13.
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

            // Restore this hidden page's OLD rectangle, draw the NEW player,
            // flip the finished screen, then update its X/Y history and switch
            // pages. Clean scenery includes items/HUD, so restoration preserves them.
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

                // Player coordinates are PIXELS. Convert to byte offset using
                // 128-byte row stride and X/2. ushort is an unsigned 16-bit
                // screen-relative offset, not an absolute banked RAM pointer.
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

            // Drawing/bank helpers restore normal memory access before the
            // ROM-serviced frame wait. Image countdown follows the wait.
            Sam.WaitForFrames(1);
            if (delay != 0)
                delay--;
        }
    }

    static void ConstrainGroundedX(byte desiredX)
    {
        // Accept one pixel at a time to avoid tunnelling through narrow solids.
        // Air motion keeps Y; ground motion searches supported clear heights
        // within +/-3 rows. Bank 12 remains mapped for nested collision probes.
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
                // Search only +/-3 rows for shallow ground changes, bounded
                // to 0..170 origins for a 22-row picture on 192 screen rows.
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
                // Highest supported AND clear-body height wins. A clear
                // old-Y position without support is a ledge exit, not a wall.
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
        // Signed velocity measures pixels/update: negative rises, positive
        // falls. Downward acceleration adds one, descent caps at +4. Probe each
        // intervening pixel so thin ceilings/platforms cannot be skipped.
        // Upward hits stop speed but do not grant Grounded; descent tests feet
        // support after each accepted row. Y=48/170 are origin limits, not floors.
        // Return coordinate movement to Main's redraw decision.
        bool moved = false;
        Sam.MapMemoryPage(12);
        if (VerticalVelocity < 0)
        {
            // Acceleration precedes ascent: -1 becomes zero without moving;
            // the next update uses the non-rising support/descent branch.
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
                        // Keep the last safe Y and cancel upward budget/speed.
                        // A ceiling hit cannot make the feet grounded.
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
                // Remaining is a temporary step budget. Landing cancels it
                // and the stored velocity immediately, without overshooting.
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
        // Body is X+8..X+17, Y+4..Y+20. ANY solid pixel on next row Y+21
        // supports its feet; all ten need not touch. Caller mapped bank 12.
        return SolidRow((byte)(y + 21), (byte)(x + 8));
    }

    static bool BoxClear(byte x, byte y)
    {
        // Scan ALL ten inset-body pixels on every body row, excluding artwork
        // margins. Corner-only tests would miss middle protrusions.
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
        // "Nibble" means four bits: one packed mask pixel. Parity chooses high/
        // even or low/odd nibble; row offset advances 128 bytes like the screen.
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
        // Reset spawn/velocity and choose matching scene/mask, but preserve
        // inventory and world-item arrays. Those belong to the persistent world,
        // not to the single decompressed active-room background in bank 6.
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
        // Repeat-count/value runs encode the mask compactly. Expand only the
        // selected room to working bank 12; scene colours are not solidity rules.
        byte[] runs = Room1CollisionRunsData.Room1Runs;
        if (room == 2)
            runs = Room2CollisionRunsData.Room2Runs;
        // The stream stores little-endian 16-bit run lengths and packed mask
        // bytes. The SDK kernel expands it into the single working mask bank.
        Sam.ExpandByteRuns(runs, 8, TutorialCollision.CollisionMask, 12);
    }

    static void BuildRoom(byte room)
    {
        // Decode compact RLE (run-length encoding) into a fresh 24 KB scene,
        // then overlay current items/HUD. Starting from scenery removes prior
        // world copies of collected items and old slot icons without manual erases.
        byte[] encoded = Room1BackgroundData.Room1Background;
        if (room == 2)
            encoded = Room2BackgroundData.Room2Background;
        // Decode one exact 24 KB MODE 4 image into page 6; the screen copier
        // then transfers rows to the hidden display page.
        Sam.DecodeRleBackground(encoded, 8, TutorialRooms.Background, 6);
        BuildItemsAndHud();
    }

    static void BuildItemsAndHud()
    {
        // World offsets already use byte ItemX, unlike pixel PlayerX: do NOT
        // divide ItemX by two again. Same item ID indexes all dimension arrays.
        // HUD means heads-up display, the on-screen carried-item slot frames.
        // Draw only records whose room matches the active scene; carried
        // identifiers (255) have no world copy.
        byte item = 0;
        while (item < 4)
        {
            if (ItemRooms[item] == CurrentRoom)
            {
                ushort worldOffset = (ushort)(ItemY[item] * 128 + ItemX[item]);
                ComposeItem(item, worldOffset);
            }
            item++;
        }

        // The HUD is part of the background, so restoration and page flips
        // treat it exactly like the scenery. Empty slots still get a frame.
        // slot is 0..2; slotX is a packed-byte column. Outline width 24 is
        // bytes, height 32 rows. 0x77 paints both pixel halves, 0x70/0x07 one
        // edge half each. Empty slots retain their frame without an item icon.
        byte slot = 0;
        byte slotX = 16;
        while (slot < 3)
        {
            Sam.DrawPackedOutline(
                TutorialRooms.Background,
                6,
                (ushort)(8 * 128 + slotX),
                24,
                32,
                0x77,
                0x70,
                0x07);
            byte itemId = InventorySlots[slot];
            if (itemId != 255)
                ComposeItem(itemId, (ushort)(15 * 128 + slotX + 6));
            slot++;
            slotX = (byte)(slotX + 36);
        }
    }

    static void ComposeItem(byte item, ushort destination)
    {
        // destination is a scene byte offset, usable for either world or HUD.
        // Source widths/stride are packed bytes; height is rows. Here stride
        // equals width, so source rows are contiguous; destination rows remain
        // 128 bytes apart. Transparent colour-zero nibbles preserve scenery.
        // The SDK stages between source bank 8 and destination bank 6.
        // The extracted item rows share one banked table. Select only the
        // payload offset here; dimensions remain explicit per item record.
        // The extracted body offsets are chest 0, coin 160, axe 347, scroll
        // 507; each points past the source's dimensions header.
        ushort sourceOffset = 0;
        if (item == 1)
            sourceOffset = 160;
        else if (item == 2)
            sourceOffset = 347;
        else if (item == 3)
            sourceOffset = 507;

        Sam.ComposeTransparentImage(
            TutorialItemAssets.Pixels,
            8,
            TutorialRooms.Background,
            6,
            sourceOffset,
            destination,
            ItemWidths[item],
            ItemHeights[item],
            ItemWidths[item]);
    }

    static bool TryInventoryAction()
    {
        // Consume a new Enter edge even on rejection; require support and no
        // direction held. Search pickups FIRST, so Enter beside an item never
        // drops the carried top item instead, even if the inventory is full.
        bool enterDown = Sam.IsKeyPressed(0xBFFE, 0x01);
        bool freshPress = enterDown && !EnterWasDown;
        // Consume every press, even if the player is airborne or too far away.
        EnterWasDown = enterDown;
        if (!freshPress || !Grounded)
            return false;
        if (Sam.IsKeyPressed(0xFEFE, 0x02) || Sam.IsKeyPressed(0xFEFE, 0x04))
            return false;

        byte item = 0;
        while (item < 4)
        {
            if (ItemRooms[item] == CurrentRoom && ItemOverlaps(item))
            {
                // A full stack rejects pickup; it never silently loses its oldest entry.
                if (InventoryCount == 3)
                {
                    ActionFlash = 20;
                    return false;
                }

                // Push means append at count, then increase count. Mark the
                // world record carried (255) so composition removes its world copy.
                InventorySlots[InventoryCount] = item;
                InventoryCount++;
                ItemRooms[item] = 255;
                return true;
            }
            item++;
        }

        // Only a clear position with no nearby item gives Enter its pop action.
        if (InventoryCount == 0)
        {
            ActionFlash = 20;
            return false;
        }
        // LIFO = last in, first out. Only the newest occupied slot may drop;
        // test count before subtraction so an empty byte count cannot wrap.
        byte top = (byte)(InventoryCount - 1);
        item = InventorySlots[top];
        return TryDropItem(item, top);
    }

    static bool ItemOverlaps(byte item)
    {
        // Convert packed-byte item X/width to PIXELS before comparing with the
        // inset player's pixel bounds. Inclusive right/bottom subtract one.
        // Reject separation on either axis; otherwise rectangles overlap,
        // including touching at a shared pixel, not merely near each other.
        // The world records stay inside the 256-pixel room. Store the player's
        // and item's inclusive right/bottom pixels as bytes, avoiding general
        // 32-bit rectangle math on this repeatedly tested interaction path.
        byte itemLeft = (byte)(ItemX[item] * 2);
        byte itemRight = (byte)((ItemX[item] + ItemWidths[item]) * 2 - 1);
        byte itemTop = ItemY[item];
        byte itemBottom = (byte)(itemTop + ItemHeights[item] - 1);
        byte playerLeft = (byte)(PlayerX + 8);
        byte playerRight = (byte)(PlayerX + 17);
        byte playerTop = (byte)(PlayerY + 4);
        byte playerBottom = (byte)(PlayerY + 20);

        if (playerRight < itemLeft)
            return false;
        if (playerLeft > itemRight)
            return false;
        if (playerBottom < itemTop)
            return false;
        if (playerTop > itemBottom)
            return false;
        return true;
    }

    static bool TryDropItem(byte item, byte slot)
    {
        // Search a bounded set of candidate rows without altering live records.
        // Atomic here means all-or-nothing state change: do not pop the stack
        // until one candidate passes bounds, empty rectangle and foot support.
        // If all fail, preserve inventory/world coordinates and flash feedback.
        // Align the item to a packed-byte X near the player's centre. Search
        // only seven nearby rows, never an arbitrary distant landing point.
        byte dropX = (byte)((PlayerX + 3) / 2);
        byte itemHeight = ItemHeights[item];
        byte firstY = (byte)(PlayerY + 18 - itemHeight);
        if (firstY < 48)
            firstY = 48;

        byte attempt = 0;
        byte candidateY = firstY;
        // Leave row y+height in range for the support probe, as well as the
        // image itself. This is why the limit uses 191-height, not 192-height.
        byte lastDropY = (byte)(191 - itemHeight);
        while (attempt < 7)
        {
            if (candidateY <= lastDropY &&
                DropFits(item, dropX, candidateY))
            {
                // Commit world location first; pop only after all bounds,
                // empty-rectangle and support tests have succeeded.
                ItemX[item] = dropX;
                ItemY[item] = candidateY;
                ItemRooms[item] = CurrentRoom;
                InventorySlots[slot] = 255;
                InventoryCount--;
                return true;
            }
            candidateY++;
            attempt++;
        }

        ActionFlash = 20;
        return false;
    }

    static bool DropFits(byte item, byte xByte, byte y)
    {
        // xByte is already a packed-byte column, so first pixel parity is zero.
        // Scan every pixel in every image row against static terrain; after
        // height rows, offset points at the support row directly underneath.
        // ANY solid pixel there supports it. Other item overlap is not tested
        // here, and ordinary carried/dropped items do not change the terrain mask.
        byte height = ItemHeights[item];
        byte pixelWidth = ItemPixelWidths[item];
        // Advance one 128-byte screen row at a time instead of recomputing
        // (y + row) * 128 inside the bounded rectangle scan.
        ushort offset = (ushort)(y * 128 + xByte);
        Sam.MapMemoryPage(12);
        byte row = 0;
        while (row < height)
        {
            if (Sam.AnySolidPixelInPackedRow(
                    TutorialCollision.CollisionMask,
                    offset,
                    0,
                    pixelWidth))
            {
                Sam.RestoreMemoryPage();
                return false;
            }
            offset += 128;
            row++;
        }
        bool supported = Sam.AnySolidPixelInPackedRow(
            TutorialCollision.CollisionMask,
            offset,
            0,
            pixelWidth);
        Sam.RestoreMemoryPage();
        return supported;
    }

    static void RefreshRoom()
    {
        // Authoritative means built from current records, not patched visible
        // pixels. Two copies/two flips also leave the expected buffer order
        // unchanged; later old-player restoration reads this refreshed scene.
        // Recompose authoritative world and HUD once, then initialize each
        // display page from that finished image before drawing the player again.
        BuildRoom(CurrentRoom);
        CopyBackground();
        Sam.FlipScreen();
        CopyBackground();
        Sam.FlipScreen();
    }

    static void CopyBackground()
    {
        // Copy 192 rows of 128 BYTES = 24 KB, with both starting offsets zero.
        // Resident staging avoids aliasing banks (mistaking one mapped window
        // for two simultaneously accessible source/destination banks).
        // The source is banked scenery; the typed kernel stages rows before
        // paging the hidden screen, avoiding an alias between the two pages.
        Sam.CopyBankedBytesToScreen(TutorialRooms.Background, 6, 0, 0, 128, 192);
    }

    static void RestoreBackground(byte x, byte y, byte width)
    {
        // Restore OLD coordinates/width for this page: 22 rows, 13/14 bytes
        // depending on old X parity. Both strides remain 128, not the cropped
        // width; clean background contains current world items and HUD icons.
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
        // One header byte then 572-byte frame bodies: 13 columns * 22 rows *
        // two interleaved mask/pixel bytes. Preserve the resident header, stage
        // only the body, restore source bank 4 before screen access or ROM waits.
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
