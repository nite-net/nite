using Microsoft.Extensions.Logging;
using Nite;
using Nite.Build;
using Nite.Esbuild;
using Nite.Packages;

const string Usage = """
    Usage: nite <command> [project-directory]

    Commands:
      install   Install nite.json dependencies into node_modules (updates nite.lock.json)
      build     Install, then build islands and entries into wwwroot/_nite

    The project directory defaults to the current directory.
    """;

if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Usage);
    return args.Length == 0 ? 1 : 0;
}

var command = args[0];
var projectDirectory = Path.GetFullPath(args.Length > 1 ? args[1] : Directory.GetCurrentDirectory());

using var loggerFactory = LoggerFactory.Create(logging => logging
    .AddSimpleConsole(o =>
    {
        o.SingleLine = true;
        o.TimestampFormat = null;
    })
    .SetMinimumLevel(LogLevel.Information));
var logger = loggerFactory.CreateLogger("nite");

using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("nite-cli");

try
{
    var project = new NiteProject(projectDirectory);
    var installer = new PackageInstaller(http, loggerFactory.CreateLogger<PackageInstaller>());

    switch (command)
    {
        case "install":
            await installer.InstallAsync(project.Directory, project.Config);
            return 0;

        case "build":
            await installer.InstallAsync(project.Directory, project.Config);
            var binary = await EsbuildBinary.EnsureAsync(http, project.Config.Registry);
            await using (var esbuild = new EsbuildService(binary, loggerFactory.CreateLogger<EsbuildService>()))
            {
                await new NiteBuilder(esbuild, loggerFactory.CreateLogger<NiteBuilder>()).BuildAsync(project);
            }
            return 0;

        default:
            Console.Error.WriteLine($"Unknown command '{command}'.\n");
            Console.Error.WriteLine(Usage);
            return 1;
    }
}
catch (Exception ex) when (ex is EsbuildException or NitePackageException or InvalidOperationException or HttpRequestException or IOException)
{
    logger.LogError("{Message}", ex.Message);
    return 1;
}
