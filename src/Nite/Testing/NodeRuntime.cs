using System.Diagnostics;
using Nite.Packages;

namespace Nite.Testing;

/// <summary>
/// The Node.js that runs Vitest: <c>NITE_NODE_PATH</c>, or <c>node</c> on <c>PATH</c>. NiteFx
/// never needs Node to build or run an app; only tests do, because Vitest is a Node program.
/// </summary>
public sealed record NodeRuntime(string Path, SemVersion Version)
{
    public const string PathVariable = "NITE_NODE_PATH";

    /// <summary>Finds Node and asks it for its version; null when there is none.</summary>
    public static async Task<NodeRuntime?> FindAsync(CancellationToken cancellationToken = default)
    {
        var path = Environment.GetEnvironmentVariable(PathVariable) is { Length: > 0 } configured ? configured : OnPath();
        if (path is null)
        {
            return null;
        }

        var start = new ProcessStartInfo(path, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        try
        {
            using var process = Process.Start(start);
            if (process is null)
            {
                return null;
            }
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            return SemVersion.TryParse(output.Trim().TrimStart('v'), out var version) ? new NodeRuntime(path, version) : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? OnPath()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "node.exe", "node.cmd" } : ["node"];
        return (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(directory => names.Select(name => System.IO.Path.Combine(directory, name)))
            .FirstOrDefault(File.Exists);
    }
}
