namespace SamCoupe.Compiler.Cli;

public sealed record Options(string ActiveSource, string Output, string Tools, bool Build, bool Run, IReadOnlyList<string> Shared)
{
    public bool LaunchBuilt { get; init; }

    public static Options Parse(string[] args)
    {
        if (args.Length == 0)
            throw new ArgumentException("Usage: sam-csharp <active.cs> [--build | --run] [--source-comments] [--tools tools.local.json] [--output directory] [--source shared.cs]");
        string active = Path.GetFullPath(args[0]);
        string output = Path.Combine(Path.GetDirectoryName(active)!, "out");
        string tools = Path.Combine(Environment.CurrentDirectory, "tools.local.json");
        bool build = false, run = false, launchBuilt = false, emitSourceComments = false;
        var shared = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--build": build = true; break;
                case "--run": run = true; build = true; break;
                case "--source-comments": emitSourceComments = true; break;
                case "--launch-built": launchBuilt = true; break;
                case "--tools": tools = Path.GetFullPath(Value(args, ref i)); break;
                case "--output": output = Path.GetFullPath(Value(args, ref i)); break;
                case "--source": shared.Add(Path.GetFullPath(Value(args, ref i))); break;
                default: throw new ArgumentException($"Unknown option: {args[i]}");
            }
        }
        if (launchBuilt && build)
            throw new ArgumentException("--launch-built cannot be combined with --build or --run.");
        return new(active, output, tools, build, run, shared)
        {
            LaunchBuilt = launchBuilt,
            EmitSourceComments = emitSourceComments
        };
    }

    public bool EmitSourceComments { get; init; }

    private static string Value(string[] args, ref int index)
    {
        if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
            throw new ArgumentException($"Missing value for {args[index - 1]}.");
        return args[index];
    }
}
