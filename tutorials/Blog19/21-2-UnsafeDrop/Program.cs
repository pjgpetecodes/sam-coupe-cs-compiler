using SamCoupe.Sdk;

// This Blog19 variant runs the SAME inventory/drop rules as 21-1, but starts
// in room 2 carrying axe ID 2. Try Enter to inspect unsafe-drop rejection:
// when no candidate succeeds, carried/world state must remain intact.
static class Program
{
    // Player origins are pixels. byte holds bounded IDs/counts; signed sbyte
    // velocity is negative UP, positive DOWN. Grounded means feet support,
    // while Space/Enter latches remember prior keyboard presses.
    // One room id selects both the scenery and its matching collision plane.
    static byte CurrentRoom = 2;
    static byte PlayerX = 34;
    static byte PlayerY = 87;
    static bool Grounded = true;
    static sbyte VerticalVelocity = 0;
    static bool SpaceWasDown = false;
    static bool EnterWasDown = false;
    static byte InventoryCount = 1;
    static byte ActionFlash = 0;

    // World records stay separate from the carried stack. Room 255 means an
    // item is in inventory; the three slot identifiers are ordered oldest-first.
    // Parallel arrays describe item IDs chest 0, coin 1, axe 2, scroll 3.
    // ItemX/ItemWidths are packed BYTES (two pixels each); Y/heights are rows.
    // ItemPixelWidths is for collision scanning. readonly fixes references,
    // not mutable elements. 255 is a sentinel meaning carried/empty, not a room.
    // Initially slot 0 contains axe 2, and ItemRooms[2]=255 hides its world copy.
    static readonly byte[] ItemX = new byte[] { 24, 40, 60, 80 };
    static readonly byte[] ItemY = new byte[] { 111, 110, 111, 110 };
    static readonly byte[] ItemRooms = new byte[] { 1, 1, 255, 1 };
    static readonly byte[] ItemWidths = new byte[] { 10, 11, 10, 10 };
    static readonly byte[] ItemPixelWidths = new byte[] { 20, 22, 20, 20 };
    static readonly byte[] ItemHeights = new byte[] { 16, 17, 16, 16 };
    static readonly byte[] InventorySlots = new byte[] { 2, 255, 255 };

    static void Main()
    {
        // Select matching room 2 scenery/mask before BOTH screen copies.
        // Screens start at physical banks 10/14; scene is bank 6, mask bank 12.
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

        // animation/frame select pictures; delay is the five-wait image clock.
        // redraw/frameChanged separate repainting from reloading image bytes.
        byte frame = 0;
        byte animation = 0;
        byte movePhase = 0;
        byte delay = 5;
        // Each buffer holds its own older sprite; remember OLD X/Y separately
        // rather than erase the hidden page using the visible player's position.
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
            // A fresh press edge, not held Space, launches only with support.
            // Set -10 before this update's gravity reduces it to -9. Remember
            // every key state so landing cannot auto-repeat a held press.
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
            // Alternate 1/3 PIXEL steps, averaging two per update. Both change
            // parity; odd X still needs shifted art, not byte-only positioning.
            // Clamp 0..230 to avoid unsigned wrap and keep the picture screen-safe.
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

            // Order: horizontal collision, room replacement, gravity, inventory
            // action. The drop sees final coordinates and the current room mask.
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
            // Both screens changed rooms: reset BOTH old-position histories
            // plus animation. Old-room footprint coordinates are no longer valid.
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
            // Only successful state changes rebuild the scene/HUD and both
            // buffers. A rejected drop leaves the axe carried and the world intact.
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

            // Border 5 gives a counted failure flash; otherwise 7/3 reports
            // grounded/airborne. Feedback changes no movement or inventory state.
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

            // Ground input picks idle/walks; preserve a just-started jump's art.
            // Sequence completion never lands the player or cancels velocity.
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

            // Images advance every five waits: idle/walks loop, jumps finish
            // at idle. Movement and collisions still execute every update.
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

            // Translate sequence frames, stage a changed image and make an
            // odd-X copy. Two four-bit colours pack into each byte; the shift
            // needs 14 packed bytes instead of an even-X image's 13.
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

            // Restore THIS hidden page's old rectangle, draw its new player,
            // flip only the finished screen, record its X/Y and switch pages.
            // Restoring afterwards could erase freshly drawn overlapping pixels.
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

                // Convert PIXEL origin to byte offset: Y*128 is a row start,
                // X/2 a packed column. ushort is a screen-relative 16-bit offset,
                // not an absolute RAM pointer into a currently selected bank.
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

            // Helpers restore memory mapping before the ROM-serviced wait.
            // This image timer is independent of inventory failure feedback.
            Sam.WaitForFrames(1);
            if (delay != 0)
                delay--;
        }
    }

    static void ConstrainGroundedX(byte desiredX)
    {
        // Accept only clear intermediate one-pixel steps, preventing tunnelling
        // across a thin obstacle. Air motion tests fixed Y; ground motion may
        // follow supported clear slope positions within +/-3 rows. Nested
        // helpers rely on collision bank 12 staying mapped for their probes.
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
                // Follow only small slopes: +/-3 rows, clamped to 0..170
                // picture origins so all 22 rows stay within a 192-row screen.
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
                // Highest supported AND body-clear candidate wins. Without
                // support, a clear old-Y sideways step exits a ledge and falls
                // later; blocked body space stops the horizontal request.
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
        // Signed pixels/update velocity increases toward DOWN by one, with
        // descent capped at +4. Test every rising/falling pixel, not only its
        // final destination, so thin platforms/ceilings cannot be skipped.
        // A ceiling cancels ascent without Grounded; falling checks feet after
        // each accepted row. Y=48/170 are screen bounds, not solid floors.
        // bool moved reports actual coordinate change for redraw.
        bool moved = false;
        Sam.MapMemoryPage(12);
        if (VerticalVelocity < 0)
        {
            // Acceleration comes first. -1 becomes zero without an upward
            // step; support/descent is handled in the following update.
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
                        // Never accept blocked candidateY. Stop at last-clear
                        // Y without declaring Grounded from ceiling contact.
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
                // Temporary downward step budget, separate from velocity.
                // Landing cancels both so no remaining step overshoots ground.
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
        // Body bounds: X+8..X+17, Y+4..Y+20. ANY solid pixel in the ten-pixel
        // foot row Y+21 suffices; body clearance is a different condition.
        return SolidRow((byte)(y + 21), (byte)(x + 8));
    }

    static bool BoxClear(byte x, byte y)
    {
        // Test every body pixel on every body row, not just corners; exclude
        // picture margins. The collision bank is already mapped by the caller.
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
        // A nibble is four bits, one packed mask pixel. Parity starts high/even
        // or low/odd; scan exactly ten pixels at this 128-byte-row-stride offset.
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
        // Reset spawn/velocity and replace scene plus matching collision, but
        // preserve inventory/world arrays and key latches across room changes.
        // Only the single working bitmap/mask is temporary room state.
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
        // Repeat count/value runs store masks compactly. Expand the selected
        // room into bank 12; painted scenery colours are not collision solidity.
        byte[] runs = Room1CollisionRunsData.Room1Runs;
        if (room == 2)
            runs = Room2CollisionRunsData.Room2Runs;
        // The stream stores little-endian 16-bit run lengths and packed mask
        // bytes. The SDK kernel expands it into the single working mask bank.
        Sam.ExpandByteRuns(runs, 8, TutorialCollision.CollisionMask, 12);
    }

    static void BuildRoom(byte room)
    {
        // Decode fresh RLE (run-length encoded) scenery before adding live
        // items and HUD. Rebuilding from records removes obsolete world items
        // and slot icons without individually patching either display buffer.
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
        // ItemX is ALREADY a packed-byte column, unlike pixel PlayerX. World
        // and HUD use the same composition helper. HUD means heads-up display:
        // the three on-screen inventory slots, with icons only for carried IDs.
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
        // slotX is bytes; outlines are 24 bytes wide, 32 rows high. 0x77 paints
        // both packed halves while 0x70/0x07 paint their individual edge halves.
        // Empty slot sentinel 255 leaves a visible frame with no icon.
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
        // destination is a scene byte offset for either world or icon. Width/
        // stride are source packed bytes, height is rows; stride equals width
        // here while destination rows advance 128 bytes. Colour-zero nibbles
        // are transparent, preserving scenery. SDK staging bridges banks 8/6.
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
        // Consume each Enter edge once even if rejected. Require support and
        // no directional input. Pickup search has priority: a nearby item,
        // including one rejected for full capacity, never becomes a drop request.
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

                // Push an ID at count, advance count, mark its world room 255.
                // This moves ownership, not the image bytes or item identity.
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
        // LIFO = last in, first out: newest occupied slot is the only pop/drop.
        // Empty-count guard above prevents unsigned subtraction wrapping.
        byte top = (byte)(InventoryCount - 1);
        item = InventorySlots[top];
        return TryDropItem(item, top);
    }

    static bool ItemOverlaps(byte item)
    {
        // Compare like units: convert item byte X/width to inclusive pixel
        // bounds, then compare with the inset player's bounds. Subtract one
        // for inclusive right/bottom; reject any separation on either axis.
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
        // This method demonstrates atomic = all-or-nothing state updates.
        // Search up to seven nearby rows WITHOUT changing world/stack records.
        // Commit coordinates/room and pop only after a safe candidate passes.
        // Failure keeps the carried axe and count unchanged, flashing the border.
        // Align the item to a packed-byte X near the player's centre. Search
        // only seven nearby rows, never an arbitrary distant landing point.
        byte dropX = (byte)((PlayerX + 3) / 2);
        byte itemHeight = ItemHeights[item];
        byte firstY = (byte)(PlayerY + 18 - itemHeight);
        if (firstY < 48)
            firstY = 48;

        byte attempt = 0;
        byte candidateY = firstY;
        // Reserve the support row y+height within 0..191 too, not just image
        // rows. Hence 191-height, one row less than a drawing-only limit.
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
        // xByte starts on a packed-byte boundary (parity zero). Scan EVERY
        // pixel of EVERY image row for terrain overlap. After height rows,
        // offset reaches the next row below: ANY solid pixel there supports it.
        // The test does not check other item overlap or modify static solidity.
        // Always restore bank 12 even on an early rejection.
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
        // Rebuild from authoritative world/slot records, copy both screens,
        // flip twice. This leaves expected buffer order unchanged and makes
        // future old-player restoration use the current item/HUD appearance.
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
        // 128 BYTES * 192 ROWS = the complete 24 KB scene. SDK resident staging
        // prevents aliasing the source bank with the mapped hidden-screen window.
        // The source is banked scenery; the typed kernel stages rows before
        // paging the hidden screen, avoiding an alias between the two pages.
        Sam.CopyBankedBytesToScreen(TutorialRooms.Background, 6, 0, 0, 128, 192);
    }

    static void RestoreBackground(byte x, byte y, byte width)
    {
        // Restore the OLD 22-row player footprint on this page using its old
        // parity width 13/14 bytes. Both source/destination strides stay 128
        // despite the cropped width. Background includes current items/HUD.
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
        // Header byte then 572-byte bodies: 13 columns * 22 rows * mask/pixel
        // pairs. Preserve resident header, stage body and restore bank 4 before
        // drawing or waits that need the normally accessible ROM interrupt.
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
