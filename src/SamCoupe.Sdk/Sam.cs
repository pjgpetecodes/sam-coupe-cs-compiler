namespace SamCoupe.Sdk;

public readonly struct SpriteFrame { }

[AttributeUsage(AttributeTargets.Field)]
public sealed class BankedDataAttribute(int page) : Attribute
{
    public int Page { get; } = page;
}

public static class Sam
{
    public static void ClearScreen() => throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void WriteScreenByte(ushort offset, byte value) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void Print(string text) => throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void PrintChar(char value) => throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void WaitForKey() => throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void DrawOpaqueSprite(byte[] sprite, ushort offset) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void BuildInterleavedSprite(byte[] pixels, byte[] masks) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static SpriteFrame SelectSpriteFrame(byte[] frames, byte index) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void DrawBuiltMaskedSprite(ushort offset, byte widthBytes, byte height) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void DrawMaskedSprite(SpriteFrame frame, ushort offset, byte widthBytes, byte height) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void DrawRleMaskedSprite(SpriteFrame frame, ushort offset, byte widthBytes, byte height) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void PrepareFixedSpriteRenderer(byte[] frames, ushort offset, byte widthBytes, byte rows) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void DrawPreparedSprite(byte frame) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static byte ReadKeyboardRow(ushort port) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static bool IsKeyPressedInRow(byte row, byte mask) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void FillScreen(byte value) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void ClearSprite(ushort offset, byte widthBytes, byte height) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void FillStripedBackground(byte first, byte second) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void SetPaletteEntry(byte index, byte value) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void SetBorderColor(byte index) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void WriteSaaRegister(byte register, byte value) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void LoadTutorialPalette(byte[] palette) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void WaitForFrames(byte count) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static bool IsKeyPressed(ushort port, byte mask) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static bool AnySolidPixelInPackedRow(byte[] mask, ushort offset, byte firstPixel, byte pixelCount) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static bool AnySolidPixelInPackedScreenColumn(byte[] mask, byte x, byte firstY, byte pixelCount) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static bool AnySolidPixelInPackedRectangle(
        byte[] mask, ushort offset, byte firstPixel, byte pixelWidth, byte rows) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void MapMemoryPage(byte page) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void RestoreMemoryPage() =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void CopyBytes(
        byte[] source,
        ushort sourceOffset,
        byte[] destination,
        ushort destinationOffset,
        ushort count) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void DecodeRleBackground(
        byte[] source,
        byte sourcePage,
        byte[] destination,
        byte destinationPage) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void ExpandByteRuns(
        byte[] source,
        byte sourcePage,
        byte[] destination,
        byte destinationPage) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void ShiftMaskedSpriteOnePixel(byte[] source, byte[] destination) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void CopyBytesToScreen(byte[] source, ushort sourceOffset, ushort screenOffset, ushort count) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void CopyBankedBytesToScreen(
        byte[] source,
        byte sourcePage,
        ushort sourceOffset,
        ushort screenOffset,
        byte widthBytes,
        byte rowCount) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void ComposeTransparentImage(
        byte[] source,
        byte sourcePage,
        byte[] destination,
        byte destinationPage,
        ushort sourceOffset,
        ushort destinationOffset,
        byte widthBytes,
        byte rowCount,
        byte sourceStride) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void DrawPackedOutline(
        byte[] destination,
        byte destinationPage,
        ushort screenOffset,
        byte widthBytes,
        byte height,
        byte topAndBottom,
        byte leftBorder,
        byte rightBorder) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void FlipScreen() =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void BeginDoubleBuffer() =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static void BeginDoubleBuffer(byte firstScreenPage, byte secondScreenPage) =>
        throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

    public static class Rom
    {
        public static void ClearScreen() => throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

        public static void WaitForKey() => throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

        public static void Print(string text) => throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");

        public static void PrintChar(char value) => throw new PlatformNotSupportedException("SAM APIs are compiler intrinsics.");
    }
}
