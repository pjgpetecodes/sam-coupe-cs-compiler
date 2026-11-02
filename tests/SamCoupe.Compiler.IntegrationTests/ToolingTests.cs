using SamCoupe.Compiler.Cli;
using SamCoupe.Compiler;
using System.Text.Json;
using Xunit;

namespace SamCoupe.Compiler.IntegrationTests;

public sealed class ToolingTests : IDisposable
{
    private readonly string directory = Path.Combine(AppContext.BaseDirectory, "out", "sam-csharp-tests", Guid.NewGuid().ToString("N"));

    public ToolingTests() => Directory.CreateDirectory(directory);

    [Fact]
    public void PerformanceReportResolvesPhysicalSpansAndUsesSourceMapIdentity()
    {
        var sources = new[] { new SourceIdentity("Program.cs", DebugMap.Hash("source")) };
        var map = DebugMap.Resolve("assembly", sources, [], new Dictionary<string, int>());
        var program = new PreparedProgram("assembly", [], sources)
        {
            Performance = new CompilationPerformance(
                [new OptimizationSite("byte-mask-predicate", "Program.cs", 4, 9)],
                [new EmissionSpan("resident", "application", "resident_start", "resident_end"),
                 new EmissionSpan("banked", "array-payload", "bank_start", "bank_end", 8),
                 new EmissionSpan("other-bank", "application", "bank_start", "bank_end", 10)],
                ["cs_prepared_001_draw"])
        };
        var symbols = new Dictionary<string, int>
        {
            ["sam_cli_image_end"] = 0xDFDD,
            ["resident_start"] = 0xBFF0,
            ["resident_end"] = 0xC010,
            ["bank_start"] = 0x3FF0,
            ["bank_end"] = 0x4010
        };
        var report = PerformanceReport.Create(program, "assembly", "sam_draw_rle_masked_sprite:\n RET\n", map, symbols, 23733);
        Assert.Equal(map.BuildId, report.BuildId);
        Assert.Equal(map.AssemblySha256, report.AssemblySha256);
        Assert.Equal(24541, report.ResidentBytes);
        Assert.Equal(35, report.ResidentHeadroomBytes);
        Assert.Equal(23733, report.BinaryBytes);
        Assert.Equal(new[] { 1, 2, 8, 9, 10, 11 }, report.Regions.Select(region => region.PhysicalPage));
        Assert.All(report.Regions, region => Assert.Equal(16, region.Bytes));
        Assert.Contains("sam_draw_rle_masked_sprite", report.RetainedRuntimeSymbols);
        Assert.Single(report.Optimizations);
        Assert.Contains("byte-mask-predicate=1", report.Summary());
        symbols["bank_end"] = 0x3FE0;
        Assert.Throws<InvalidDataException>(() => PerformanceReport.Create(program, "assembly", "", symbols: symbols));
    }

    [Fact]
    public void PerformanceReportRejectsUnresolvedOrOverlappingSpans()
    {
        var program = new PreparedProgram("assembly", [], [])
        {
            Performance = new CompilationPerformance([],
                [new EmissionSpan("one", "slots", "start", "end", 8),
                 new EmissionSpan("two", "slots", "start", "end", 8)], [])
        };
        var symbols = new Dictionary<string, int> { ["sam_cli_image_end"] = 0x9000, ["start"] = 2, ["end"] = 20 };
        Assert.Throws<InvalidDataException>(() => PerformanceReport.Create(program, "assembly", "", symbols: symbols));
        symbols.Remove("end");
        Assert.Throws<InvalidDataException>(() => PerformanceReport.Create(program, "assembly", "", symbols: symbols));
    }

    [Fact]
    public async Task AssemblyOnlyBuildPublishesUnmeasuredReportAndFailedBuildPreservesPreviousGeneration()
    {
        string output = Path.Combine(directory, "out");
        var options = new Options(Path.Combine(directory, "Program.cs"), output, "unused.json", false, false, []);
        var program = new PreparedProgram("cs_main:\n RET\n", [], []);
        await BuildPipeline.PublishAndRun(options, program);
        string reportPath = Path.Combine(output, "program.performance.json");
        string previous = File.ReadAllText(reportPath);
        var report = JsonSerializer.Deserialize<PerformanceReport>(previous, ToolSettings.JsonOptions)!;
        Assert.Null(report.ResidentBytes);
        Assert.Null(report.BinaryBytes);
        Assert.Empty(report.Regions);
        Assert.Contains("layout unmeasured", report.Summary());
        File.WriteAllText(Path.Combine(output, "program.dsk"), "old disk");
        await BuildPipeline.PublishAndRun(options, program with { Assembly = "cs_main:\n NOP\n RET\n" });
        Assert.False(File.Exists(Path.Combine(output, "program.dsk")));
        Assert.NotEqual(previous, File.ReadAllText(reportPath));
        previous = File.ReadAllText(reportPath);
        string stub = Write("unused-tool.bin", "");
        string settings = Write("failing-tools.json", JsonSerializer.Serialize(
            new { pyz80 = "dotnet", samdisk = stub, samdos = stub }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => BuildPipeline.PublishAndRun(
            options with { Build = true, Tools = settings },
            program with { Assembly = "cs_main:\n NOP\n NOP\n RET\n" }));
        Assert.Equal(previous, File.ReadAllText(reportPath));
        Assert.Empty(Directory.GetDirectories(output, ".stage-*"));
    }

    [Fact]
    public void SourceCommentsOptionIsOptIn()
    {
        string source = Write("Program.cs", "");

        Assert.False(Options.Parse([source]).EmitSourceComments);
        Assert.True(Options.Parse([source, "--source-comments"]).EmitSourceComments);
    }

    [Fact]
    public void DiscoversProgramRecursivelyButExcludesGeneratedFiles()
    {
        string active = Write("Program.cs", "class Program {}");
        string helper = Write(Path.Combine("nested folder", "Helper.cs"), "class Helper {}");
        Write(Path.Combine("obj", "Generated.cs"), "invalid");
        Write(Path.Combine("bin", "Other.cs"), "invalid");
        Write(Path.Combine("out", "Output.cs"), "invalid");
        Assert.Equal(new[] { active, helper }.Order(StringComparer.Ordinal), SourceDiscovery.Find(active));
    }

    [Fact]
    public void RejectsNonCSharpAndMissingSource()
    {
        Assert.Throws<ArgumentException>(() => SourceDiscovery.Find(Write("Notes.txt", "")));
        Assert.Throws<ArgumentException>(() => SourceDiscovery.Find(Path.Combine(directory, "Missing.cs")));
        Assert.Throws<ArgumentException>(() => SourceDiscovery.Find(Write(Path.Combine("obj", "Generated.cs"), "")));
    }

    [Fact]
    public void ResolvesPhysicalPagesAndRejectsMissingLabels()
    {
        var symbols = DebugMap.ReadSymbols("8000=point1\nC020=point2\n");
        var identities = new[] { new SourceIdentity("Program.cs", DebugMap.Hash("source")) };
        var points = new[]
        {
            new SourcePoint("point1", "Program.cs", 4, 1, 4, 10, "Main"),
            new SourcePoint("point2", "Program.cs", 5, 1, 5, 10, "Main")
        };
        var map = DebugMap.Resolve("assembly", identities, points, symbols);
        Assert.Equal(1, map.Points[0].PhysicalPage);
        Assert.Equal(2, map.Points[1].PhysicalPage);
        Assert.Equal(0x20, map.Points[1].PageOffset);
        Assert.Throws<InvalidDataException>(() => DebugMap.Resolve("assembly", identities, points,
            DebugMap.ReadSymbols("8000=point1")));
        Assert.NotEqual(map.BuildId, DebugMap.Resolve("changed", identities, points, symbols).BuildId);
    }

    [Fact]
    public void InvalidAssemblerMapsAreErrors()
    {
        Assert.Throws<InvalidDataException>(() => DebugMap.ReadSymbols("invalid"));
        Assert.Throws<InvalidDataException>(() => DebugMap.ReadSymbols("8000=same\n8010=same"));
    }

    [Fact]
    public void SymbolExportRetainsAliasedSequencePointLabelsWithoutPickleExecution()
    {
        var symbols = DebugMap.ReadSymbolExport("(dp0\nVmethod\np1\nI32768\nsVpoint\np2\nI32768\ns.");
        Assert.Equal(symbols["method"], symbols["point"]);
        Assert.Throws<InvalidDataException>(() => DebugMap.ReadSymbolExport("cos\nsystem\n."));
        Assert.Throws<InvalidDataException>(() => DebugMap.ReadSymbolExport("(dp0\nVpoint\np1\nI32768\n"));
    }

    [Fact]
    public void ToolConfigurationRejectsMissingRequiredFiles()
    {
        Assert.Throws<ArgumentException>(() => ToolSettings.Load(Path.Combine(directory, "missing.json"), true));
        string settings = Write("tools.json", "{\"pyz80\":\"pyz80\",\"samdisk\":\"missing\",\"samdos\":\"missing\"}");
        Assert.Throws<ArgumentException>(() => ToolSettings.Load(settings, false));
    }

    [Fact]
    public void RelativeToolPathsResolveAgainstConfigurationNotCurrentDirectory()
    {
        string executable = Write(Path.Combine("tools", "samdisk.exe"), "");
        string dos = Write(Path.Combine("tools", "samdos.raw"), "");
        string emulator = Write(Path.Combine("tools", "simcoupe.exe"), "");
        string settings = Write("tools.json", """
            {"pyz80":"pyz80","samdisk":"tools\\samdisk.exe","samdos":"tools\\samdos.raw","simcoupe":"tools\\simcoupe.exe"}
            """);
        var tools = ToolSettings.Load(settings, true);
        Assert.Equal(executable, tools.Samdisk);
        Assert.Equal(dos, tools.Samdos);
        Assert.Equal(emulator, tools.Simcoupe);
        Assert.Equal("pyz80", tools.Pyz80);
    }

    [Fact]
    public async Task RunningProgramLeasePreventsReplacingItsArtifacts()
    {
        string output = Path.Combine(directory, "out");
        Directory.CreateDirectory(output);
        string assembly = Path.Combine(output, "program.asm");
        File.WriteAllText(assembly, "previous build");
        using var lease = new FileStream(Path.Combine(output, "launch.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var options = new Options(Path.Combine(directory, "Program.cs"), output, "unused.json", false, false, []);
        var program = new PreparedProgram("new build", [], []);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => BuildPipeline.PublishAndRun(options, program));
        Assert.Contains("already building or running", error.Message);
        Assert.Equal("previous build", File.ReadAllText(assembly));
    }

    [Fact]
    public void LaunchBuiltRejectsEditedSourcesAndTamperedArtifacts()
    {
        string output = Path.Combine(directory, "built");
        string assembly = Write(Path.Combine("built", "program.asm"), "assembly");
        string disk = Write(Path.Combine("built", "program.dsk"), "disk bytes");
        string installedRuntime = Path.Combine(AppContext.BaseDirectory, "runtime", "sam", "hardware.asm");
        string runtime = Write(Path.Combine("built", "sam-runtime.asm"),
            RuntimeTreeShaker.Shake("assembly", File.ReadAllText(installedRuntime), emitSourceComments: true));
        var sources = new[] { new SourceIdentity("Program.cs", DebugMap.Hash("source")) };
        var map = DebugMap.Resolve("assembly", sources, [], new Dictionary<string, int>()) with
        {
            EmitSourceComments = true,
            DiskSha256 = DebugMap.HashFile(disk),
            RuntimeSha256 = DebugMap.HashFile(runtime)
        };
        Write(Path.Combine("built", "program.source-map.json"), JsonSerializer.Serialize(map, ToolSettings.JsonOptions));
        BuildPipeline.ValidatePublished(output, sources);
        Assert.Throws<InvalidOperationException>(() => BuildPipeline.ValidatePublished(output,
            [new SourceIdentity("Program.cs", DebugMap.Hash("edited source"))]));
        File.WriteAllText(disk, "different disk");
        Assert.Throws<InvalidOperationException>(() => BuildPipeline.ValidatePublished(output, sources));
        File.WriteAllText(disk, "disk bytes");
        File.WriteAllText(assembly, "different assembly");
        Assert.Throws<InvalidOperationException>(() => BuildPipeline.ValidatePublished(output, sources));
        File.WriteAllText(assembly, "assembly");
        File.AppendAllText(runtime, "\n; tampered runtime");
        Assert.Throws<InvalidOperationException>(() => BuildPipeline.ValidatePublished(output, sources));
    }

    [Theory]
    [InlineData(180224, true)]
    [InlineData(180226, false)]
    [InlineData(204804, false)]
    public void BootLoadChecksActualDirectoryExtentIncludingEmptyDumpTail(int length, bool valid)
    {
        var image = new byte[819200];
        image[0] = 19;
        "AUTO"u8.CopyTo(image.AsSpan(1));
        image[13] = 4;
        image[14] = 1;
        image[236] = 1;
        image[238] = 0x80;
        image[239] = (byte)(length / 16384);
        image[240] = (byte)length;
        image[241] = (byte)((length % 16384) >> 8);
        image[242] = 2;
        image[244] = 0x80;
        int first = 4 * 20 * 512;
        image[first] = 19;
        image[first + 1] = image[240];
        image[first + 2] = image[241];
        image[first + 4] = 0x80;
        image[first + 7] = image[239];
        image[first + 8] = 1;
        if (valid)
        {
            SamDosLoadContract.Validate(image);
            image[first + 1]++;
            Assert.Throws<InvalidDataException>(() => SamDosLoadContract.Validate(image));
        }
        else
        {
            Assert.Throws<InvalidDataException>(() => SamDosLoadContract.Validate(image));
        }
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(directory, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
