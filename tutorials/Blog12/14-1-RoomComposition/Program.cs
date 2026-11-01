using SamCoupe.Sdk;

// Blog11 restored a ready-made beach. This lesson first builds that clean
// background from a list of scenery blocks, then uses the same restore/draw/
// flip loop. Room records describe placement; they are not collision rules.
static class Program
{
    static void Main()
    {
        // Build one authoritative background before copying it into both
        // display buffers. Screen starts 10/14, background 6 and graphics 8
        // refer to physical RAM banks; none is a pixel coordinate.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.SetBorderColor(7);
        Sam.BeginDoubleBuffer(10, 14);
        BuildRoom(TutorialRooms.Room1Graphics, TutorialRooms.Room1Records);
        CopyBackground();
        Sam.FlipScreen();
        CopyBackground();
        Sam.FlipScreen();

        // byte counters/coordinates fit 0..255; bool flags describe decisions.
        // animation identifies the sequence, frame the position within it,
        // and delay the number of frame waits before the next image change.
        byte frame = 0;
        byte animation = 0;
        byte x = 0;
        byte movePhase = 0;
        byte delay = 5;
        // Each buffer retains its own old sprite. Remember the X belonging to
        // the page about to be drawn, not merely the last visible player's X.
        byte previousX10 = 0;
        byte previousX14 = 0;
        bool drawingPage14 = true;
        bool redraw = true;
        bool frameChanged = true;

        while (true)
        {
            // Input is active-low at the hardware; IsKeyPressed interprets it.
            // Alternate 1/3-pixel movement and clamp X to 0..230 so the complete
            // 26-pixel sprite fits. Left takes priority over right.
            bool moved = false;
            byte step = 1;
            if (movePhase != 0)
                step = 3;
            if (Sam.IsKeyPressed(0xFEFE, 0x02))
            {
                if (x >= step)
                {
                    x = (byte)(x - step);
                    moved = true;
                }
                else if (x != 0)
                {
                    x = 0;
                    moved = true;
                }
                movePhase = (byte)(1 - movePhase);
            }
            else if (Sam.IsKeyPressed(0xFEFE, 0x04))
            {
                if (x <= 230 - step)
                {
                    x = (byte)(x + step);
                    moved = true;
                }
                else if (x != 230)
                {
                    x = 230;
                    moved = true;
                }
                movePhase = (byte)(1 - movePhase);
            }

            // Sequence IDs: idle 0, straight jump 1, walks 2/3, side jumps 4/5.
            // These are visual jumps only; the screen Y remains 115. Do not
            // interrupt a jump sequence with walking input before it finishes.
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

            // Idle/walk sequences loop; jump sequences finish at idle. Restart
            // the five-frame timer and flag the image for loading on a change.
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

            // Translate an animation-local frame to the shared table index.
            // Prepare both byte-aligned and one-pixel-shifted masks/pixels
            // only when the image changes, not every time the player moves.
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

            // Erase the hidden page's old footprint, draw the new footprint,
            // then flip. Odd X uses 14 rather than 13 packed bytes because the
            // shifted 26-pixel image straddles one additional screen byte.
            if (redraw)
            {
                byte oldX = previousX10;
                if (drawingPage14)
                    oldX = previousX14;
                byte oldWidth = 13;
                if ((oldX & 1) != 0)
                    oldWidth = 14;
                RestoreBackground(oldX, oldWidth);

                // Two MODE 4 pixels share a byte; a screen row has 128 bytes.
                // This ushort is a 16-bit byte offset from the screen start.
                ushort offset = (ushort)(115 * 128 + x / 2);
                if ((x & 1) == 0)
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
                    previousX14 = x;
                else
                    previousX10 = x;
                drawingPage14 = !drawingPage14;
                redraw = false;
            }

            // Wait only after helpers restore normal bank/ROM accessibility.
            // Drawing and image loading use separate flags from this timer.
            Sam.WaitForFrames(1);
            if (delay != 0)
                delay--;
        }
    }

    static void BuildRoom(byte[] graphics, byte[] records)
    {
        // Start with black so untouched areas are predictable. Background is
        // banked on page 6; BlankRow is the resident zero-filled row source.
        // A byte[] is the table reference, not a newly allocated room here.
        Sam.MapMemoryPage(6);
        for (int row = 0; row < 192; row++)
            Sam.CopyBytes(TutorialRooms.BlankRow, 0, TutorialRooms.Background, (ushort)(row * 128), 128);
        Sam.RestoreMemoryPage();

        // Record zero gives the number of blocks. Each following seven-byte
        // record stores: 16-bit source offset, 16-bit destination offset,
        // byte width, row height and source stride. int avoids narrow arithmetic
        // while walking the table; ushort expresses valid SDK byte offsets.
        int blockCount = records[0];
        for (int block = 0; block < blockCount; block++)
        {
            int record = 1 + block * 7;
            ushort source = ReadWord(records, record);
            ushort destination = ReadWord(records, record + 2);
            byte width = records[record + 4];
            byte height = records[record + 5];
            byte stride = records[record + 6];

            // Width is the part to copy; stride is the distance between starts
            // of source rows. Different values crop out a slice of a wider
            // image without changing its stored layout. Screen rows always
            // advance 128 bytes, independently of the source image's width.
            for (int row = 0; row < height; row++)
            {
                ushort sourceOffset = (ushort)(source + row * stride);
                ushort destinationOffset = (ushort)(destination + row * 128);
                // Stage graphics in resident GraphicsRow before mapping page
                // 6. Otherwise switching the RAM window could hide our source.
                Sam.MapMemoryPage(8);
                Sam.CopyBytes(graphics, sourceOffset, TutorialRooms.GraphicsRow, 0, width);
                Sam.RestoreMemoryPage();

                Sam.MapMemoryPage(6);
                for (int column = 0; column < width; column++)
                {
                    byte sourceByte = TutorialRooms.GraphicsRow[column];
                    byte destinationByte = TutorialRooms.Background[destinationOffset + column];
                    // A nibble is a four-bit pixel colour. In these scenery
                    // blocks colour zero means transparent: preserve the old
                    // background nibble rather than painting black over it.
                    // Test high/low nibbles separately so one transparent pixel
                    // does not suppress its opaque neighbour.
                    byte composed = sourceByte;
                    if ((sourceByte & 0xF0) == 0)
                        composed = (byte)((composed & 0x0F) | (destinationByte & 0xF0));
                    if ((sourceByte & 0x0F) == 0)
                        composed = (byte)((composed & 0xF0) | (destinationByte & 0x0F));
                    TutorialRooms.Background[destinationOffset + column] = composed;
                }
                Sam.RestoreMemoryPage();
            }
        }
    }

    static ushort ReadWord(byte[] data, int index)
    {
        // "Little endian" stores the low byte first. Shift the following high
        // byte eight bits left and combine them into one unsigned 16-bit value.
        return (ushort)(data[index] | data[index + 1] << 8);
    }

    static void CopyBackground()
    {
        // Stage every clean-background row from bank 6 into resident memory,
        // restore that mapping, then let the SDK map and write the hidden
        // screen. The full image is 192 * 128 = 24576 bytes (24 KB).
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

    static void RestoreBackground(byte x, byte width)
    {
        // Restore only the previous sprite's 22 rows from the composed clean
        // room, using pixel X / 2 as its packed byte column. width must include
        // the extra byte for odd-X shifted images, or a trail would remain.
        ushort screenOffset = (ushort)(115 * 128 + x / 2);
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
        // Skip the table header at byte 0; frame bodies have interleaved mask/
        // pixel pairs: 13 byte columns * 22 rows * 2 = 572 bytes. Preserve the
        // destination header and stage the body before leaving source bank 4.
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
