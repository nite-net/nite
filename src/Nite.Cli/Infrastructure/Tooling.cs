using System.ComponentModel;
using System.Reflection;
using Microsoft.Extensions.Logging;
using Nite.Esbuild;
using Nite.Packages;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nite.Cli.Infrastructure;

/// <summary>Options every project command shares.</summary>
internal class ProjectSettings : CommandSettings
{
    [CommandOption("-C|--dir <DIRECTORY>")]
    [Description("Project directory (the one with nite.json). Defaults to the current directory.")]
    public string? Directory { get; set; }

    [CommandOption("--verbose")]
    [Description("Show debug output.")]
    public bool Verbose { get; set; }

    public string ProjectDirectory => Path.GetFullPath(Directory ?? Environment.CurrentDirectory);
}

/// <summary>Services shared by the commands.</summary>
internal static class Tooling
{
    public static readonly HttpClient Http = CreateHttpClient();

    public static string Version { get; } = ReadVersion();

    /// <summary>The version without build metadata: 0.1.0 rather than 0.1.0+abc123.</summary>
    public static string ShortVersion => Version.Split('+')[0];

    public static ILoggerFactory CreateLoggerFactory(bool verbose, LogLevel minimum = LogLevel.Information)
    {
        var level = verbose ? LogLevel.Debug : minimum;
        return LoggerFactory.Create(logging => logging
            .ClearProviders()
            .AddProvider(new SpectreLoggerProvider(level))
            .SetMinimumLevel(level));
    }

    public static PackageInstaller CreateInstaller(ILoggerFactory loggers) =>
        new(Http, loggers.CreateLogger<PackageInstaller>());

    public static async Task<EsbuildService> StartEsbuildAsync(NiteProject project, ILoggerFactory loggers, CancellationToken cancellationToken)
    {
        var binary = await EsbuildBinary.EnsureAsync(Http, project.Config.Registry, cancellationToken);
        return new EsbuildService(binary, loggers.CreateLogger<EsbuildService>());
    }

    /// <summary>Loads the project, failing with a helpful message when there is no nite.json.</summary>
    public static NiteProject LoadProject(string directory, bool requireConfig = true)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            throw new CliException($"{directory} does not exist.");
        }
        if (requireConfig && !File.Exists(Path.Combine(directory, NiteConfig.FileName)))
        {
            throw new CliException($"No {NiteConfig.FileName} in {directory}. Run [bold]nite init[/] to create a project, or pass --dir.");
        }
        return new NiteProject(directory);
    }

    public static string Relative(string path) =>
        Path.GetRelativePath(Environment.CurrentDirectory, path) is var relative && !relative.StartsWith("..", StringComparison.Ordinal)
            ? relative
            : path;

    private static string ReadVersion() =>
        typeof(Tooling).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Tooling).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"nite-cli/{ReadVersion().Split('+')[0]}");
        return client;
    }
}

/// <summary>An expected failure: printed as a one-line error, without a stack trace. Markup allowed.</summary>
internal sealed class CliException(string message) : Exception(message);

internal static class Output
{
    public static void Success(string markup) => AnsiConsole.MarkupLine($"[green]✓[/] {markup}");

    public static void Info(string markup) => AnsiConsole.MarkupLine(markup);

    public static void Hint(string markup) => AnsiConsole.MarkupLine($"[grey]{markup}[/]");

    public static string Version(string? version) => version is null ? "[grey]-[/]" : Markup.Escape(version);
}
