using SamCoupe.Sdk;

// Blog09 positioned the moving sprite one pixel at a time. This first Blog10
// example isolates scenery: copy a palm's packed image to the hidden screen,
// then display the completed picture. There is no player or collision yet.
static class Program
{
    static void Main()
    {
        // A palette translates the four-bit colour numbers in the image into
        // actual colours. Double buffering gives us a screen that is not being
        // displayed, so the viewer never sees a partly copied palm.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.BeginDoubleBuffer();
        // Skip the two-byte image header. Width and source stride are measured
        // in BYTES (41 bytes = 82 MODE 4 pixels); height is 77 screen rows.
        // A screen row occupies 128 bytes, so row 70, byte column 40 has this
        // offset from the start of the screen, not an absolute RAM address.
        CopyRows(2, 41, 77, 41, 70 * 128 + 40);
        Sam.FlipScreen();
        Sam.SetBorderColor(7);
        while (true)
        {
            // Waiting lets the ROM frame interrupt run. It can reset the
            // palette, so reload our colours after each wait, not before it.
            Sam.WaitForFrames(1);
            Sam.LoadTutorialPalette(TutorialData.Palette);
            Sam.SetBorderColor(7);
        }
    }

    static void CopyRows(int sourceOffset, int width, int rows, int sourceStride, int destination)
    {
        // int keeps row/offset arithmetic wide enough for a complete screen.
        // ushort is the unsigned 16-bit offset/count expected by the SDK.
        // "Stride" means the distance to the next row's first byte. It need not
        // equal the copied width: a cropped image can skip unused source bytes.
        for (int row = 0; row < rows; row++)
        {
            // Banked memory is RAM made accessible by selecting a page. Page 8
            // holds the source; staging one row in resident RowBuffer keeps it
            // available when the SDK maps the screen into the same RAM window.
            Sam.MapMemoryPage(8);
            Sam.CopyBytes(
                TutorialData.PalmGraphic,
                (ushort)(sourceOffset + row * sourceStride),
                TutorialData.RowBuffer,
                0,
                (ushort)width);
            Sam.RestoreMemoryPage();
            // Source rows advance by sourceStride, but destination rows always
            // advance by the MODE 4 screen stride, 128 bytes. Copy only width,
            // leaving the remainder of that screen row unchanged.
            Sam.CopyBytesToScreen(
                TutorialData.RowBuffer,
                0,
                (ushort)(destination + row * 128),
                (ushort)width);
        }
    }
}
