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

        if (settings.Ui && !vitest.HasUi)
        {
            // Vitest would offer to install it with npm.
            throw new CliException($"The Vitest UI is a package of its own. Add it at Vitest's version: [bold]nite add -D @vitest/ui@{vitest.Version}[/]");
        }

        var configPath = vitest.WriteConfig();
        var arguments = new List<string> { vitest.Entry };
        if (!settings.Watch && !settings.Ui)
        {
            arguments.Add("run");
        }
        arguments.AddRange(["--config", configPath]);
        var uiPort = 0;
        if (settings.Ui)
        {
            // A port of our choosing, on IPv4 loopback, so the dev server knows where to find it;
            // --watch because Vitest otherwise runs once and exits when not in a terminal.
            uiPort = VitestUi.FreePort();
            arguments.AddRange(["--ui", "--watch", "--api.host", "127.0.0.1", "--api.port", uiPort.ToString(System.Globalization.CultureInfo.InvariantCulture), "--api.strictPort"]);
        }
        if (settings.Coverage) arguments.Add("--coverage");
        arguments.AddRange(settings.Filters);
        arguments.AddRange(context.Remaining.Raw);

        if (settings.Verbose)
        {
            Output.Hint(Markup.Escape($"{node.Path} {string.Join(' ', arguments)}"));
        }
        if (!settings.Ui)
        {
            return await ExternalProcess.RunAsync(node.Path, arguments, project.Directory, cancellationToken: cancellationToken);
        }

        // This process lives as long as Vitest, so its id tells the dev server whether the UI is still up.
        var state = VitestUi.Write(project, new VitestUi(uiPort, Environment.ProcessId));
        Output.Hint($"With the app running, the UI is also at {VitestUi.BasePath} on the app's address: open the link below with the app's host and port.");
        try
        {
            return await ExternalProcess.RunAsync(node.Path, arguments, project.Directory, cancellationToken: cancellationToken);
        }
        finally
        {
            File.Delete(state);
        }
    }
}
