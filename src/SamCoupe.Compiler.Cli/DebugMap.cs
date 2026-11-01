using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SamCoupe.Compiler.Cli;

public sealed record SourcePoint(string Label, string File, int Line, int Column, int EndLine, int EndColumn, string Method);
public sealed record ResolvedPoint(string Label, string File, int Line, int Column, int EndLine, int EndColumn,
    string Method, int LogicalAddress, int PhysicalPage, int PageOffset);
public sealed record SourceIdentity(string File, string Sha256);
public sealed record DebugMap(int Version, string BuildId, string AssemblySha256,
    IReadOnlyList<SourceIdentity> Sources, IReadOnlyList<ResolvedPoint> Points)
{
    public string DiskSha256 { get; init; } = "";
    public string RuntimeSha256 { get; init; } = "";
    public bool EmitSourceComments { get; init; }
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    public static Dictionary<string, int> ReadSymbols(string mapText)
    {
        var symbols = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string line in mapText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = line.IndexOf('=');
            if (separator <= 0 || separator == line.Length - 1
                || !int.TryParse(line.AsSpan(0, separator), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int address))
                throw new InvalidDataException($"Invalid pyz80 map line: {line}");
            if (!symbols.TryAdd(line[(separator + 1)..], address))
                throw new InvalidDataException($"Duplicate pyz80 symbol: {line[(separator + 1)..]}");
        }
        return symbols;
    }

    public static Dictionary<string, int> ReadSymbolExport(string text)
    {
        // pyz80's protocol-0 export retains aliases that its address map drops.
        // Parse only the flat numeric dictionary format; never deserialize pickle.
        using var reader = new StringReader(text);
        if (reader.ReadLine() != "(dp0")
            throw new InvalidDataException("Unsupported pyz80 symbol export header.");
        var symbols = new Dictionary<string, int>(StringComparer.Ordinal);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line is "." or "s.")
                return symbols;
            if (line.StartsWith('s'))
                line = line[1..];
            if (!line.StartsWith('V') || line.Length < 2)
                throw new InvalidDataException($"Unsupported pyz80 symbol export entry: {line}");
            string name = line[1..];
            string memo = reader.ReadLine() ?? throw new InvalidDataException("Missing symbol memo.");
            if (!memo.StartsWith('p') || !int.TryParse(memo.AsSpan(1), out _))
                throw new InvalidDataException("Invalid symbol memo.");
            string value = reader.ReadLine() ?? throw new InvalidDataException("Missing symbol value.");
            if (value.StartsWith('I') && int.TryParse(value.AsSpan(1), CultureInfo.InvariantCulture, out int address))
                symbols.Add(name, address);
            else if (!(value.StartsWith('F') && double.TryParse(value.AsSpan(1), CultureInfo.InvariantCulture, out _))
                && !(value.StartsWith('L') && long.TryParse(value.AsSpan(1).TrimEnd('L'), CultureInfo.InvariantCulture, out _)))
                throw new InvalidDataException($"Unsupported pyz80 symbol value: {value}");
        }
        throw new InvalidDataException("Incomplete pyz80 symbol export.");
    }

    public static DebugMap Resolve(string assembly, IReadOnlyList<SourceIdentity> sources,
        IEnumerable<SourcePoint> points, IReadOnlyDictionary<string, int> symbols)
    {
        var resolved = new List<ResolvedPoint>();
        var bankedCodePages = FindBankedCodePages(assembly);
        foreach (SourcePoint point in points)
        {
            if (!symbols.TryGetValue(point.Label, out int address))
                throw new InvalidDataException($"Source map label missing after assembly: {point.Label}");
            if (address is >= 0 and < 0x4000)
            {
                if (!bankedCodePages.TryGetValue(point.Label, out int page))
                    throw new InvalidDataException($"Source location is not in a declared code page: {point.Label} at {address:X4}");
                resolved.Add(new(point.Label, point.File, point.Line, point.Column, point.EndLine, point.EndColumn,
                    point.Method, address, page, address));
                continue;
            }

            if (address < 0x8000 || address >= 0xE000)
                throw new InvalidDataException($"Source location outside resident program: {point.Label} at {address:X4}");
            // DUMP 1,0 loads the two resident pages at 0x8000 and 0xC000.
            resolved.Add(new(point.Label, point.File, point.Line, point.Column, point.EndLine, point.EndColumn,
                point.Method, address, 1 + (address - 0x8000) / 0x4000, address & 0x3FFF));
        }
        string assemblyHash = Hash(assembly);
        string identity = assemblyHash + string.Join("", sources.Select(s => s.File + s.Sha256));
        return new(1, Hash(identity), assemblyHash, sources, resolved);
    }

    private static Dictionary<string, int> FindBankedCodePages(string assembly)
    {
        var pages = new Dictionary<string, int>(StringComparer.Ordinal);
        int? currentCodePage = null;
        foreach (string line in assembly.Split('\n'))
        {
            string text = line.Trim();
            if (text.StartsWith("DUMP ", StringComparison.OrdinalIgnoreCase))
            {
                string[] fields = text[5..].Split(',', StringSplitOptions.TrimEntries);
                currentCodePage = fields.Length == 2 &&
                    fields[1] == "0" &&
                    int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int page)
                    ? page
                    : null;
                continue;
            }

            if (currentCodePage is int codePage && text.StartsWith("cs_seq_", StringComparison.Ordinal) &&
                text.EndsWith(':'))
            {
                pages[text[..^1]] = codePage;
            }
        }

        return pages;
    }
}
