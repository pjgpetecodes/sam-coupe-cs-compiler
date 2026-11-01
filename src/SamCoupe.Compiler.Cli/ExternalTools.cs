using System.ComponentModel;
using System.Diagnostics;

namespace SamCoupe.Compiler.Cli;

public static class ExternalTools
{
    public static async Task Run(string executable, IEnumerable<string> arguments, string directory)
    {
        using Process process = Start(executable, arguments, directory);
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(executable)} failed with exit code {process.ExitCode}.");
    }

    public static Process Start(string executable, IEnumerable<string> arguments, string directory)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false
        };
        foreach (string argument in arguments)
            start.ArgumentList.Add(argument);
        try
        {
            return Process.Start(start)
                ?? throw new InvalidOperationException($"Could not start {executable}.");
        }
        catch (Win32Exception error)
        {
            throw new InvalidOperationException($"Could not start {executable}: {error.Message}. Check the configured path and PATH.", error);
        }
    }
}
