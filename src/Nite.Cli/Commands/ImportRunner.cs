using Nite.Cli.Infrastructure;
using Nite.Cli.Templates;
using Nite.Import;
using Nite.Packages;
using Spectre.Console;

namespace Nite.Cli.Commands;

/// <summary><c>nite init --from</c>: reports what an existing project would become, then (unless a dry run) creates it.</summary>
internal static class ImportRunner
{
    public static async Task<int> RunAsync(InitSettings settings, string directory, string name, IReadOnlyDictionary<string, string> replacements, CancellationToken cancellationToken)
    {
        ImportPlan plan;
        try
        {
            plan = ProjectImporter.Analyze(settings.From!);
        }
        catch (ImportException ex)
        {
            throw new CliException(Markup.Escape(ex.Message));
        }

        Report(plan);
        if (settings.DryRun)
        {
            Output.Hint("Dry run: nothing was written.");
            return 0;
        }

        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine($"Creating [bold]{Markup.Escape(name)}[/] in {Markup.Escape(Tooling.Relative(directory))}");

        // The host project comes from the SPA template; everything client-side comes from the source.
        foreach (var (file, content) in ProjectTemplates.For(ProjectTemplates.Spa))
        {
            if (file == "nite.json" || file.StartsWith("Client/", StringComparison.Ordinal))
            {
                continue;
            }
            var relative = InitCommand.Fill(file, replacements);
            var path = Path.Combine(directory, relative);
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, InitCommand.Fill(content, replacements), cancellationToken);
            }
        }
        var written = ProjectImporter.Apply(plan, directory);
        foreach (var group in written.GroupBy(GroupOf).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            AnsiConsole.MarkupLine($"[green]+[/] {Markup.Escape(group.Key)} [grey]({group.Count()} file{(group.Count() == 1 ? "" : "s")})[/]");
        }

        if (!settings.NoInstall)
        {
            using var loggers = Tooling.CreateLoggerFactory(settings.Verbose);
            var lockFile = await Tooling.CreateInstaller(loggers).InstallAsync(directory, NiteConfig.Load(directory), cancellationToken, seedVersions: plan.LockedVersions);
            ReportVersions(plan, lockFile);
        }

        AnsiConsole.WriteLine();
        Output.Success("Imported. Next:");
        if (directory != Environment.CurrentDirectory)
        {
            AnsiConsole.MarkupLine($"  cd {Markup.Escape(Tooling.Relative(directory))}");
        }
        AnsiConsole.MarkupLine(settings.NoInstall ? "  nite install\n  nite watch" : "  nite watch");
        Output.Hint("Your API goes in Program.cs; the app can call it at /api/... with no proxy or CORS.");
        return 0;
    }

    private static void Report(ImportPlan plan)
    {
        var locked = plan.LockedVersions.Count;
        AnsiConsole.MarkupLine($"Importing [bold]{Markup.Escape(Tooling.Relative(plan.Source))}[/]: {plan.Framework} project"
            + (plan.Lockfile is null ? ", no lockfile" : $", {Markup.Escape(Path.GetFileName(plan.Lockfile))} ({locked} package{(locked == 1 ? "" : "s")} locked)"));

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("Package").AddColumn("Range").AddColumn("Locked").AddColumn("Imported as");
        foreach (var (package, range) in plan.Dependencies)
        {
            table.AddRow(Markup.Escape(package), Markup.Escape(range), Output.Version(plan.LockedVersion(package)), "dependency");
        }
        foreach (var (package, range) in plan.DevDependencies)
        {
            table.AddRow(Markup.Escape(package), Markup.Escape(range), Output.Version(plan.LockedVersion(package)), "[grey]dev[/]");
        }
        foreach (var skipped in plan.Skipped.OrderBy(s => s.Name, StringComparer.Ordinal))
        {
            table.AddRow($"[grey]{Markup.Escape(skipped.Name)}[/]", $"[grey]{Markup.Escape(skipped.Spec)}[/]", "", $"[grey]skipped: {Markup.Escape(skipped.Reason)}[/]");
        }
        AnsiConsole.Write(table);

        foreach (var group in plan.Files.GroupBy(f => GroupOf(f.Destination)).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            Output.Hint($"{group.Key}: {group.Count()} file{(group.Count() == 1 ? "" : "s")}");
        }
        foreach (var warning in plan.Warnings)
        {
            AnsiConsole.MarkupLine($"[yellow]warn[/]  {Markup.Escape(warning)}");
        }
        foreach (var note in plan.Notes)
        {
            AnsiConsole.MarkupLine($"[grey]note  {Markup.Escape(note)}[/]");
        }
    }

    /// <summary>Says how many direct dependencies kept the version the old lockfile had.</summary>
    private static void ReportVersions(ImportPlan plan, NiteLockFile lockFile)
    {
        var kept = 0;
        var changed = new List<string>();
        foreach (var package in plan.Dependencies.Keys.Concat(plan.DevDependencies.Keys))
        {
            var before = plan.LockedVersion(package);
            var after = lockFile.Packages.GetValueOrDefault("node_modules/" + package)?.Version;
            if (before is null || after is null)
            {
                continue;
            }
            if (before == after)
            {
                kept++;
            }
            else
            {
                changed.Add($"{package} {before} → {after}");
            }
        }

        Output.Success($"Installed {lockFile.Packages.Count} package(s)" + (plan.LockedVersions.Count > 0 ? $"; {kept} direct dependenc{(kept == 1 ? "y keeps its" : "ies keep their")} locked version" : ""));
        foreach (var line in changed)
        {
            AnsiConsole.MarkupLine($"[yellow]↑[/] {Markup.Escape(line)}");
        }
    }

    private static string GroupOf(string destination)
    {
        var parts = destination.Split('/');
        return parts.Length == 1 ? (destination.StartsWith(".env", StringComparison.Ordinal) ? ".env files" : destination)
            : parts[0] == "Client" && parts.Length > 2 ? $"Client/{parts[1]}/"
            : parts[0] + "/";
    }
}
