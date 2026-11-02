// Blog 6, lesson 8-3: explicit native drawing, with animation still written in C#.
using SamCoupe.Sdk;

static class Program
{
    // IDs: idle, roll, left walk, right walk, left jump, right jump.
    static readonly byte[] Lengths = { 2, 8, 8, 7, 7, 7 };
    static readonly byte[] Starts = { 0, 0, 9, 17, 24, 31 };

    static void Main()
    {
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.SetPaletteEntry(0, 0);
        Sam.BeginDoubleBuffer(4, 6);
        // Validate once and build a frame-pointer table. No decompression in the draw loop.
        // 641 = row 5 * 128 + byte 1; each cropped row holds 13 mask/pixel pairs.
        Sam.PrepareFixedSpriteRenderer(TutorialData.SpriteFrames, 641, 13, 22);
        Sam.DrawPreparedSprite(0);
        byte animation = 0;
        byte frame = 0;
        byte delay = 5;
        byte length = 2;
        byte start = 0;
        bool pending = true;
        while (true)
        {
            // Like the assembly, tick every refresh and present only a complete pending frame.
            // The five-tick countdown includes drawing time instead of waiting five more ticks.
            Sam.WaitForFrames(1);
            if (pending)
            {
                Sam.FlipScreen();
                pending = false;
            }

            // Read each row once. Keyboard bits are active-low; keys 1..5 keep their colour.
            byte colours = Sam.ReadKeyboardRow(0xF7FE);
            if (Sam.IsKeyPressedInRow(colours, 31))
            {
                byte colour = 0;
                if (Sam.IsKeyPressedInRow(colours, 1))
                    colour = 0;
                else if (Sam.IsKeyPressedInRow(colours, 2))
                    colour = 0x11;
                else if (Sam.IsKeyPressedInRow(colours, 4))
                    colour = 0x22;
                else if (Sam.IsKeyPressedInRow(colours, 8))
                    colour = 0x44;
                else
                    colour = 0x7F;
                Sam.SetPaletteEntry(0, colour);
            }

            bool redraw = false;
            // Rolls/jumps finish before sampling action keys. Held Space may retrigger.
            if (animation != 1 && animation != 4 && animation != 5)
            {
                byte next = 0;
                byte directions = Sam.ReadKeyboardRow(0xFEFE);
                byte space = Sam.ReadKeyboardRow(0x7FFE);
                if (Sam.IsKeyPressedInRow(directions, 2))
                    next = 2;
                else if (Sam.IsKeyPressedInRow(directions, 4))
                    next = 3;
                if (Sam.IsKeyPressedInRow(space, 1))
                {
                    if (next == 2)
                        next = 4;
                    else if (next == 3)
                        next = 5;
                    else
                        next = 1;
                }
                if (next != animation)
                {
                    animation = next;
                    frame = 0;
                    delay = 5;
                    length = Lengths[animation];
                    start = Starts[animation];
                    redraw = true;
                }
            }

            // Frame advancement remains ordinary C#. Only idle and walks loop.
            delay--;
            if (delay == 0)
            {
                delay = 5;
                frame++;
                if (frame == length)
                {
                    frame = 0;
                    if (animation == 1 || animation == 4 || animation == 5)
                    {
                        animation = 0;
                        length = 2;
                        start = 0;
                    }
                }
                redraw = true;
            }
            if (redraw)
            {
                byte sprite = (byte)(start + frame);
                if (animation == 1 && frame != 0)
                    sprite++;
                // Clear/draw the hidden rectangle; flipping is a separate, next-refresh action.
                Sam.DrawPreparedSprite(sprite);
                pending = true;
            }
        }
    }
}
