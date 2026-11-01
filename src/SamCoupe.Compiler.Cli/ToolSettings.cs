using System.Text.Json;

namespace SamCoupe.Compiler.Cli;

public sealed record ToolSettings(string Pyz80, string Samdisk, string Samdos, string Simcoupe)
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static ToolSettings Load(string path, bool run)
    {
        if (!File.Exists(path))
            throw new ArgumentException($"Tool settings are missing: {path}. Copy tools.example.json to tools.local.json and set your tool paths.");
        var tools = JsonSerializer.Deserialize<ToolSettings>(File.ReadAllText(path), JsonOptions)
            ?? throw new ArgumentException("Tool settings must be a JSON object.");
        if (string.IsNullOrWhiteSpace(tools.Pyz80))
            throw new ArgumentException("Tool settings require a pyz80 executable name or path.");
        string directory = Path.GetDirectoryName(Path.GetFullPath(path))!;
        tools = tools with
        {
            Pyz80 = tools.Pyz80.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0
                ? ResolveFile(tools.Pyz80, directory) : tools.Pyz80,
            Samdisk = ResolveFile(tools.Samdisk, directory),
            Samdos = ResolveFile(tools.Samdos, directory),
            Simcoupe = ResolveFile(tools.Simcoupe, directory)
        };
        RequireFile(tools.Samdisk, "samdisk");
        RequireFile(tools.Samdos, "samdos");
        if (run)
            RequireFile(tools.Simcoupe, "simcoupe");
        return tools;
    }

    private static string ResolveFile(string? value, string directory) =>
        string.IsNullOrWhiteSpace(value) ? string.Empty : Path.GetFullPath(value, directory);

    private static void RequireFile(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || !File.Exists(value))
            throw new ArgumentException($"Required {name} file is missing: {value}. Update tools.local.json.");
    }
}
