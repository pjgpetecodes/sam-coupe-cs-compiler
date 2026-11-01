// Blog 6, lesson 8-3: cropped frames and sideways-jump choices; source: assembly\blog6\8-3.asm.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // These byte values (unsigned 0..255) track the local pose, sequence ID, and selected colour.
        // Keeping animation separate from frame lets every new sequence begin at pose zero.
        byte frame = 0;
        byte animation = 0;
        byte background = 0;
        // The editor palette is retained, except entry zero initially becomes black.
        // CLUT means Colour Look-Up Table: a pixel's four-bit index selects a displayed colour.
        // Cleared screen bytes contain index 0 in both halves, so override that entry after loading.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.SetPaletteEntry(0, 0);
        Sam.BeginDoubleBuffer();
        while (true)
        {
            // Keys 1..5 are active-low bits 0..4 of row F7; the selected background persists on release.
            // Port FE reads the keyboard; its high byte selects a row through a zero bit.
            // Masks 01/02/04/08/10 test individual columns. Active-low means 0 = pressed,
            // which IsKeyPressed translates to bool true. Else-if gives the lowest key priority.
            if (Sam.IsKeyPressed(0xF7FE, 0x01))
                background = 0;
            else if (Sam.IsKeyPressed(0xF7FE, 0x02))
                background = 1;
            else if (Sam.IsKeyPressed(0xF7FE, 0x04))
                background = 2;
            else if (Sam.IsKeyPressed(0xF7FE, 0x08))
                background = 3;
            else if (Sam.IsKeyPressed(0xF7FE, 0x10))
                background = 4;
            // Changing CLUT entry zero recolours the background, not the stored screen bytes.
            byte backgroundColour = 0;
            if (background == 1)
                backgroundColour = 0x11;
            else if (background == 2)
                backgroundColour = 0x22;
            else if (background == 3)
                backgroundColour = 0x44;
            else if (background == 4)
                backgroundColour = 0x7F;
            Sam.SetPaletteEntry(0, backgroundColour);

            // IDs: 0 idle, 1 roll, 2/3 left/right walk, 4/5 left/right jump.
            // Space alone rolls; Space+Z/X jumps sideways. Z wins when both directions are held.
            // Space is row 7F bit 0; Z/X are row FE bits 1/2, all on active-low port FE.
            // Rolls/jumps finish despite release; held Space may retrigger afterwards (no edge latch).
            // Start nextAnimation as the current ID. The && condition means ALL three comparisons
            // must hold before reading new action input, so a running roll/jump keeps its state.
            // An edge latch would remember last iteration's released/pressed state; none is used here.
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
                // Change ID and rewind the local counter together, avoiding a mid-sequence start.
                animation = nextAnimation;
                frame = 0;
            }

            // Frame ranges: idle 0,1; roll 0,2..8; walks 9..16 and 17..23; jumps 24..30 and 31..37.
            // The cropped source omits image 25, so jump offsets 24/31 begin source images 26/33.
            // Add each sequence's table start to its local pose number. Roll instead skips image 1.
            // C# promotes byte addition to int; (byte) converts the known-small table index back.
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

            // Offset 641 is row 5, byte 1: 5*128+1. Cropping gives 13 bytes x 22 rows (26x22 pixels).
            // RLE expands to 26 mask/pixel bytes per source row; screen rows still stride by 128.
            // Cropping removes unused outer rows/columns without changing the surviving pixels.
            // RLE means run-length encoding, saving RAM with repeat/literal packets; drawing
            // combines (screen & mask) | pixel. Each source pair describes two four-bit pixels.
            // Clear/draw the hidden page before flipping the completed frame.
            Sam.ClearSprite(641, 13, 22);
            // The editor's sideways motion is in the frames, not an extra X translation.
            Sam.DrawRleMaskedSprite(Sam.SelectSpriteFrame(TutorialData.SpriteFrames, sprite), 641, 13, 22);
            Sam.FlipScreen();
            // The runtime restores ROM for HALT and preserves our palette across its interrupts.
            // ROM is built-in firmware; HALT waits for its periodic handler. Five 50 Hz waits
            // request about 0.1 seconds per pose, plus decoding/drawing elapsed time.
            Sam.WaitForFrames(5);
            // Only idle/walk loop; all three action sequences finish and reset to idle.
            // && joins each ID with its length test; || accepts any completed action.
            // Test after showing a pose, so its last image is not lost. There is no break:
            // returning to idle changes state, while the main while loop continues indefinitely.
            frame++;
            if (animation == 0 && frame == 2)
                frame = 0;
            else if (animation == 2 && frame == 8)
                frame = 0;
            else if (animation == 3 && frame == 7)
                frame = 0;
            else if (animation == 1 && frame == 8 ||
                     animation == 4 && frame == 7 ||
                     animation == 5 && frame == 7)
            {
                animation = 0;
                frame = 0;
            }
        }
    }
}
