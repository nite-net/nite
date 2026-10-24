using System.Diagnostics;

namespace Nite.Cli.Infrastructure;

internal static class ExternalProcess
{
    /// <summary>
    /// Runs a command attached to this console (so colors, prompts and Ctrl+C behave as if it was
    /// run directly) and returns its exit code.
    /// </summary>
    public static async Task<int> RunAsync(string fileName, IEnumerable<string> arguments, string workingDirectory,
        IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(fileName)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        foreach (var (key, value) in environment ?? new Dictionary<string, string>())
        {
            start.Environment[key] = value;
        }

        // Ctrl+C reaches the child directly (same console); we only wait for it to finish.
        ConsoleCancelEventHandler keepAlive = (_, e) => e.Cancel = true;
        Console.CancelKeyPress += keepAlive;
        try
        {
            using var process = Process.Start(start) ?? throw new CliException($"Could not start {fileName}.");
            await process.WaitForExitAsync(CancellationToken.None);
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new CliException($"Could not find [bold]{fileName}[/]. Is the .NET SDK installed and on PATH?");
        }
        finally
        {
            Console.CancelKeyPress -= keepAlive;
        }
    }

    /// <summary>The dotnet executable running this tool, so `nite watch` uses the same SDK install.</summary>
    public static string Dotnet =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host
        : Environment.ProcessPath is { } self && Path.GetFileNameWithoutExtension(self) == "dotnet" ? self
        : "dotnet";
}
