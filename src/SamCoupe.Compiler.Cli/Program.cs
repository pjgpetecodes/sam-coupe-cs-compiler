using System.Text.Json;
using SamCoupe.Compiler;

namespace SamCoupe.Compiler.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Options options = Options.Parse(args);
            var paths = SourceDiscovery.Find(options.ActiveSource).ToList();
            foreach (string shared in options.Shared)
            {
                if (!File.Exists(shared) || !Path.GetExtension(shared).Equals(".cs", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Shared source is missing or not C#: {shared}");
                if (!paths.Contains(shared, StringComparer.OrdinalIgnoreCase))
                    paths.Add(shared);
            }
            paths.Sort(StringComparer.Ordinal);
            SourceFile[] sources = paths.Select(path => new SourceFile(path, File.ReadAllText(path))).ToArray();
            var identities = sources.Select(source => new SourceIdentity(source.Path, DebugMap.Hash(source.Text))).ToArray();
            if (options.LaunchBuilt)
            {
                await BuildPipeline.LaunchPublished(options, identities);
                return 0;
            }
            CompileResult result = new CSharpCompiler().Compile(sources,
                new CompileOptions { EmitSourceComments = options.EmitSourceComments });
            foreach (var diagnostic in result.Diagnostics)
                Console.Error.WriteLine($"{diagnostic.File}({diagnostic.Line},{diagnostic.Column}): error {diagnostic.Code}: {diagnostic.Message}");
            if (!result.Success)
                return 1;
            var points = result.SequencePoints.Select(point =>
                new SourcePoint(point.Label, point.File, point.Line, point.Column, point.EndLine, point.EndColumn, point.Method)).ToArray();
            await BuildPipeline.PublishAndRun(options, new(result.AssemblyText, points, identities)
            { Performance = result.Performance });
            return 0;
        }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidOperationException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"SAM C# build/run failed: {error.Message}");
            return 1;
        }
    }
}
