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
                <NiteCliPath>$(MSBuildThisFileDirectory){relative}\src\Nite.Cli\bin\$(Configuration)\net10.0\Nite.Cli.dll</NiteCliPath>
              </PropertyGroup>

              <Import Project="{relative}\build\Nite.targets" />

            """;
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
