using System.Text.RegularExpressions;
using SamCoupe.Compiler;

namespace SamCoupe.Compiler.Cli;

public sealed record PerformanceRegion(string Name, string Category, int PhysicalPage, int PageOffset, int Bytes);

public sealed record PerformanceReport(
    int Version,
    string BuildId,
    string AssemblySha256,
    IReadOnlyList<SourceIdentity> Sources,
    int? ResidentBytes,
    int? ResidentHeadroomBytes,
    int? BinaryBytes,
    IReadOnlyList<PerformanceRegion> Regions,
    IReadOnlyList<OptimizationSite> Optimizations,
    IReadOnlyList<string> PreparedKernels,
    IReadOnlyList<string> RetainedRuntimeSymbols)
{
    public static PerformanceReport Create(PreparedProgram program, string assembly, string runtime,
        DebugMap? map = null, IReadOnlyDictionary<string, int>? symbols = null, int? binaryBytes = null)
    {
        var metadata = program.Performance ?? new CompilationPerformance([], [], []);
        var regions = new List<PerformanceRegion>();
        int? residentBytes = null;
        if (symbols is not null)
        {
            if (!symbols.TryGetValue("sam_cli_image_end", out int end) || end < 0x8000 || end > 0xE000)
                throw new InvalidDataException("Performance report cannot resolve the resident image end.");
            residentBytes = end - 0x8000;
            foreach (var span in metadata.Spans)
            {
                if (!symbols.TryGetValue(span.StartLabel, out int start) ||
                    !symbols.TryGetValue(span.EndLabel, out int stop) || stop < start ||
                    (span.BankPage is null && (start < 0x8000 || stop > end)) ||
                    (span.BankPage is not null && (start < 0 || stop > 0x8000)))
                    throw new InvalidDataException($"Performance report cannot resolve span '{span.Name}'.");
                while (start < stop)
                {
                    int page = span.BankPage is int bank ? bank + start / 0x4000 : 1 + (start - 0x8000) / 0x4000;
                    int offset = start & 0x3FFF;
                    int length = Math.Min(stop - start, 0x4000 - offset);
                    regions.Add(new PerformanceRegion(span.Name, span.Category, page, offset, length));
                    start += length;
                }
            }
            foreach (var page in regions.GroupBy(region => region.PhysicalPage))
            {
                int occupiedEnd = 0;
                foreach (var region in page.OrderBy(region => region.PageOffset))
                {
                    if (region.PageOffset < occupiedEnd)
                        throw new InvalidDataException($"Performance regions overlap on physical page {page.Key}.");
                    occupiedEnd = region.PageOffset + region.Bytes;
                }
            }
        }

        string hash = DebugMap.Hash(assembly);
        string buildId = map?.BuildId ??
            DebugMap.Hash(hash + string.Join("", program.Sources.Select(source => source.File + source.Sha256)));
        var retained = Regex.Matches(runtime, @"(?m)^\s*(sam_\w+):")
            .Select(match => match.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToArray();
        return new PerformanceReport(1, buildId, hash, program.Sources, residentBytes,
            residentBytes is int bytes ? 0x6000 - bytes : null, binaryBytes,
            regions, metadata.Optimizations, metadata.PreparedKernels, retained);
    }

    public string Summary()
    {
        string layout = ResidentBytes is int bytes
            ? $"resident {bytes:N0}/24,576 bytes; {ResidentHeadroomBytes:N0} bytes below E000; binary {BinaryBytes:N0} bytes"
            : "layout unmeasured (assembly only)";
        string rewrites = string.Join(", ", Optimizations.GroupBy(site => site.Kind)
            .OrderBy(group => group.Key, StringComparer.Ordinal).Select(group => $"{group.Key}={group.Count()}"));
        return $"Performance: {layout}; local rewrites: {(rewrites.Length == 0 ? "none" : rewrites)}.";
    }
}
