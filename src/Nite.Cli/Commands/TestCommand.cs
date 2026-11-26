using System.ComponentModel;
using Nite.Cli.Infrastructure;
using Nite.Testing;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nite.Cli.Commands;

internal sealed class TestSettings : ProjectSettings
{
    [CommandArgument(0, "[FILTERS]")]
    [Description("Only test files whose path contains one of these.")]
    public string[] Filters { get; set; } = [];

    [CommandOption("-w|--watch")]
    [Description("Keep running and re-run tests when files change.")]
    public bool Watch { get; set; }

    [CommandOption("--ui")]
    [Description("Open the Vitest UI (implies --watch).")]
    public bool Ui { get; set; }

    [CommandOption("--coverage")]
    [Description("Collect coverage for this run, into .nite/coverage.")]
    public bool Coverage { get; set; }
}

/// <summary>
/// <c>nite test</c>: writes a Vitest configuration that matches nite.json, then runs Vitest with
/// Node. Arguments after <c>--</c> go to Vitest as they are.
/// </summary>
internal sealed class TestCommand : AsyncCommand<TestSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, TestSettings settings, CancellationToken cancellationToken)
    {
        var project = Tooling.LoadProject(settings.ProjectDirectory);

        // Packages first, so a fresh checkout or an edited nite.json just works.
        using (var loggers = Tooling.CreateLoggerFactory(settings.Verbose, Microsoft.Extensions.Logging.LogLevel.Warning))
        {
            await Tooling.CreateInstaller(loggers).InstallAsync(project.Directory, project.Config, cancellationToken);
        }

        var vitest = VitestSetup.Find(project) ?? throw new CliException(
            "Vitest is not installed. Add it, with a DOM for component tests: [bold]nite add -D vitest jsdom @testing-library/react[/]");
        if (vitest.Version < VitestSetup.MinimumVersion)
        {
            throw new CliException($"Vitest {vitest.Version} is too old; NiteFx needs {VitestSetup.MinimumVersion} or later. Run [bold]nite update vitest --latest[/].");
        }

        var node = await NodeRuntime.FindAsync(cancellationToken) ?? throw new CliException(
            $"nite test runs Vitest, which needs Node.js{(vitest.NodeRange is { } needed ? $" {Markup.Escape(needed)}" : "")}, and none was found. " +
            $"Install it from https://nodejs.org, or set {NodeRuntime.PathVariable}. (Building and running the app never needs Node.)");
        if (!vitest.SupportsNode(node.Version))
        {
            throw new CliException($"Node.js {node.Version} ({Markup.Escape(node.Path)}) is too old for Vite {vitest.ViteVersion}, which needs {Markup.Escape(vitest.NodeRange!)}.");
        }

        if ((settings.Coverage || vitest.CoverageProvider is not null) && !vitest.HasCoverageProvider(vitest.CoverageProvider ?? "v8"))
        {
            var provider = vitest.CoverageProvider ?? "v8";
            throw new CliException($"Coverage needs @vitest/coverage-{provider}. Add it at Vitest's version: [bold]nite add -D @vitest/coverage-{provider}@{vitest.Version}[/]");
        }

        var configPath = vitest.WriteConfig();
        var arguments = new List<string> { vitest.Entry };
        if (!settings.Watch && !settings.Ui)
        {
            arguments.Add("run");
        }
        arguments.AddRange(["--config", configPath]);
        if (settings.Ui) arguments.Add("--ui");
        if (settings.Coverage) arguments.Add("--coverage");
        arguments.AddRange(settings.Filters);
        arguments.AddRange(context.Remaining.Raw);

        if (settings.Verbose)
        {
            Output.Hint(Markup.Escape($"{node.Path} {string.Join(' ', arguments)}"));
        }
        return await ExternalProcess.RunAsync(node.Path, arguments, project.Directory, cancellationToken: cancellationToken);
    }
}
