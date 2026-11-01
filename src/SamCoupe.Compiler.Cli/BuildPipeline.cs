using System.Text;
using System.Text.Json;
using SamCoupe.Compiler;

namespace SamCoupe.Compiler.Cli;

public sealed record PreparedProgram(string Assembly, IReadOnlyList<SourcePoint> Points,
    IReadOnlyList<SourceIdentity> Sources);

public static class BuildPipeline
{
    public static async Task PublishAndRun(Options options, PreparedProgram program)
    {
        ToolSettings? tools = options.Build ? ToolSettings.Load(options.Tools, options.Run) : null;
        Directory.CreateDirectory(options.Output);
        string lockPath = Path.Combine(options.Output, "launch.lock");
        using FileStream lease = AcquireLock(lockPath);
        string stage = Path.Combine(options.Output, ".stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        try
        {
            string assembly = program.Assembly.Contains("sam_cli_image_end:", StringComparison.Ordinal)
                ? program.Assembly
                : program.Assembly + "\nsam_cli_image_end:\n";
            await File.WriteAllTextAsync(Path.Combine(stage, "program.asm"), assembly, new UTF8Encoding(false));
            string runtime = Path.Combine(AppContext.BaseDirectory, "runtime", "sam", "hardware.asm");
            if (!File.Exists(runtime))
                throw new InvalidOperationException($"Compiler hardware runtime is missing: {runtime}. Rebuild the compiler.");
            await File.WriteAllTextAsync(Path.Combine(stage, "sam-runtime.asm"),
                RuntimeTreeShaker.Shake(assembly, await File.ReadAllTextAsync(runtime), options.EmitSourceComments),
                new UTF8Encoding(false));

            DebugMap? resolved = null;
            if (tools is not null)
            {
                await ExternalTools.Run(tools.Pyz80,
                    ["--nozip", "--mapfile=program.map", "--exportfile=program.symbols", "--obj=program.bin", "-I", tools.Samdos, "-o", "program.mgt", "program.asm"],
                    stage);
                var symbols = DebugMap.ReadSymbolExport(await File.ReadAllTextAsync(Path.Combine(stage, "program.symbols")));
                if (!symbols.TryGetValue("sam_cli_image_end", out int end) || end > 0xE000 || end <= 0x8000)
                    throw new InvalidDataException($"Program exceeds the resident image budget (0x8000..0xDFFF). Final end: {end:X4}.");
                SamDosLoadContract.Validate(await File.ReadAllBytesAsync(Path.Combine(stage, "program.mgt")));
                resolved = DebugMap.Resolve(assembly, program.Sources, program.Points, symbols);
                await ExternalTools.Run(tools.Samdisk, ["copy", "--force", "program.mgt", "program.dsk"], stage);
                resolved = resolved with
                {
                    EmitSourceComments = options.EmitSourceComments,
                    DiskSha256 = DebugMap.HashFile(Path.Combine(stage, "program.dsk")),
                    RuntimeSha256 = DebugMap.HashFile(Path.Combine(stage, "sam-runtime.asm"))
                };
                await File.WriteAllTextAsync(Path.Combine(stage, "program.source-map.json"),
                    JsonSerializer.Serialize(resolved, ToolSettings.JsonOptions), new UTF8Encoding(false));
            }
            else
            {
                await File.WriteAllTextAsync(Path.Combine(stage, "program.sequence-points.json"),
                    JsonSerializer.Serialize(program, ToolSettings.JsonOptions), new UTF8Encoding(false));
            }

            // Publish a new generation only after all selected build stages succeed.
            foreach (string file in Directory.EnumerateFiles(stage))
                File.Move(file, Path.Combine(options.Output, Path.GetFileName(file)), overwrite: true);
            if (!options.Build)
                RemoveOldBuiltArtifacts(options.Output);
            else
                RemoveIfPresent(Path.Combine(options.Output, "program.sequence-points.json"));
            Console.WriteLine($"Assembly: {Path.Combine(options.Output, "program.asm")}");
            if (resolved is not null)
                Console.WriteLine($"Disk: {Path.Combine(options.Output, "program.dsk")}\nSource map: {Path.Combine(options.Output, "program.source-map.json")}");
            if (options.Run)
                await LaunchEmulator(tools!, options.Output);
        }
        finally
        {
            // This unique stage directory belongs solely to this build.
            Directory.Delete(stage, recursive: true);
        }
    }

    public static async Task LaunchPublished(Options options, IReadOnlyList<SourceIdentity> sources)
    {
        ToolSettings tools = ToolSettings.Load(options.Tools, run: true);
        if (!Directory.Exists(options.Output))
            throw new InvalidOperationException("No built program exists. Build the active program first.");
        using FileStream lease = AcquireLock(Path.Combine(options.Output, "launch.lock"));
        ValidatePublished(options.Output, sources);
        await LaunchEmulator(tools, options.Output);
    }

    public static void ValidatePublished(string output, IReadOnlyList<SourceIdentity> sources)
    {
        string path = Path.Combine(output, "program.source-map.json");
        if (!File.Exists(path))
            throw new InvalidOperationException("Source map is missing. Rebuild the active program.");
        var map = JsonSerializer.Deserialize<DebugMap>(File.ReadAllText(path), ToolSettings.JsonOptions)
            ?? throw new InvalidDataException("Invalid source map. Rebuild the active program.");
        if (map.Version != 1 || map.Sources is null || !map.Sources.SequenceEqual(sources))
            throw new InvalidOperationException("Program sources changed since assembly. Rebuild before launching.");
        string assembly = Path.Combine(output, "program.asm");
        string disk = Path.Combine(output, "program.dsk");
        string runtime = Path.Combine(output, "sam-runtime.asm");
        string installedRuntime = Path.Combine(AppContext.BaseDirectory, "runtime", "sam", "hardware.asm");
        if (!File.Exists(assembly) || !File.Exists(disk) || !File.Exists(runtime) || !File.Exists(installedRuntime)
            || DebugMap.Hash(File.ReadAllText(assembly)) != map.AssemblySha256
            || DebugMap.HashFile(disk) != map.DiskSha256
            || DebugMap.HashFile(runtime) != map.RuntimeSha256
            || DebugMap.Hash(RuntimeTreeShaker.Shake(File.ReadAllText(assembly),
                File.ReadAllText(installedRuntime), map.EmitSourceComments)) != map.RuntimeSha256)
            throw new InvalidOperationException("Built program artifacts changed or are incomplete. Rebuild before launching.");
    }

    private static async Task LaunchEmulator(ToolSettings tools, string output)
    {
        Console.WriteLine("Starting SimCoupe (256 KB). Close this instance before rebuilding the program.");
        await ExternalTools.Run(tools.Simcoupe,
            [Path.Combine(output, "program.dsk"), "-mainmem", "256", "-externalmem", "0", "-autoload", "yes"], output);
    }

    private static FileStream AcquireLock(string path)
    {
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException error)
        {
            throw new InvalidOperationException("This program is already building or running. Close its F5-launched SimCoupe instance and stop its launch before rebuilding.", error);
        }
    }

    private static void RemoveOldBuiltArtifacts(string output)
    {
        foreach (string name in new[] { "program.mgt", "program.dsk", "program.map", "program.symbols", "program.bin", "program.source-map.json" })
            RemoveIfPresent(Path.Combine(output, name));
    }

    private static void RemoveIfPresent(string file)
    {
        if (File.Exists(file))
            File.Delete(file);
    }
}
