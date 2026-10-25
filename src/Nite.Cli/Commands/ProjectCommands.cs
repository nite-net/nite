using Microsoft.Extensions.Logging;
using System.ComponentModel;
using Nite.Build;
using Nite.Cli.Infrastructure;
using Nite.Esbuild;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nite.Cli.Commands;

/// <summary><c>nite build</c>: install, then the production build into wwwroot/_nite.</summary>
internal sealed class BuildCommand : AsyncCommand<ProjectSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, ProjectSettings settings, CancellationToken cancellationToken)
    {
        var project = Tooling.LoadProject(settings.ProjectDirectory);
        using var loggers = Tooling.CreateLoggerFactory(settings.Verbose);

        await Tooling.CreateInstaller(loggers).InstallAsync(project.Directory, project.Config, cancellationToken);
        await using var esbuild = await Tooling.StartEsbuildAsync(project, loggers, cancellationToken);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        // The table below reports the result, so the builder only needs to surface warnings.
        using var warnings = Tooling.CreateLoggerFactory(settings.Verbose, LogLevel.Warning);
        var manifest = await new NiteBuilder(esbuild, warnings.CreateLogger<NiteBuilder>(), warnings, Tooling.Http).BuildAsync(project, cancellationToken);
        var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started);

        if (manifest.Entries.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]warn[/]  Nothing to build: no index.html, no islands and no \"entries\" in nite.json.");
            return 0;
        }

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("Entry").AddColumn("Output").AddColumn(new TableColumn("Size").RightAligned()).AddColumn(new TableColumn("Shared").RightAligned());
        foreach (var (key, entry) in manifest.Entries.OrderBy(e => e.Key == NiteManifest.RuntimeKey).ThenBy(e => e.Key, StringComparer.Ordinal))
        {
            var size = Size(project, entry.File) + entry.Css.Sum(css => Size(project, css));
            var shared = entry.Imports.Sum(chunk => Size(project, chunk));
            table.AddRow(
                key == NiteManifest.RuntimeKey ? "[grey]island loader[/]" : Markup.Escape(key),
                $"[grey]{Markup.Escape(Path.GetRelativePath(project.Directory, Path.Combine(project.OutputDirectory, entry.File)))}[/]",
                Kib(size),
                shared == 0 ? "[grey]-[/]" : Kib(shared));
        }
        AnsiConsole.Write(table);
        foreach (var page in manifest.Html.Values)
        {
            Output.Hint($"html  {Markup.Escape(Path.GetRelativePath(project.Directory, Path.Combine(project.OutputDirectory, page)))}");
        }
        Output.Success($"Built {manifest.Entries.Count} entr{(manifest.Entries.Count == 1 ? "y" : "ies")} in {elapsed.TotalMilliseconds:0} ms");
        return 0;
    }

    private static long Size(NiteProject project, string path)
    {
        var file = new FileInfo(Path.Combine(project.OutputDirectory, path));
        return file.Exists ? file.Length : 0;
    }

    private static string Kib(long bytes) => $"{bytes / 1024.0:0.0} KiB";
}

internal sealed class WatchSettings : ProjectSettings
{
    [CommandOption("--browser-refresh")]
    [Description("Keep dotnet watch's own browser refresh (reloads the page after every C# change). Useful for Razor pages; off by default so single-page apps keep their state.")]
    public bool BrowserRefresh { get; set; }

    [CommandOption("--no-auto-restart")]
    [Description("Let dotnet watch ask (or wait for Ctrl+R) when an edit needs a restart, instead of restarting the app automatically.")]
    public bool NoAutoRestart { get; set; }
}

/// <summary>
/// <c>nite watch</c>: <c>dotnet watch</c> with its browser refresh script turned off, so Nite's
/// own client handles the browser (Fast Refresh, server-update events, reload after restarts),
/// and with edits that need a restart restarting the app. Anything after <c>--</c> goes to dotnet watch.
/// </summary>
/// <remarks>
/// Without <c>DOTNET_WATCH_RESTART_ON_RUDE_EDIT</c>, an interactive dotnet watch treats some
/// edits that cannot take effect as warnings: adding an endpoint to top-level code in Program.cs
/// reports ENC0118 ("might not have any effect until the application is restarted"), applies an
/// update that changes nothing (Main already ran), and waits for Ctrl+R. The page then calls an
/// endpoint that does not exist. With the variable set, it restarts, and Nite reloads the page.
/// </remarks>
internal sealed class WatchCommand : AsyncCommand<WatchSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, WatchSettings settings, CancellationToken cancellationToken)
    {
        var directory = settings.ProjectDirectory;
        if (!Directory.Exists(directory) || !Directory.EnumerateFiles(directory, "*.csproj").Any())
        {
            throw new CliException($"No .csproj in {Markup.Escape(directory)}. Run nite watch from the project folder or pass --dir.");
        }

        var environment = WatchEnvironment(settings, System.Environment.GetEnvironmentVariable);
        var arguments = context.Remaining.Raw.ToList();
        var shown = environment.Count == 0 ? "" : "  (" + string.Join(' ', environment.Select(e => $"{e.Key}={e.Value}")) + ")";
        Output.Hint($"dotnet watch {string.Join(' ', arguments)}{Markup.Escape(shown)}");
        return await ExternalProcess.RunAsync(ExternalProcess.Dotnet, ["watch", .. arguments], directory, environment, cancellationToken);
    }

    /// <summary>The variables nite watch sets for dotnet watch; one the user already set is left alone.</summary>
    internal static Dictionary<string, string> WatchEnvironment(WatchSettings settings, Func<string, string?> current)
    {
        var environment = new Dictionary<string, string>();
        if (!settings.BrowserRefresh && current("DOTNET_WATCH_SUPPRESS_BROWSER_REFRESH") is null)
        {
            environment["DOTNET_WATCH_SUPPRESS_BROWSER_REFRESH"] = "1";
        }
        if (!settings.NoAutoRestart && current("DOTNET_WATCH_RESTART_ON_RUDE_EDIT") is null)
        {
            environment["DOTNET_WATCH_RESTART_ON_RUDE_EDIT"] = "1";
        }
        return environment;
    }
}

internal sealed class VersionCommand : Command
{
    public override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        var parts = Tooling.Version.Split('+', 2);
        AnsiConsole.MarkupLine($"nite [bold]{Markup.Escape(parts[0])}[/]{(parts.Length > 1 ? $" [grey]({Markup.Escape(parts[1][..Math.Min(parts[1].Length, 12)])})[/]" : "")}");
        Output.Hint($"esbuild {EsbuildBinary.Version}, {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        return 0;
    }
}

internal sealed class UpdateSelfSettings : CommandSettings
{
    [CommandOption("--prerelease")]
    [Description("Allow prerelease versions.")]
    public bool Prerelease { get; set; }

    [CommandOption("--version <VERSION>")]
    [Description("Install a specific version.")]
    public string? Version { get; set; }
}

/// <summary><c>nite update-self</c>: <c>dotnet tool update</c>, local if a tool manifest lists Nite.Cli, otherwise global.</summary>
internal sealed class UpdateSelfCommand : AsyncCommand<UpdateSelfSettings>
{
    private const string PackageId = "Nite.Cli";

    public override async Task<int> ExecuteAsync(CommandContext context, UpdateSelfSettings settings, CancellationToken cancellationToken)
    {
        var manifest = FindLocalManifest(Environment.CurrentDirectory);
        var toolRoot = InstalledToolRoot();
        if (manifest is null && toolRoot is null)
        {
            throw new CliException("This nite is not running as a dotnet tool (it may be a source build), so there is nothing to update. Install it with [bold]dotnet tool install --global Nite.Cli[/].");
        }

        var arguments = new List<string> { "tool", "update", PackageId };
        if (manifest is null)
        {
            var globalRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
            if (Path.GetFullPath(toolRoot!).TrimEnd(Path.DirectorySeparatorChar) == Path.GetFullPath(globalRoot).TrimEnd(Path.DirectorySeparatorChar))
            {
                arguments.Add("--global");
            }
            else
            {
                arguments.AddRange(["--tool-path", toolRoot!]);
            }
        }
        if (settings.Prerelease)
        {
            arguments.Add("--prerelease");
        }
        if (settings.Version is { } version)
        {
            arguments.AddRange(["--version", version]);
        }

        Output.Hint($"dotnet {string.Join(' ', arguments)}{(manifest is null ? "" : $"  (local tool: {Markup.Escape(Tooling.Relative(manifest))})")}");
        Output.Hint($"current version: {Markup.Escape(Tooling.ShortVersion)}");
        return await ExternalProcess.RunAsync(ExternalProcess.Dotnet, arguments, Environment.CurrentDirectory, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Where this tool is installed, from its own location: dotnet tools run from
    /// <c>&lt;root&gt;/.store/nite.cli/&lt;version&gt;/…</c>, so the root is the folder above <c>.store</c>.
    /// </summary>
    private static string? InstalledToolRoot()
    {
        var marker = $"{Path.DirectorySeparatorChar}.store{Path.DirectorySeparatorChar}{PackageId.ToLowerInvariant()}{Path.DirectorySeparatorChar}";
        var baseDirectory = AppContext.BaseDirectory;
        var index = baseDirectory.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? null : baseDirectory[..index];
    }

    /// <summary>The nearest dotnet-tools.json that lists Nite.Cli, as dotnet tool itself searches.</summary>
    private static string? FindLocalManifest(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
        {
            foreach (var candidate in new[] { Path.Combine(directory.FullName, ".config", "dotnet-tools.json"), Path.Combine(directory.FullName, "dotnet-tools.json") })
            {
                if (File.Exists(candidate) && File.ReadAllText(candidate).Contains($"\"{PackageId.ToLowerInvariant()}\"", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate;
                }
            }
        }
        return null;
    }
}
