// Blog 2, lesson 3-2: the second opaque-copy example; source: assembly\blog2\3-2.asm.
using SamCoupe.Sdk;

static class Program
{
    static void Main()
    {
        // This source's 512-byte payload is still 16 packed bytes x 32 rows (32x32 pixels).
        // TutorialData is the source byte array, not an image loaded by a managed runtime.
        // Each byte holds two four-bit palette indices: left pixel first, right pixel second.
        // The copy helper writes each whole byte, then advances to the next 128-byte screen row.
        // The source rows are adjacent (16 bytes apart), but screen rows are 128 bytes apart,
        // so the helper must skip the unused 112 destination bytes after each sprite row.
        // It preserves the row-copy result of the assembly variant; transparency comes next.
        // Opaque copying replaces even the background-coloured pixels. Offset 0 is top left.
        Sam.DrawOpaqueSprite(TutorialData.OpaqueSprite, 0);
        // Do not return immediately: let the reader inspect the sprite before restoring BASIC.
        Sam.WaitForKey();
    }
}
