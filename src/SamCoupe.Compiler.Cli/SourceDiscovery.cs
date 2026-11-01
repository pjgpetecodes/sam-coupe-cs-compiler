namespace SamCoupe.Compiler.Cli;

public static class SourceDiscovery
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "out", ".git", ".vs"
    };

    public static IReadOnlyList<string> Find(string activeSource)
    {
        string file = Path.GetFullPath(activeSource);
        if (!File.Exists(file) || !Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Open a saved C# source (.cs) from the program folder.");
        string root = Path.GetDirectoryName(file)!;
        if (Excluded.Contains(Path.GetFileName(root))
            || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Select a source in a program folder, not a generated or linked directory.");
        var files = new List<string>();
        Visit(root, files);
        if (!files.Contains(file, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("The active source is in an excluded or linked directory.");
        files.Sort(StringComparer.Ordinal);
        return files;
    }

    private static void Visit(string directory, List<string> files)
    {
        foreach (string file in Directory.EnumerateFiles(directory))
            if (Path.GetExtension(file).Equals(".cs", StringComparison.OrdinalIgnoreCase)
                && (File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                files.Add(Path.GetFullPath(file));
        foreach (string child in Directory.EnumerateDirectories(directory))
            if (!Excluded.Contains(Path.GetFileName(child))
                && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0)
                Visit(child, files);
    }
}
