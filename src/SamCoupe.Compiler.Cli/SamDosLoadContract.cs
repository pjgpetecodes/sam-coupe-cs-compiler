namespace SamCoupe.Compiler.Cli;

/// <summary>Checks the on-disk load extent, which can differ from pyz80's sparse object length.</summary>
public static class SamDosLoadContract
{
    public static void Validate(byte[] image)
    {
        if (image.Length != 80 * 2 * 10 * 512)
            throw new InvalidDataException("Expected an uncompressed 80-track MGT disk.");
        int count = 0;
        for (int index = 0; index < 80; index++)
        {
            var entry = image.AsSpan((index / 20 * 20 * 512) + index % 20 * 256, 256);
            if ((entry[0] & 31) != 19 || !entry.Slice(1, 4).SequenceEqual("AUTO"u8))
                continue;
            count++;
            int page = entry[236] & 31;
            int address = entry[237] | entry[238] << 8;
            int length = entry[239] * 0x4000 + entry[240] + (entry[241] << 8);
            if (page != 1 || address != 0x8000 || length <= 0 ||
                page * 0x4000 + address - 0x8000 + length > 12 * 0x4000)
                throw new InvalidDataException(
                    $"SAMDOS 256 KB boot CODE load reaches its reserved high-memory envelope " +
                    $"(page {page}, address {address:X4}, length {length}). " +
                    "Initialize high-page data after native entry instead of loading it contiguously.");
            if (entry[242] != 2 || entry[243] != 0 || entry[244] != 0x80)
                throw new InvalidDataException("SAMDOS AUTO execution must target resident page 1 at 8000.");
            int side = entry[13] >> 7;
            int track = entry[13] & 127;
            int sector = entry[14];
            if (track >= 80 || sector is < 1 or > 10)
                throw new InvalidDataException("Invalid AUTO file's first sector.");
            var header = image.AsSpan((track * 20 + side * 10 + sector - 1) * 512, 9);
            int headerLength = header[7] * 0x4000 + header[1] + (header[2] << 8);
            if (header[0] != 19 || header[8] != page || headerLength != length ||
                (header[3] | header[4] << 8) != address)
                throw new InvalidDataException("AUTO directory and CODE sector header disagree.");
        }
        if (count != 1)
            throw new InvalidDataException("Expected exactly one AUTO CODE file.");
    }
}
