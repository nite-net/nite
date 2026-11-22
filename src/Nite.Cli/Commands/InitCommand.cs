using System.ComponentModel;
using System.Text;
using Nite.Cli.Infrastructure;
using Nite.Cli.Templates;
using Nite.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nite.Cli.Commands;

internal sealed class InitSettings : CommandSettings
{
    [CommandArgument(0, "[DIRECTORY]")]
    [Description("Where to create the project. Defaults to the current directory.")]
    public string? Directory { get; set; }

    [CommandOption("-t|--template <TEMPLATE>")]
    [Description("[green]spa[/] (React app + API) or [green]islands[/] (Razor Pages with React islands). Defaults to the init.template setting, which defaults to spa.")]
    public string? Template { get; set; }

    [CommandOption("-n|--name <NAME>")]
    [Description("Project name. Defaults to the directory name.")]
    public string? Name { get; set; }

    [CommandOption("-f|--framework <FRAMEWORK>")]
    [Description("[green]net10.0[/] or [green]net11.0[/]. Defaults to the newest one the installed .NET SDK can build.")]
    public string? Framework { get; set; }

    [CommandOption("--nite-source <PATH>")]
    [Description("Reference a Nite source checkout instead of the NuGet package.")]
    public string? NiteSource { get; set; }

    [CommandOption("--from <PATH>")]
    [Description("Import an existing npm, yarn, pnpm or bun project (Vite or Create React App). The source is not changed.")]
    public string? From { get; set; }

    [CommandOption("--dry-run")]
    [Description("With --from: show what would be imported, and write nothing.")]
    public bool DryRun { get; set; }

    [CommandOption("--force")]
    [Description("Scaffold into a directory that already has files (existing files are never overwritten).")]
    public bool Force { get; set; }

    [CommandOption("--no-install")]
    [Description("Skip installing packages.")]
    public bool NoInstall { get; set; }

    [CommandOption("--verbose")]
    [Description("Show debug output.")]
    public bool Verbose { get; set; }

    public override ValidationResult Validate() =>
        Template is not null && !ProjectTemplates.Names.Contains(Template) ? ValidationResult.Error($"Unknown template '{Template}'. Choose {string.Join(" or ", ProjectTemplates.Names)}.")
        : Framework is not null && !ProjectTemplates.Frameworks.Contains(Framework) ? ValidationResult.Error($"Unknown framework '{Framework}'. Choose {string.Join(" or ", ProjectTemplates.Frameworks)}.")
        : From is not null && Template is not null ? ValidationResult.Error("--from imports a project, so --template does not apply.")
        : DryRun && From is null ? ValidationResult.Error("--dry-run only applies with --from.")
        : ValidationResult.Success();
}

/// <summary><c>nite init</c>: scaffolds a project, then installs its packages.</summary>
internal sealed class InitCommand : AsyncCommand<InitSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, InitSettings settings, CancellationToken cancellationToken)
    {
        var directory = Path.GetFullPath(settings.Directory ?? Environment.CurrentDirectory);
        var template = settings.Template ?? NiteSettings.Load().Get(NiteSettings.InitTemplateKey) ?? ProjectTemplates.Spa;
        if (!ProjectTemplates.Names.Contains(template))
        {
            throw new CliException($"The init.template setting is '{Markup.Escape(template)}', which is not a template. Choose {string.Join(" or ", ProjectTemplates.Names)}.");
        }
        var name = settings.Name ?? new DirectoryInfo(directory).Name;
        var safeName = SafeProjectName(name);

        if (settings.From is not null && Path.GetFullPath(settings.From) == directory)
        {
            throw new CliException("Import into a new directory: nite init <new-dir> --from <project>. The source project is left untouched.");
        }

        if (!settings.DryRun && Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any(e => !IsIgnorable(e)) && !settings.Force)
        {
            throw new CliException($"{Markup.Escape(Tooling.Relative(directory))} is not empty. Use --force to add a project to it anyway.");
        }
        if (!settings.DryRun && Directory.Exists(directory) && Directory.EnumerateFiles(directory, "*.csproj").FirstOrDefault() is { } existing)
        {
            throw new CliException($"{Markup.Escape(Tooling.Relative(directory))} already has {Markup.Escape(Path.GetFileName(existing))}.");
        }

        var replacements = new Dictionary<string, string>
        {
            [ProjectTemplates.PackageReference] = NiteReference(directory, settings.NiteSource),
            ["NiteApp"] = safeName,
            ["NITE_TITLE"] = safeName,
            ["NITE_NAMESPACE"] = SafeNamespace(safeName),
            ["NITE_PORT"] = Random.Shared.Next(5100, 5900).ToString(),
            ["NITE_FRAMEWORK"] = settings.Framework ?? DefaultFramework(await SdkVersionAsync(directory, cancellationToken)),
        };

        if (settings.From is not null)
        {
            return await ImportRunner.RunAsync(settings, directory, safeName, replacements, cancellationToken);
        }

        AnsiConsole.MarkupLine($"Creating [bold]{template}[/] project [bold]{Markup.Escape(safeName)}[/] in {Markup.Escape(Tooling.Relative(directory))}");
        foreach (var (file, content) in ProjectTemplates.For(template).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var relative = Fill(file, replacements);
            var path = Path.Combine(directory, relative);
            if (File.Exists(path))
            {
                AnsiConsole.MarkupLine($"[yellow]skip[/]  {Markup.Escape(relative)} [grey](exists)[/]");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, Fill(content, replacements), new UTF8Encoding(false), cancellationToken);
            AnsiConsole.MarkupLine($"[green]+[/] {Markup.Escape(relative)}");
        }

        if (!settings.NoInstall)
        {
            using var loggers = Tooling.CreateLoggerFactory(settings.Verbose);
            var lockFile = await Tooling.CreateInstaller(loggers).InstallAsync(directory, NiteConfig.Load(directory), cancellationToken);
            Output.Success($"Installed {lockFile.Packages.Count} package(s)");
        }

        AnsiConsole.WriteLine();
        Output.Success("Ready. Next:");
        if (directory != Environment.CurrentDirectory)
        {
            AnsiConsole.MarkupLine($"  cd {Markup.Escape(Tooling.Relative(directory))}");
        }
        if (settings.NoInstall)
        {
            AnsiConsole.MarkupLine("  nite install");
        }
        AnsiConsole.MarkupLine(template == ProjectTemplates.Spa ? "  nite watch" : "  nite watch --browser-refresh");
        return 0;
    }

    private static string NiteReference(string projectDirectory, string? niteSource)
    {
        if (niteSource is null)
        {
            if (Tooling.ShortVersion.EndsWith("-dev", StringComparison.Ordinal))
            {
                throw new CliException("This nite is a local build, so there is no Nite package of its version to reference. Use --nite-source <path-to-nite-repo>.");
            }
            return ProjectTemplates.PackageReference.Replace("NITE_VERSION", Tooling.ShortVersion, StringComparison.Ordinal);
        }

        var source = Path.GetFullPath(niteSource);
        if (!File.Exists(Path.Combine(source, "src", "Nite", "Nite.csproj")))
        {
            throw new CliException($"{Markup.Escape(source)} does not look like a Nite checkout (no src/Nite/Nite.csproj).");
        }
        var relative = Path.GetRelativePath(projectDirectory, source).Replace('/', '\\');
        return $"""
              <ItemGroup>
                <ProjectReference Include="{relative}\src\Nite\Nite.csproj" />
                <!-- Built first so Release builds can run `nite build`; not referenced as an assembly. -->
                <ProjectReference Include="{relative}\src\Nite.Cli\Nite.Cli.csproj" ReferenceOutputAssembly="false" Private="false" />
              </ItemGroup>

              <PropertyGroup>
                <!-- The CLI build for this app's framework: the project reference above builds that one. -->
                <NiteCliPath>$(MSBuildThisFileDirectory){relative}\src\Nite.Cli\bin\$(Configuration)\$(TargetFramework)\Nite.Cli.dll</NiteCliPath>
              </PropertyGroup>

              <Import Project="{relative}\build\Nite.targets" />

            """;
    }

    /// <summary>
    /// The newest framework the SDK can build: a .NET 10 SDK cannot target net11.0, and a project
    /// on net10.0 does not run where only .NET 11 is installed. Without a known SDK version (no
    /// dotnet on PATH), the runtime this tool runs on stands in for it.
    /// </summary>
    internal static string DefaultFramework(string? sdkVersion, int? runtimeMajor = null)
    {
        var major = sdkVersion is not null && int.TryParse(sdkVersion.Split('.')[0], out var sdkMajor)
            ? sdkMajor
            : runtimeMajor ?? Environment.Version.Major;
        return ProjectTemplates.Frameworks.LastOrDefault(f => FrameworkMajor(f) <= major) ?? ProjectTemplates.Frameworks[0];
    }

    private static int FrameworkMajor(string framework) => int.Parse(framework["net".Length..framework.IndexOf('.')]);

    /// <summary><c>dotnet --version</c> where the project goes, so a global.json there is honored; null if it fails or takes over 10 seconds.</summary>
    private static async Task<string?> SdkVersionAsync(string directory, CancellationToken cancellationToken)
    {
        var start = new System.Diagnostics.ProcessStartInfo("dotnet", "--version")
        {
            WorkingDirectory = ExistingAncestor(directory),
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        System.Diagnostics.Process? process;
        try
        {
            process = System.Diagnostics.Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // no dotnet on PATH
        }
        if (process is null)
        {
            return null;
        }

        using (process)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                var output = (await process.StandardOutput.ReadToEndAsync(timeout.Token)).Trim();
                await process.WaitForExitAsync(timeout.Token);
                return process.ExitCode == 0 && output.Length > 0 ? output : null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
        }
    }

    private static string ExistingAncestor(string directory)
    {
        for (var current = directory; current is not null; current = Path.GetDirectoryName(current))
        {
            if (Directory.Exists(current))
            {
                return current;
            }
        }
        return Environment.CurrentDirectory;
    }

    internal static string Fill(string text, IReadOnlyDictionary<string, string> replacements) =>
        replacements.Aggregate(text, (current, pair) => current.Replace(pair.Key, pair.Value, StringComparison.Ordinal));

    private static bool IsIgnorable(string path) =>
        Path.GetFileName(path) is ".git" or ".gitignore" or ".gitattributes" or "README.md" or "LICENSE" or ".DS_Store" or ".vscode" or ".idea";

    internal static string SafeProjectName(string name)
    {
        var cleaned = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '-').ToArray()).Trim('-', '.');
        return cleaned.Length == 0 ? "NiteApp" : cleaned;
    }

    internal static string SafeNamespace(string name)
    {
        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(part =>
        {
            var identifier = new string(part.Select(c => char.IsLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
            return char.IsDigit(identifier[0]) ? "_" + identifier : identifier;
        });
        return string.Join('.', parts);
    }
}
