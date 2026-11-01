// Blog 2, lesson 2: direct MODE 4 screen bytes; source: assembly\blog2\2.asm.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // MODE 4 packs two palette indices per byte: high nibble left, low nibble right.
        // A nibble is four bits, so each pixel stores a number 0..15. The palette converts
        // that number to a displayed colour; the byte itself does not store an RGB colour.
        // The 0x prefix denotes hexadecimal: FF has index F in both nibbles, 44 index 4.
        // Offsets are screen-relative; +1 moves two pixels right, +0x80 moves one row down.
        // There are 256 pixels across, hence 256/2 = 128 (0x80) bytes per row.
        // The first argument is a byte offset, not a pixel coordinate or a CPU address.
        // For example 0081 is row 1, byte 1; 0181 is row 3, byte 1.
        // These eight writes form a four-pixel-wide, four-row square using indices F and 4.
        Sam.WriteScreenByte(0x0000, 0xFF);
        Sam.WriteScreenByte(0x0080, 0x44);
        Sam.WriteScreenByte(0x0001, 0xFF);
        Sam.WriteScreenByte(0x0081, 0x44);
        Sam.WriteScreenByte(0x0100, 0xFF);
        Sam.WriteScreenByte(0x0180, 0x44);
        Sam.WriteScreenByte(0x0101, 0xFF);
        Sam.WriteScreenByte(0x0181, 0x44);

        // Keep the square visible before the runtime restores BASIC's screen.
        // The SDK handles screen paging safely; this lesson concentrates on the byte layout,
        // rather than making Main manipulate the machine's memory registers directly.
        Sam.WaitForKey();
    }
}
