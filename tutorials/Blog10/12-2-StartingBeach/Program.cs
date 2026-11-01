using SamCoupe.Sdk;

// The static palm example copied a small rectangle. Here the same bank-to-row-
// buffer-to-screen technique copies all 192 rows of the starting beach.
static class Program
{
    static void Main()
    {
        // Draw into the hidden buffer first. Flipping changes which completed
        // screen is visible; it is not a request to copy the image again.
        Sam.LoadTutorialPalette(TutorialData.Palette);
        Sam.BeginDoubleBuffer();
        CopyRows();
        Sam.FlipScreen();
        Sam.SetBorderColor(7);
        while (true)
        {
            // The ROM interrupt supplies the frame wait and may replace CLUT
            // colours. Restore the tutorial palette and border afterwards.
            Sam.WaitForFrames(1);
            Sam.LoadTutorialPalette(TutorialData.Palette);
            Sam.SetBorderColor(7);
        }
    }

    static void CopyRows()
    {
        // MODE 4 packs two pixels into each byte: 256 pixels / 2 = 128 bytes
        // per row. The 24 KB image has two 96-row halves in separate banks.
        // Source row zero of the lower half is screen row 96, not screen row 0.
        for (int row = 0; row < 192; row++)
        {
            if (row < 96)
                CopyRow(TutorialData.BeachScreenTop, 8, row, row);
            else
                CopyRow(TutorialData.BeachScreenBottom, 10, row - 96, row);
        }
    }

    static void CopyRow(byte[] source, byte page, int sourceRow, int screenRow)
    {
        // byte[] names the source bytes; byte holds the small page number.
        // int is used for multiplication, and ushort explicitly converts the
        // valid offset to the SDK's 16-bit screen/buffer offset type.
        // Map only while reading the banked source. RowBuffer stays resident,
        // so it survives restoring the bank before screen RAM is accessed.
        Sam.MapMemoryPage(page);
        Sam.CopyBytes(source, (ushort)(sourceRow * 128), TutorialData.RowBuffer, 0, 128);
        Sam.RestoreMemoryPage();
        // Both strides are 128 here, but sourceRow and screenRow differ for
        // the lower half. These are byte offsets from each image's start.
        Sam.CopyBytesToScreen(TutorialData.RowBuffer, 0, (ushort)(screenRow * 128), 128);
    }
}
