using Nite.Cli.Commands;
using Nite.Cli.Infrastructure;
using Nite.Esbuild;
using Nite.Packages;
using Spectre.Console;
using Spectre.Console.Cli;


// Redirected output (pipes, CI logs) can report a width of -1, which makes Spectre drop every
// line it renders. Fall back to $COLUMNS, then to something wide enough for tables.
if (AnsiConsole.Profile.Width <= 0)
{
    AnsiConsole.Profile.Width = int.TryParse(Environment.GetEnvironmentVariable("COLUMNS"), out var columns) && columns > 0 ? columns : 120;
}
if (AnsiConsole.Profile.Height <= 0)
{
    AnsiConsole.Profile.Height = 24;
}

var app = new CommandApp();
app.Configure(config =>
{
    config.SetApplicationName("nite");
    config.SetApplicationVersion(Tooling.ShortVersion);
    config.SetExceptionHandler((exception, _) => ReportError(exception));

    config.AddCommand<InitCommand>("init")
        .WithDescription("Create a project (a React SPA, or Razor Pages with islands).")
        .WithExample("init")
        .WithExample("init", "my-app")
        .WithExample("init", "my-site", "--template", "islands");

    config.AddCommand<InstallCommand>("install")
        .WithAlias("add")
        .WithAlias("i")
        .WithDescription("Install nite.json packages, or add new ones.")
        .WithExample("install")
        .WithExample("add", "react-router")
        .WithExample("add", "@types/node", "-D");

    config.AddCommand<RemoveCommand>("remove")
        .WithAlias("rm")
        .WithAlias("delete")
        .WithAlias("uninstall")
        .WithDescription("Remove packages from nite.json and node_modules.")
        .WithExample("rm", "react-router");

    config.AddCommand<ListCommand>("list")
        .WithAlias("ls")
        .WithDescription("Show installed packages.")
        .WithExample("list", "--all");

    config.AddCommand<UpdateCommand>("update")
        .WithAlias("up")
        .WithDescription("Check for newer packages and update within (or, with --latest, beyond) their ranges.")
        .WithExample("update", "--check")
        .WithExample("update")
        .WithExample("update", "react", "--latest");

    config.AddCommand<BuildCommand>("build")
        .WithDescription("Production build into wwwroot/_nite (dotnet publish runs this for you).");

    config.AddCommand<WatchCommand>("watch")
        .WithDescription("Run dotnet watch with Nite handling the browser (hot updates without losing state).")
        .WithExample("watch")
        .WithExample("watch", "--", "--launch-profile", "https");

    config.AddCommand<VersionCommand>("version")
        .WithDescription("Show the Nite version.");

    config.AddCommand<UpdateSelfCommand>("update-self")
        .WithDescription("Update the nite tool itself (dotnet tool update).");
});

return await app.RunAsync(args);

static int ReportError(Exception exception)
{
    switch (exception)
    {
        case CliException:
            AnsiConsole.MarkupLine($"[red]error[/] {exception.Message}");
            break;
        case NitePackageException or EsbuildException or InvalidOperationException or HttpRequestException or IOException:
            AnsiConsole.MarkupLine($"[red]error[/] {Markup.Escape(exception.Message)}");
            break;
        case CommandAppException:
            AnsiConsole.MarkupLine($"[red]error[/] {Markup.Escape(exception.Message)}");
            AnsiConsole.MarkupLine("[grey]Run [bold]nite --help[/] for usage.[/]");
            break;
        case OperationCanceledException:
            return 130;
        default:
            AnsiConsole.WriteException(exception, ExceptionFormats.ShortenEverything);
            break;
    }
    return 1;
}
