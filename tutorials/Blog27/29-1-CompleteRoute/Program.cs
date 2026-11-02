using SamCoupe.Sdk;

// Blog27 assembles the finished four-room engine without adding a new rule.
// It intentionally preserves Blog26's gameplay and native binary: movement,
// chest/inventory puzzle, animated water, bee contact and one-life restart.
static class Program
{
    // byte holds bounded IDs/counts and PIXEL player origins. sbyte is signed:
    // negative velocity goes UP (smaller Y), positive DOWN. Grounded describes
    // support, not velocity. Space/Enter bool latches remember earlier keys.
    // ChestPlaced persists after the puzzle drop; ReturnX remembers pixel X
    // for coming back from the upper room without replacing inventory state.
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
    static bool ChestPlaced = false;
    static byte ReturnX = 60;
    // Zero means alive; otherwise the number of 50 Hz updates left in the
    // visible death pause. These values are captured before play begins.
    static byte DeathTimer = 0;
    static byte InitialRoom;
    static byte InitialReturnX;
    static readonly byte[] InitialItemX = new byte[] { 0, 0, 0 };
    static readonly byte[] InitialItemY = new byte[] { 0, 0, 0 };
    static readonly byte[] InitialItemRooms = new byte[] { 0, 0, 0 };
    static readonly byte[] InitialInventorySlots = new byte[] { 0, 0, 0 };
    static byte InitialInventoryCount;
    static bool InitialChestPlaced;
    // The sea has four authored pictures. One game update is one 50 Hz
    // interrupt, so a three-tick timer advances waves at the original cadence.
    static byte WaterFrame = 0;
    static byte WaterTimer = 3;
    // The bee uses BYTE columns because its 22 pixels occupy 11 whole screen
    // bytes. Its timer and flap state are independent of Dizzy's animation.
    static byte BeeX = 77;
    static byte BeeY = 100;
    static sbyte BeeVelocity = 1;
    static byte BeeFlap = 0;
    static byte BeeFrame = 0;
    static byte BeeTimer = 3;
    // Each physical screen page remembers its own last bee column; 255 means
    // that page has not drawn a bee since its room background was refreshed.
    static byte BeePreviousX10 = 255;
    static byte BeePreviousX14 = 255;
    // Hazard overlap is recomputed every update; the previous result is kept
    // only so leaving danger restores the correct grounded/airborne border.
    static bool HazardWasContact = false;

    // World records stay separate from the carried stack. Room 255 means an
    // item is in inventory; the three slot identifiers are ordered oldest-first.
    // Parallel arrays describe IDs 0 (chest), 1/2 (two records sharing coin art).
    // ItemX/ItemWidths use packed BYTES, ItemY/heights use screen ROWS.
    // ItemPixelWidths is for terrain scans. readonly fixes each array reference,
    // not its elements. 255 means carried/empty; 254 marks the specially placed
    // chest, which is drawn explicitly in room 2 instead of as an ordinary item.
    static readonly byte[] ItemX = new byte[] { 24, 108, 49 };
    static readonly byte[] ItemY = new byte[] { 111, 148, 134 };
    // The second coin belongs to room 4, reachable through the lower route.
    static readonly byte[] ItemRooms = new byte[] { 1, 1, 4 };
    static readonly byte[] ItemWidths = new byte[] { 10, 11, 11 };
    static readonly byte[] ItemPixelWidths = new byte[] { 20, 22, 22 };
    static readonly byte[] ItemHeights = new byte[] { 16, 17, 17 };
    static readonly byte[] InventorySlots = new byte[] { 255, 255, 255 };

    static void Main()
    {
        // Copy initial ownership before play changes it. Both sea and bee
        // deaths restore this same snapshot through the existing restart path.
        SaveInitialState();
        // Screens start at physical banks 10/14; the clean 24 KB scene is bank
        // 6 and its packed terrain mask bank 12. Select matching room sources,
        // then initialize BOTH screens so neither retains earlier scenery.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.SetBorderColor(7);
        Sam.BeginDoubleBuffer(10, 14);
        // Keep one decompressed background and collision plane in working
        // pages; the three lesson rooms' compact source art stays banked.
        LoadCollision(CurrentRoom);
        BuildRoom(CurrentRoom);
        CopyBackground();
        Sam.FlipScreen();
        // Water graphics live in bank 8. Copy the first mask/pixel image into
        // resident staging arrays before the first hidden-page draw.
        LoadWaterFrame(WaterFrame);
        CopyBackground();
        Sam.FlipScreen();

        // animation/frame select pictures; delay times five waits between them.
        // redraw flags repaint, frameChanged flags reloading/reshifting pixels.
        // These are separate from physical velocity and item ownership.
        byte frame = 0;
        byte animation = 0;
        byte movePhase = 0;
        byte delay = 5;
        // Each buffer keeps its own OLD sprite coordinates. The hidden page
        // can contain a player image older than the currently visible one.
        // drawingPage14 selects which history to restore on the next redraw.
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
            bool dyingAtStart = DeathTimer != 0;
            // Count down once per 50 Hz update, not once per animation picture.
            // When the timer expires, stage the next of four frames and request
            // a new hidden-page image even if Dizzy and the room have not moved.
            if (WaterTimer != 0)
                WaterTimer--;
            if (WaterTimer == 0)
            {
                WaterTimer = 3;
                WaterFrame = (byte)((WaterFrame + 1) & 3);
                LoadWaterFrame(WaterFrame);
                redraw = true;
            }
            // Advance the independent actor before death handling: the bee
            // keeps moving during the pause, on its own three-tick schedule.
            if (AdvanceBee())
                redraw = true;
            // While dying, input and physics stop, but the normal redraw path
            // below still restores and animates the sea on alternating pages.
            if (dyingAtStart)
            {
                DeathTimer--;
                if (DeathTimer == 0)
                {
                    RestoreInitialState();
                    EnterRoom(InitialRoom, 120, 106);
                    SpaceWasDown = false;
                    EnterWasDown = false;
                    HazardWasContact = false;
                    animation = 0;
                    frame = 0;
                    delay = 5;
                    previousX10 = PlayerX;
                    previousY10 = PlayerY;
                    previousX14 = PlayerX;
                    previousY14 = PlayerY;
                    redraw = true;
                    frameChanged = true;
                    moved = true;
                    Sam.SetBorderColor(7);
                    displayedGrounded = 1;
                }
                else if ((DeathTimer & 8) != 0)
                    Sam.SetBorderColor(4);
                else
                    Sam.SetBorderColor(0);
            }
            bool leftDown = false;
            bool rightDown = false;
            if (!dyingAtStart)
            {
                leftDown = Sam.IsKeyPressed(0xFEFE, 0x02);
                rightDown = Sam.IsKeyPressed(0xFEFE, 0x04);
            }
            // Keyboard matrix bits are active-low; remember Space so a held
            // key cannot start another jump after landing.
            bool spaceDown = false;
            if (!dyingAtStart)
                spaceDown = Sam.IsKeyPressed(0x7FFE, 0x01);
            // A fresh "edge" is down now but up on the previous update.
            // Grounded-only edges avoid mid-air relaunch and held-key bouncing.
            // Initial velocity -10 is reduced to -9 by this update's gravity.
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
            // Alternate 1/3 PIXELS to average two per update. Both change X
            // parity, so odd positions still use shifted art, not byte-only
            // movement. Clamp to 0..230 before unsigned coordinates can wrap.
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

            // Order matters: horizontal movement, grounded side exits, gravity
            // (including the vertical link), then Enter's inventory action.
            // Each physics/interaction probe uses current room/coordinates.
            bool enteredRoom = false;
            // Exits are explicit links, and only grounded movement can use them.
            // Screens 3 and 4 are true left/right neighbours; do not infer a
            // route from room numbers because screen 2's cliff stays solid.
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
            else if (Grounded && leftDown && PlayerX == 0 && CurrentRoom == 3)
            {
                // Room 4 has two real supports at its right edge. Carry the
                // player's height class across instead of dropping a cloud
                // traveller onto the grass or lifting a ground traveller.
                byte entryY = 62;
                if (PlayerY >= 100)
                    entryY = 152;
                EnterRoom(4, 230, entryY);
                enteredRoom = true;
            }
            else if (Grounded && rightDown && PlayerX == 230 && CurrentRoom == 4)
            {
                // The return uses the matching upper cloud or lower rock on
                // room 3's left edge; the bridge remains outside this route.
                byte entryY = 63;
                if (PlayerY >= 100)
                    entryY = 155;
                EnterRoom(3, 0, entryY);
                enteredRoom = true;
            }
            // Side exits replaced both screens, so reset BOTH old-position
            // histories and restart pictures at the destination spawn.
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
            byte roomBeforeGravity = CurrentRoom;
            if (!dyingAtStart && ApplyGravity())
                moved = true;
            // A vertical link replaces both display pages inside ApplyGravity.
            // Reset their saved rectangles after that room change and force a
            // draw even when a boundary return changes room without changing Y.
            // Keep the existing animation frame and preserved jump/fall speed.
            if (CurrentRoom != roomBeforeGravity)
            {
                previousX10 = PlayerX;
                previousY10 = PlayerY;
                previousX14 = PlayerX;
                previousY14 = PlayerY;
                redraw = true;
                moved = true;
            }
            // Hazards are deliberately not part of the solid terrain mask:
            // Dizzy must be able to enter the sea before contact is detected.
            // Test the settled body position once per update after gravity.
            bool hazardContact = false;
            if (!dyingAtStart)
                hazardContact = CheckHazards();
            if (hazardContact && !dyingAtStart && DeathTimer == 0)
            {
                // Detection and consequence remain separate: Blog23 only
                // painted red; this lesson starts a timed one-life reset.
                DeathTimer = 100;
                animation = 0;
                frame = 0;
                delay = 5;
                redraw = true;
                frameChanged = true;
            }
            // A successful action changes world records/HUD. Recompose the
            // authoritative background and both screens before drawing again.
            // Failure leaves records untouched and instead flashes feedback.
            if (!dyingAtStart && TryInventoryAction())
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

            // Hazard red has priority over ordinary support colours. Remember
            // the prior contact so leaving the rectangle restores the current
            // support colour even when Grounded itself did not change.
            if (dyingAtStart && DeathTimer != 0)
            {
                // Keep the flashing red/black death feedback above; no support
                // color is allowed to hide it during the pause.
            }
            else if (hazardContact)
            {
                Sam.SetBorderColor(4);
                HazardWasContact = true;
                displayedGrounded = 0;
                if (Grounded)
                    displayedGrounded = 1;
            }
            else if (HazardWasContact)
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
                HazardWasContact = false;
            }
            // Failure flashes border 5 for counted updates; otherwise 7/3
            // reports support. Feedback never changes velocity or hazard state.
            else if (ActionFlash != 0)
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

            // Ground input picks idle/walks; preserve a just-started jump's
            // 4/5 pictures. Art follows physics; sequence completion cannot land us.
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

            // Idle/walk sequences loop, jumps finish at idle on their image
            // timer. Physical motion and item checks execute every update.
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

            // Translate sequence-local frame indices into the shared table.
            // Stage one image and make its odd-X copy only when art changes.
            // Two four-bit colours share a byte; a one-pixel shift spans 14
            // packed bytes rather than the even-X image's 13.
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

            // Restore THIS hidden page's old rectangle, draw current player
            // pixels, flip only the finished image, then record its X/Y and
            // switch pages. Clean background includes current items and HUD.
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
                // Clear every old wave strip from the clean, water-free room
                // before drawing the player and current water frame again.
                RestoreWater();
                // Restore both moving actors before drawing either one. That
                // way overlapping old rectangles cannot erase a newly drawn
                // sprite on the same hidden page.
                RestoreBee(drawingPage14);
                DrawBee(drawingPage14);

                // Convert PIXEL origin to byte offset: 128-byte row stride plus
                // X/2. ushort is unsigned 16-bit, screen-relative, not a raw RAM
                // pointer. X parity chooses even/odd image alignment separately.
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

                // Waves are drawn after Dizzy, so opaque crest pixels pass in
                // front while transparent nibbles preserve his colours.
                DrawWater();
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

            // All mapping helpers restore access before this ROM-serviced
            // frame wait. Image timing is separate from physics step budgets.
            Sam.WaitForFrames(1);
            if (delay != 0)
                delay--;
        }
    }

    static void SaveInitialState()
    {
        // Keep only the persistent world state in this snapshot: room links,
        // chest puzzle state, and the world/inventory records. Render buffers,
        // collision workspace and animation timers are rebuilt or harmless.
        InitialRoom = CurrentRoom;
        InitialReturnX = ReturnX;
        InitialInventoryCount = InventoryCount;
        InitialChestPlaced = ChestPlaced;
        byte item = 0;
        while (item < 3)
        {
            InitialItemX[item] = ItemX[item];
            InitialItemY[item] = ItemY[item];
            InitialItemRooms[item] = ItemRooms[item];
            InitialInventorySlots[item] = InventorySlots[item];
            item++;
        }
    }

    static void RestoreInitialState()
    {
        // Copy each saved record back before EnterRoom rebuilds art and the
        // terrain mask. The loop bound is three because there are three world
        // items and three LIFO inventory slots in this tutorial version.
        CurrentRoom = InitialRoom;
        ReturnX = InitialReturnX;
        InventoryCount = InitialInventoryCount;
        ChestPlaced = InitialChestPlaced;
        byte item = 0;
        while (item < 3)
        {
            ItemX[item] = InitialItemX[item];
            ItemY[item] = InitialItemY[item];
            ItemRooms[item] = InitialItemRooms[item];
            InventorySlots[item] = InitialInventorySlots[item];
            item++;
        }
    }

    static void ConstrainGroundedX(byte desiredX)
    {
        // This method stays in the resident window because it maps collision
        // data into the lower window; banked code would disappear while that
        // data page is selected. Keep only accepted safe origins.
        // Probe one horizontal pixel at a time to prevent "tunnelling" through
        // thin solids between the start and requested final position.
        byte acceptedX = PlayerX;
        byte acceptedY = PlayerY;
        bool grounded = Grounded;
        bool movingRight = desiredX > PlayerX;
        bool blocked = false;
        // The active room's packed solidity nibbles occupy one reusable bank.
        Sam.MapMemoryPage(12);

        // Air motion keeps Y for body checks. Ground motion can search nearby
        // supported heights instead; snapping in the air would disrupt a jump.
        // Nested collision helpers assume terrain bank 12 remains selected.
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

                // Ground following searches +/-3 rows, clamped to 0..170 so
                // all 22 picture rows stay on a 192-row screen.
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

                // Highest supported AND body-clear position wins. Without
                // nearby support, a clear old-Y sideways step can exit a ledge;
                // gravity later in the update supplies the falling motion.
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
        // Signed pixels-per-update velocity increases toward DOWN by one.
        // Split movement into one-pixel steps to avoid skipping thin platforms
        // or ceilings. Support cancels falling; ascent hits cancel speed without
        // granting Grounded. The room-2 top link adds a special boundary case.
        bool moved = false;
        Sam.MapMemoryPage(12);
        if (VerticalVelocity < 0)
        {
            // Acceleration is applied before upward steps. If -1 becomes zero,
            // no ascent occurs; the next update checks non-rising support.
            VerticalVelocity++;
            if (VerticalVelocity < 0)
            {
                // Negate still-negative speed for a positive temporary budget.
                // Remaining steps are consumed one at a time, not added to Y
                // in one jump that might tunnel across solid scenery.
                byte remaining = (byte)(-VerticalVelocity);
                while (remaining != 0)
                {
                    // At this upper-origin boundary, only the placed-chest
                    // puzzle in room 2 and X<48 unlock entry to room 3.
                    // Otherwise cancel ascent as at the earlier demo boundary.
                    if (PlayerY == 48)
                    {
                        if (CurrentRoom == 2 && ChestPlaced && PlayerX < 48)
                        {
                            // EnterRoom resets velocity/support by default.
                            // Save signed ascent, translate Y by +100 for the
                            // destination coordinate system, replace the room,
                            // restore ascent and continue its unused step budget.
                            // Restore terrain mapping before the room helpers,
                            // then remap the NEW mask for subsequent body checks.
                            sbyte jumpVelocity = VerticalVelocity;
                            byte enteringY = (byte)(PlayerY + 100);
                            byte enteringX = PlayerX;
                            Sam.RestoreMemoryPage();
                            EnterRoom(3, enteringX, enteringY);
                            VerticalVelocity = jumpVelocity;
                            Grounded = false;
                            Sam.MapMemoryPage(12);
                            moved = true;
                        }
                        else
                        {
                            VerticalVelocity = 0;
                            remaining = 0;
                        }
                    }

                    if (remaining == 0)
                        break;
                    // Test the next upward body position before committing Y.
                    // A blocked candidate leaves the last safe origin in place.
                    byte candidateY = (byte)(PlayerY - 1);
                    if (BoxClear(PlayerX, candidateY))
                    {
                        PlayerY = candidateY;
                        moved = true;
                        remaining--;
                    }
                    else
                    {
                        VerticalVelocity = 0;
                        remaining = 0;
                    }
                }
            }
        }
        else
        {
            // HasSupport includes both static terrain and the special chest
            // surface. Non-rising feet support stops velocity immediately.
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

                // Descending acceleration caps at +4. Check support after
                // every accepted pixel; landing clears speed AND step budget.
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

                // 170 is the last drawing-safe origin. In upper room 3 it is
                // a return link: preserve fall speed, subtract 100 from Y and
                // return at saved ReturnX. Else it remains only a drawing bound,
                // not solid terrain or a reason to declare Grounded.
                if (PlayerY == 170)
                {
                    if (CurrentRoom == 3)
                    {
                        sbyte fallVelocity = VerticalVelocity;
                        byte returnY = (byte)(PlayerY - 100);
                        Sam.RestoreMemoryPage();
                        EnterRoom(2, ReturnX, returnY);
                        VerticalVelocity = fallVelocity;
                        Grounded = false;
                        Sam.MapMemoryPage(12);
                    }
                    else
                    {
                        Grounded = false;
                        VerticalVelocity = 0;
                    }
                }
            }
        }
        Sam.RestoreMemoryPage();
        return moved;
    }

    static bool HasSupport(byte x, byte y)
    {
        // Dynamic chest support is tested separately from static terrain.
        // Normal foot support tests ANY solid pixel on Y+21 beneath the inset
        // body (X+8..X+17, Y+4..Y+20). Caller selected mask bank 12.
        if (ChestSupports(x, y))
            return true;
        return SolidRow((byte)(y + 21), (byte)(x + 8));
    }

    static bool ChestSupports(byte x, byte y)
    {
        // Only the placed puzzle chest in room 2 supports this way. Match the
        // implemented feet-row convention EXACTLY: feetRow+1 == chest top Y.
        // Then compare inclusive horizontal PIXEL bounds; ItemX/width must be
        // multiplied by two because they are packed bytes. ANY overlap supports.
        // This is not a full solid-rectangle body test and does not modify the
        // static terrain mask consulted by BoxClear.
        if (!ChestPlaced || CurrentRoom != 2)
            return false;
        byte feetRow = (byte)(y + 21);
        if (feetRow + 1 != ItemY[0])
            return false;
        byte chestLeft = (byte)(ItemX[0] * 2);
        byte chestRight = (byte)((ItemX[0] + ItemWidths[0]) * 2 - 1);
        byte playerLeft = (byte)(x + 8);
        byte playerRight = (byte)(x + 17);
        if (playerRight < chestLeft)
            return false;
        if (playerLeft > chestRight)
            return false;
        return true;
    }

    static bool CheckHazards()
    {
        // Test the same inset body used by solid collision. Hazards are in
        // PIXELS with inclusive edges, separate from the terrain mask so the
        // player can pass through them. The sea stays fixed in room 1; the bee
        // rectangle follows its actor in room 4 on every update.
        if (CurrentRoom != 1 && CurrentRoom != 4)
            return false;

        byte bodyLeft = (byte)(PlayerX + 8);
        byte bodyRight = (byte)(PlayerX + 17);
        byte bodyTop = (byte)(PlayerY + 4);
        byte bodyBottom = (byte)(PlayerY + 20);
        byte hazardLeft = 0;
        byte hazardRight = 0;
        byte hazardTop = 0;
        byte hazardBottom = 0;
        if (CurrentRoom == 1)
        {
            // The sea begins just beyond the shoreline. Edges are inclusive,
            // so touching row 144 or pixel column 200 counts as contact.
            hazardLeft = 200;
            hazardRight = 255;
            hazardTop = 144;
            hazardBottom = 191;
        }
        else
        {
            // Omit empty wing tips and antenna/stinger margins. BeeX is in
            // byte columns, so multiply by two before adding pixel offsets.
            hazardLeft = (byte)(BeeX * 2 + 2);
            hazardRight = (byte)(BeeX * 2 + 18);
            hazardTop = (byte)(BeeY + 9);
            hazardBottom = (byte)(BeeY + 21);
        }

        // Two inclusive rectangles overlap unless one lies wholly before the
        // other. The same four early-outs work for the stationary sea and a
        // moving bee because both use the same body-box units.
        if (bodyRight < hazardLeft)
            return false;
        if (bodyLeft > hazardRight)
            return false;
        if (bodyBottom < hazardTop)
            return false;
        if (bodyTop > hazardBottom)
            return false;
        return true;
    }

    static bool BoxClear(byte x, byte y)
    {
        // Scan every row and ten pixels of the inset body, not just corners:
        // middle protrusions must block us. Artwork margins are excluded.
        // This helper tests the mapped static mask, not ChestSupports.
        byte firstX = (byte)(x + 8);
        byte firstY = (byte)(y + 4);
        ushort offset = (ushort)(firstY * 128 + firstX / 2);
        return !Sam.AnySolidPixelInPackedRectangle(
            TutorialCollision.CollisionMask, offset, (byte)(firstX & 1), 10, 17);
    }

    static bool SolidRow(byte y, byte firstX)
    {
        // A nibble is four bits, one packed collision pixel. Even X begins at
        // the high nibble, odd at the low. Screen-like 128-byte row stride and
        // exact ten-pixel count avoid false hits from neighbouring half-bytes.
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

    [BankedCode(3)]
    static void EnterRoom(byte room, byte x, byte y)
    {
        // Replace ID/spawn, reset ordinary support/velocity, choose matching
        // scenery and mask, then initialize both screens. Persistent inventory,
        // ChestPlaced and ReturnX survive; vertical callers explicitly restore
        // their saved velocity and airborne state after this default reset.
        CurrentRoom = room;
        PlayerX = x;
        PlayerY = y;
        Grounded = true;
        VerticalVelocity = 0;
        // Both display pages were just replaced; their old bee rectangles no
        // longer exist and must not be copied over the destination room.
        BeePreviousX10 = 255;
        BeePreviousX14 = 255;
        LoadCollision(room);
        BuildRoom(room);
        // Replace the hidden bitmap first, flip when complete, then initialize
        // the other page so no old-room pixels can be restored later.
        CopyBackground();
        Sam.FlipScreen();
        CopyBackground();
        Sam.FlipScreen();
    }

    [BankedCode(3)]
    static void LoadCollision(byte room)
    {
        // Repeated-byte runs keep compact room masks banked. Expand the chosen
        // source into working bank 12. Selecting room ID consistently for art
        // and mask prevents invisible walls or pass-through painted platforms.
        byte[] runs = Room1CollisionRunsData.Room1Runs;
        if (room == 2)
            runs = Room2CollisionRunsData.Room2Runs;
        else if (room == 3)
            runs = Room3CollisionRunsData.Room3Runs;
        else if (room == 4)
            runs = Room4CollisionRunsData.Room4Runs;
        // The stream stores little-endian 16-bit run lengths and packed mask
        // bytes. The SDK kernel expands it into the single working mask bank.
        Sam.ExpandByteRuns(runs, 8, TutorialCollision.CollisionMask, 12);
    }

    [BankedCode(3)]
    static void BuildRoom(byte room)
    {
        // RLE = run-length encoding, storing repetitions compactly. Expand
        // selected scenery into the clean 24 KB bank-6 image, then add live
        // world items and HUD. Do not restore from an old painted display page.
        byte[] encoded = Room1BackgroundData.Room1Background;
        if (room == 2)
            encoded = Room2BackgroundData.Room2Background;
        else if (room == 3)
            encoded = Room3BackgroundData.Room3Background;
        else if (room == 4)
            encoded = Room4BackgroundData.Room4Background;
        // Decode one exact 24 KB MODE 4 image into page 6; the screen copier
        // then transfers rows to the hidden display page.
        Sam.DecodeRleBackground(encoded, 8, TutorialRooms.Background, 6);
        BuildItemsAndHud();
    }

    [BankedCode(3)]
    static void BuildItemsAndHud()
    {
        // ItemX is already a byte column, unlike PlayerX pixels. Same ID indexes
        // all arrays. Ordinary items draw only in their own room; the special
        // placed chest (sentinel 254) is explicitly drawn in room 2.
        // HUD means heads-up display: the visible inventory slot frames/icons.
        // Draw only records whose room matches the active scene; carried
        // identifiers (255) have no world copy.
        byte item = 0;
        while (item < 3)
        {
            if (ItemRooms[item] == CurrentRoom || item == 0 && ChestPlaced && CurrentRoom == 2)
            {
                ushort worldOffset = (ushort)(ItemY[item] * 128 + ItemX[item]);
                ComposeItem(item, worldOffset);
            }
            item++;
        }

        // The HUD is part of the background, so restoration and page flips
        // treat it exactly like the scenery. Empty slots still get a frame.
        // slotX is packed bytes; outlines are 24 bytes wide and 32 rows high.
        // 0x77 colours both halves, 0x70/0x07 their edge halves separately.
        // Sentinel 255 leaves an empty framed slot instead of an item icon.
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

    [BankedCode(3)]
    static void ComposeItem(byte item, ushort destination)
    {
        // Extracted images retain a two-byte width/height header; their pixel
        // bodies begin at array offset 2. A source offset counts bytes from the
        // array start, while destination is a scene byte offset for world/HUD.
        // Width/stride are packed bytes, height rows; source stride equals width
        // here, while destination screen rows remain 128 bytes apart.
        // Transparent colour-zero nibbles preserve background. SDK staging
        // bridges source bank 8 and scene bank 6 without hiding the source.
        // The chest and both coins use their original, separately named
        // C# source arrays. Items 1 and 2 intentionally share one coin image.
        byte[] image = TutorialChest.ChestImage;
        if (item != 0)
            image = TutorialCoin.CoinImage;

        Sam.ComposeTransparentImage(
            image,
            8,
            TutorialRooms.Background,
            6,
            2,
            destination,
            ItemWidths[item],
            ItemHeights[item],
            ItemWidths[item]);
    }

    [BankedCode(3)]
    static bool TryInventoryAction()
    {
        // Consume each new Enter edge once, even on rejection; require support
        // and no held direction. Pickup has priority over pop/drop. The sentinel
        // 254 placed chest is not an ordinary room record for this pickup test.
        bool enterDown = Sam.IsKeyPressed(0xBFFE, 0x01);
        bool freshPress = enterDown && !EnterWasDown;
        // Consume every press, even if the player is airborne or too far away.
        EnterWasDown = enterDown;
        if (!freshPress || !Grounded)
            return false;
        if (Sam.IsKeyPressed(0xFEFE, 0x02) || Sam.IsKeyPressed(0xFEFE, 0x04))
            return false;

        byte item = 0;
        while (item < 3)
        {
            if (ItemRooms[item] == CurrentRoom && ItemOverlaps(item))
            {
                // A full stack rejects pickup; it never silently loses its oldest entry.
                if (InventoryCount == 3)
                {
                    ActionFlash = 20;
                    return false;
                }

                // Push an ID at count, advance count, mark its world room
                // carried (255); the image bytes themselves never move.
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
        // LIFO = last in, first out. Drop only the newest occupied slot.
        // Guarding count above prevents empty unsigned subtraction wrapping.
        byte top = (byte)(InventoryCount - 1);
        item = InventorySlots[top];
        return TryDropItem(item, top);
    }

    [BankedCode(3)]
    static bool ItemOverlaps(byte item)
    {
        // Compare identical units: item byte X/width become PIXEL bounds,
        // matching the inset player. Inclusive right/bottom subtract one;
        // separation on either axis rejects the overlap.
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

    [BankedCode(3)]
    static bool TryDropItem(byte item, byte slot)
    {
        // Atomic means all-or-nothing: search at most seven nearby rows without
        // changing records. Bounds, clear terrain rectangle and support must
        // succeed BEFORE coordinates/room and inventory count are committed.
        // A failed candidate never loses the carried item.
        // Align the item to a packed-byte X near the player's centre. Search
        // only seven nearby rows, never an arbitrary distant landing point.
        byte dropX = (byte)((PlayerX + 3) / 2);
        byte itemHeight = ItemHeights[item];
        byte firstY = (byte)(PlayerY + 18 - itemHeight);
        if (firstY < 48)
            firstY = 48;

        byte attempt = 0;
        byte candidateY = firstY;
        // Reserve the support row y+height inside screen rows 0..191 as well
        // as the item image itself; this is not just a drawing-only height limit.
        byte lastDropY = (byte)(191 - itemHeight);
        while (attempt < 7)
        {
            if (candidateY <= lastDropY &&
                DropFits(item, dropX, candidateY))
            {
                // A valid drop can additionally overlap the puzzle's trigger
                // region: X tests are byte columns, Y tests are rows. Only
                // chest ID 0 in room 2, not already placed, can activate it.
                bool chestTrigger = false;
                if (item == 0 && CurrentRoom == 2 && !ChestPlaced &&
                    dropX < 31 &&
                    dropX + ItemWidths[item] >= 21 &&
                    candidateY < 112 &&
                    candidateY + ItemHeights[item] >= 100)
                {
                    chestTrigger = true;
                }

                // Commit every safe drop, even outside the trigger. Triggered
                // chest drops additionally persist ChestPlaced, save ReturnX
                // and use room sentinel 254; ordinary drops use CurrentRoom.
                ItemX[item] = dropX;
                ItemY[item] = candidateY;
                if (chestTrigger)
                {
                    ChestPlaced = true;
                    ReturnX = PlayerX;
                    ItemRooms[item] = 254;
                }
                else
                {
                    ItemRooms[item] = CurrentRoom;
                }
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
        // xByte is already byte-aligned, hence starting parity zero. Probe
        // every pixel of every item row against terrain, advancing offset by
        // 128-byte stride. Afterwards offset points at the row just underneath;
        // ANY solid pixel there supplies support. Other item overlap and the
        // chest's dynamic support are not part of this static terrain check.
        // Restore bank 12 on success AND early rejection.
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

    [BankedCode(3)]
    static void RefreshRoom()
    {
        // Rebuild from current world/slot records ("authoritative" state), then
        // copy/flip both buffers. Two flips retain the expected buffer order
        // while refreshing the clean source used for old-player restoration.
        // Recompose authoritative world and HUD once, then initialize each
        // display page from that finished image before drawing the player again.
        BuildRoom(CurrentRoom);
        CopyBackground();
        Sam.FlipScreen();
        CopyBackground();
        Sam.FlipScreen();
    }

    [BankedCode(3)]
    static void CopyBackground()
    {
        // Width 128 is BYTES, height 192 ROWS: the whole 24 KB MODE 4 scene.
        // Resident staging avoids aliasing (mistaking one mapped RAM window
        // for two simultaneously accessible source and screen banks).
        // The source is banked scenery; the typed kernel stages rows before
        // paging the hidden screen, avoiding an alias between the two pages.
        Sam.CopyBankedBytesToScreen(TutorialRooms.Background, 6, 0, 0, 128, 192);
    }

    [BankedCode(3)]
    static void RestoreBackground(byte x, byte y, byte width)
    {
        // Restore 22 OLD player rows, with 13/14 byte width from old X parity.
        // Both offsets use Y*128+X/2 and both strides remain 128, not cropped
        // width. Clean scenery includes current world objects and HUD.
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
        // One header byte then 572-byte bodies: 13 columns * 22 rows * two
        // interleaved mask/pixel bytes. Masks keep background bits; pixels add
        // visible art. Stage body after its preserved resident header, then
        // restore source bank 4 before screen accesses or ROM frame waits.
        Sam.MapMemoryPage(4);
        Sam.CopyBytes(
            TutorialData.SpriteFrames,
            (ushort)(1 + index * 572),
            TutorialData.FrameBuffer,
            1,
            572);
        Sam.RestoreMemoryPage();
    }

    static void LoadWaterFrame(byte frame)
    {
        // A banked data page cannot stay mapped while resident C# continues
        // executing. Copy both ten-byte halves into resident buffers, then
        // restore the lower mapping before using either half.
        ushort frameOffset = (ushort)(1 + frame * 80);
        Sam.MapMemoryPage(8);
        Sam.CopyBytes(
            WaterFramesData.LeftFrames,
            frameOffset,
            WaterFramesData.LeftFrameBuffer,
            1,
            80);
        Sam.CopyBytes(
            WaterFramesData.RightFrames,
            frameOffset,
            WaterFramesData.RightFrameBuffer,
            1,
            80);
        Sam.RestoreMemoryPage();
    }

    static bool AdvanceBee()
    {
        // Room 4 is screen 4 in the four-screen route. Do not spend the actor's
        // animation ticks elsewhere; entering the meadow starts its local
        // three-interrupt rhythm. Return true only when position or wings change.
        if (CurrentRoom != 4)
            return false;
        BeeTimer--;
        if (BeeTimer != 0)
            return false;

        BeeTimer = 3;
        BeeFlap = (byte)(BeeFlap ^ 1);
        if (BeeVelocity > 0)
            BeeX++;
        else
            BeeX--;

        // The source actor steps just beyond each limit, then reverses:
        // actual endpoints are byte columns 54 and 116, both still in bounds.
        if (BeeX < 55 || BeeX > 115)
        {
            if (BeeVelocity > 0)
                BeeVelocity = -1;
            else
                BeeVelocity = 1;
        }

        // Right-facing frames are 0/1 and left-facing frames are 2/3.
        BeeFrame = BeeFlap;
        if (BeeVelocity < 0)
            BeeFrame = (byte)(BeeFrame + 2);
        return true;
    }

    static void RestoreBee(bool page14)
    {
        // Restore this buffer's prior 11-byte by 22-row rectangle from clean
        // room art. X is already a byte column; Y is a pixel row.
        if (CurrentRoom != 4)
            return;
        byte oldX = BeePreviousX10;
        if (page14)
            oldX = BeePreviousX14;
        if (oldX == 255)
            return;
        ushort offset = (ushort)(BeeY * 128 + oldX);
        Sam.CopyBankedBytesToScreen(
            TutorialRooms.Background,
            6,
            offset,
            offset,
            11,
            22);
    }

    static void DrawBee(bool page14)
    {
        // Stage no shifted copy: a whole-byte X origin keeps both packed pixels
        // aligned. Masks preserve the meadow wherever source colour is zero.
        if (CurrentRoom != 4)
            return;
        ushort offset = (ushort)(BeeY * 128 + BeeX);
        Sam.DrawMaskedSprite(
            Sam.SelectSpriteFrame(BeeFramesData.Frames, BeeFrame),
            offset,
            11,
            22);
        if (page14)
            BeePreviousX14 = BeeX;
        else
            BeePreviousX10 = BeeX;
    }

    static void RestoreWater()
    {
        // The source room registers two adjoining strips on rows 139–142.
        // Restore from the authoritative water-free scene, not from either
        // display page, so the previous frame cannot leave a coloured trail.
        if (CurrentRoom != 1)
            return;
        Sam.CopyBankedBytesToScreen(
            TutorialRooms.Background,
            6,
            17890,
            17890,
            20,
            4);
        Sam.CopyBankedBytesToScreen(
            TutorialRooms.Background,
            6,
            17910,
            17910,
            10,
            4);
    }

    static void DrawWater()
    {
        // The source is 20 bytes wide, but the sprite kernel intentionally
        // limits one rectangle to 16 bytes. Draw its two ten-byte halves, then
        // reuse the left half for the final clipped strip at the screen edge.
        // Masks make zero nibbles transparent, including over Dizzy's art.
        if (CurrentRoom != 1)
            return;
        Sam.DrawMaskedSprite(
            Sam.SelectSpriteFrame(WaterFramesData.LeftFrameBuffer, 0),
            17890,
            10,
            4);
        Sam.DrawMaskedSprite(
            Sam.SelectSpriteFrame(WaterFramesData.RightFrameBuffer, 0),
            17900,
            10,
            4);
        Sam.DrawMaskedSprite(
            Sam.SelectSpriteFrame(WaterFramesData.LeftFrameBuffer, 0),
            17910,
            10,
            4);
    }
}
