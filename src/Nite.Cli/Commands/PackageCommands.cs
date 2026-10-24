using System.ComponentModel;
using Nite.Cli.Infrastructure;
using Nite.Packages;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nite.Cli.Commands;

internal sealed class InstallSettings : ProjectSettings
{
    [CommandArgument(0, "[PACKAGES]")]
    [Description("Packages to add, like [green]react[/], [green]react@^19[/] or [green]@types/react@latest[/]. Omit to install everything in nite.json.")]
    public string[] Packages { get; set; } = [];

    [CommandOption("-D|--dev")]
    [Description("Add to devDependencies (installed for editors and type checking, never bundled).")]
    public bool Dev { get; set; }

    [CommandOption("-E|--exact")]
    [Description("Pin the exact version instead of a ^ range.")]
    public bool Exact { get; set; }
}

/// <summary><c>nite install</c> / <c>nite add</c>: install everything, or add packages first (as pnpm add does).</summary>
internal sealed class InstallCommand : AsyncCommand<InstallSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, InstallSettings settings, CancellationToken cancellationToken)
    {
        var directory = settings.ProjectDirectory;
        var section = settings.Dev ? NiteConfigFile.DevDependencies : NiteConfigFile.Dependencies;
        Tooling.LoadProject(directory, requireConfig: settings.Packages.Length == 0);

        var added = new List<(string Name, string Range)>();
        if (settings.Packages.Length > 0)
        {
            var registry = new NpmRegistry(Tooling.Http, NiteConfig.Load(directory).Registry);
            foreach (var spec in settings.Packages)
            {
                var (name, requested) = PackageSpec.Parse(spec);
                var packument = await registry.GetPackumentAsync(name, cancellationToken);
                var range = PackageSpec.RangeToSave(name, requested, packument, settings.Exact);
                NiteConfigFile.RemoveDependency(directory, name); // moving between sections, like pnpm
                NiteConfigFile.SetDependency(directory, section, name, range);
                added.Add((name, range));
            }
        }

        var before = NiteLockFile.TryLoad(Path.Combine(directory, PackageInstaller.LockFileName));
        using var loggers = Tooling.CreateLoggerFactory(settings.Verbose);
        var lockFile = await Tooling.CreateInstaller(loggers).InstallAsync(directory, NiteConfig.Load(directory), cancellationToken);

        foreach (var (name, range) in added)
        {
            var version = lockFile.Packages.GetValueOrDefault("node_modules/" + name)?.Version;
            AnsiConsole.MarkupLine($"[green]+[/] {Markup.Escape(name)} [bold]{Output.Version(version)}[/] [grey]({Markup.Escape(range)}{(settings.Dev ? ", dev" : "")})[/]");
        }
        LockDiff.Print(before, lockFile);
        return 0;
    }
}

internal sealed class RemoveSettings : ProjectSettings
{
    [CommandArgument(0, "<PACKAGES>")]
    [Description("Packages to remove from nite.json and node_modules.")]
    public string[] Packages { get; set; } = [];
}

internal sealed class RemoveCommand : AsyncCommand<RemoveSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, RemoveSettings settings, CancellationToken cancellationToken)
    {
        var directory = settings.ProjectDirectory;
        Tooling.LoadProject(directory);

        var removed = 0;
        foreach (var spec in settings.Packages)
        {
            var (name, _) = PackageSpec.Parse(spec);
            var sections = NiteConfigFile.RemoveDependency(directory, name);
            if (sections.Count == 0)
            {
                AnsiConsole.MarkupLine($"[yellow]warn[/]  {Markup.Escape(name)} is not in nite.json");
                continue;
            }
            AnsiConsole.MarkupLine($"[red]-[/] {Markup.Escape(name)} [grey]({string.Join(", ", sections)})[/]");
            removed++;
        }
        if (removed == 0)
        {
            return 1;
        }

        var before = NiteLockFile.TryLoad(Path.Combine(directory, PackageInstaller.LockFileName));
        using var loggers = Tooling.CreateLoggerFactory(settings.Verbose);
        var lockFile = await Tooling.CreateInstaller(loggers).InstallAsync(directory, NiteConfig.Load(directory), cancellationToken);
        LockDiff.Print(before, lockFile);
        return 0;
    }
}

internal sealed class ListSettings : ProjectSettings
{
    [CommandOption("-a|--all")]
    [Description("Include transitive dependencies.")]
    public bool All { get; set; }
}

internal sealed class ListCommand : Command<ListSettings>
{
    public override int Execute(CommandContext context, ListSettings settings, CancellationToken cancellationToken)
    {
        var project = Tooling.LoadProject(settings.ProjectDirectory);
        var config = project.Config;
        var lockFile = NiteLockFile.TryLoad(Path.Combine(project.Directory, PackageInstaller.LockFileName));
        if (lockFile is null)
        {
            Output.Hint("No nite.lock.json yet. Run [bold]nite install[/].");
        }

        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("Package").AddColumn("Range").AddColumn("Installed").AddColumn("Type");

        var direct = config.AllDependencies.OrderBy(p => p.Key, StringComparer.Ordinal);
        foreach (var (name, range) in direct)
        {
            var type = config.Dependencies.ContainsKey(name) ? "dependency"
                : config.DevDependencies.ContainsKey(name) ? "[grey]dev[/]"
                : "[grey]implicit[/]";
            var installed = Installed(project.Directory, "node_modules/" + name);
            table.AddRow(Markup.Escape(name), Markup.Escape(range), installed is null ? "[red]missing[/]" : $"[green]{Markup.Escape(installed)}[/]", type);
        }

        if (settings.All && lockFile is not null)
        {
            var transitive = lockFile.Packages.Where(p => !config.AllDependencies.ContainsKey(p.Key["node_modules/".Length..]));
            foreach (var (location, package) in transitive.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var nested = location.Split("/node_modules/");
                var label = nested.Length == 1
                    ? location["node_modules/".Length..]
                    : $"{nested[^1]} [grey](under {Markup.Escape(nested[^2].Replace("node_modules/", ""))})[/]";
                var installed = Installed(project.Directory, location);
                table.AddRow($"[grey]{(nested.Length == 1 ? Markup.Escape(label) : label)}[/]", "", installed is null ? "[red]missing[/]" : Markup.Escape(installed), "[grey]transitive[/]");
            }
        }

        if (table.Rows.Count == 0)
        {
            Output.Hint("No packages. Add one with [bold]nite add <package>[/].");
            return 0;
        }

        AnsiConsole.Write(table);
        if (!settings.All && lockFile is not null)
        {
            var transitiveCount = lockFile.Packages.Count - config.AllDependencies.Count;
            if (transitiveCount > 0)
            {
                Output.Hint($"{transitiveCount} transitive package(s) not shown; use --all.");
            }
        }
        return 0;
    }

    private static string? Installed(string projectDirectory, string location)
    {
        var path = Path.Combine(projectDirectory, location, "package.json");
        if (!File.Exists(path))
        {
            return null;
        }
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.TryGetProperty("version", out var version) ? version.GetString() : null;
    }
}

internal sealed class UpdateSettings : ProjectSettings
{
    [CommandArgument(0, "[PACKAGES]")]
    [Description("Only these packages. Omit to update everything.")]
    public string[] Packages { get; set; } = [];

    [CommandOption("-c|--check")]
    [Description("Only report what is outdated; change nothing.")]
    public bool Check { get; set; }

    [CommandOption("-L|--latest")]
    [Description("Move ranges in nite.json to the latest versions, even across major versions.")]
    public bool Latest { get; set; }
}

/// <summary>
/// <c>nite update</c>: reports current, wanted (newest within the range) and latest versions, then
/// installs the wanted ones, or with <c>--latest</c> rewrites the ranges first.
/// </summary>
internal sealed class UpdateCommand : AsyncCommand<UpdateSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, UpdateSettings settings, CancellationToken cancellationToken)
    {
        var project = Tooling.LoadProject(settings.ProjectDirectory);
        var config = project.Config;
        var lockPath = Path.Combine(project.Directory, PackageInstaller.LockFileName);
        var before = NiteLockFile.TryLoad(lockPath);

        var names = settings.Packages.Select(p => PackageSpec.Parse(p).Name).ToList();
        foreach (var unknown in names.Where(n => !config.AllDependencies.ContainsKey(n)))
        {
            throw new CliException($"{Markup.Escape(unknown)} is not in nite.json.");
        }
        var direct = config.AllDependencies.Where(p => names.Count == 0 || names.Contains(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal).ToList();

        var registry = new NpmRegistry(Tooling.Http, config.Registry);
        var rows = await AnsiConsole.Status().StartAsync("Checking the registry…", async _ =>
            await Task.WhenAll(direct.Select(async pair =>
            {
                var packument = await registry.GetPackumentAsync(pair.Key, cancellationToken);
                var versions = packument.Versions.Keys.Select(v => SemVersion.TryParse(v, out var parsed) ? parsed : null).OfType<SemVersion>().ToList();
                var wanted = SemVerRange.TryParse(pair.Value, out var range) ? range.MaxSatisfying(versions) : null;
                var latest = packument.DistTags.TryGetValue("latest", out var tag) && SemVersion.TryParse(tag, out var parsedLatest) ? parsedLatest : null;
                var current = before?.Packages.GetValueOrDefault("node_modules/" + pair.Key)?.Version;
                return new Row(pair.Key, pair.Value, current, wanted, latest, config.Dependencies.ContainsKey(pair.Key));
            })));

        var outdated = rows.Where(r => r.IsOutdated).ToList();
        if (outdated.Count == 0)
        {
            Output.Success($"All {rows.Length} package(s) are on their latest versions.");
            if (settings.Check || settings.Latest)
            {
                return 0;
            }
        }
        else
        {
            var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
            table.AddColumn("Package").AddColumn("Current").AddColumn("Wanted").AddColumn("Latest").AddColumn("Range");
            foreach (var row in outdated)
            {
                table.AddRow(
                    Markup.Escape(row.Name) + (row.IsDependency ? "" : " [grey](dev)[/]"),
                    Output.Version(row.Current),
                    row.Wanted is null ? "[grey]-[/]" : row.WantedIsNewer ? $"[yellow]{row.Wanted}[/]" : row.Wanted.ToString(),
                    row.Latest is null ? "[grey]-[/]" : row.LatestIsNewer ? $"[magenta]{row.Latest}[/]" : row.Latest.ToString(),
                    Markup.Escape(row.Range));
            }
            AnsiConsole.Write(table);
            Output.Hint("[yellow]Wanted[/]: newest within the range (nite update). [magenta]Latest[/]: outside the range (nite update --latest).");
        }

        if (settings.Check)
        {
            return 0;
        }

        if (settings.Latest)
        {
            foreach (var row in rows.Where(r => r.LatestIsNewer && r.Latest is not null))
            {
                var range = PackageSpec.KeepOperator(row.Range, row.Latest!.ToString());
                NiteConfigFile.SetDependency(project.Directory, row.IsDependency ? NiteConfigFile.Dependencies : NiteConfigFile.DevDependencies, row.Name, range);
                AnsiConsole.MarkupLine($"[grey]nite.json[/] {Markup.Escape(row.Name)} [grey]\"{Markup.Escape(row.Range)}\"[/] → [magenta]\"{Markup.Escape(range)}\"[/]");
            }
        }

        using var loggers = Tooling.CreateLoggerFactory(settings.Verbose);
        var lockFile = await Tooling.CreateInstaller(loggers).InstallAsync(project.Directory, NiteConfig.Load(project.Directory), cancellationToken, update: names);
        LockDiff.Print(before, lockFile);
        return 0;
    }

    private sealed record Row(string Name, string Range, string? Current, SemVersion? Wanted, SemVersion? Latest, bool IsDependency)
    {
        private SemVersion? CurrentVersion => SemVersion.TryParse(Current, out var version) ? version : null;
        public bool WantedIsNewer => Wanted is not null && (CurrentVersion is null || Wanted > CurrentVersion);
        public bool LatestIsNewer => Latest is not null && (Wanted is null || Latest > Wanted) && (CurrentVersion is null || Latest > CurrentVersion);
        public bool IsOutdated => WantedIsNewer || LatestIsNewer;
    }
}

/// <summary>Parses <c>name@range</c> package specs, scoped names included.</summary>
internal static class PackageSpec
{
    public static (string Name, string? Range) Parse(string spec)
    {
        var at = spec.IndexOf('@', spec.StartsWith('@') ? 1 : 0);
        var (name, range) = at < 0 ? (spec, null) : (spec[..at], spec[(at + 1)..]);
        var parts = name.Split('/');
        var valid = name.StartsWith('@')
            ? parts.Length == 2 && parts[0].Length > 1 && parts[1].Length > 0
            : parts.Length == 1 && name.Length > 0;
        if (!valid || name.StartsWith('.') || name.Contains(".."))
        {
            throw new CliException($"'{Markup.Escape(spec)}' is not a package name.");
        }
        return (name, string.IsNullOrWhiteSpace(range) ? null : range);
    }

    /// <summary>The range to write to nite.json: given ranges as-is, dist-tags and bare names as ^latest.</summary>
    public static string RangeToSave(string name, string? requested, NpmPackument packument, bool exact)
    {
        string version;
        if (requested is null || packument.DistTags.ContainsKey(requested))
        {
            version = packument.DistTags.GetValueOrDefault(requested ?? "latest")
                ?? throw new CliException($"{Markup.Escape(name)} has no '{Markup.Escape(requested ?? "latest")}' tag.");
        }
        else if (SemVerRange.TryParse(requested, out var range))
        {
            var versions = packument.Versions.Keys.Select(v => SemVersion.TryParse(v, out var parsed) ? parsed : null).OfType<SemVersion>();
            var match = range.MaxSatisfying(versions)
                ?? throw new CliException($"No version of {Markup.Escape(name)} matches '{Markup.Escape(requested)}'.");
            // A real range is kept as written; a plain version honors --exact.
            if (!SemVersion.TryParse(requested, out _))
            {
                return requested;
            }
            version = match.ToString();
        }
        else
        {
            throw new CliException($"'{Markup.Escape(requested)}' is not a version, range or tag of {Markup.Escape(name)}. Nite installs registry versions only.");
        }
        return exact ? version : "^" + version;
    }

    /// <summary>Keeps a range's operator when moving it to a new version: ^1.2.3 → ^2.0.0, 1.2.3 → 2.0.0.</summary>
    public static string KeepOperator(string range, string version) =>
        range.StartsWith('~') ? "~" + version
        : SemVersion.TryParse(range, out _) ? version
        : "^" + version;
}

/// <summary>Prints what an install changed, pnpm style.</summary>
internal static class LockDiff
{
    /// <returns>Whether anything changed.</returns>
    public static bool Print(NiteLockFile? before, NiteLockFile after)
    {
        var old = before?.Packages ?? [];
        var added = after.Packages.Keys.Except(old.Keys).ToList();
        var removed = old.Keys.Except(after.Packages.Keys).ToList();
        var changed = after.Packages.Where(p => old.TryGetValue(p.Key, out var previous) && previous.Version != p.Value.Version).ToList();

        if (added.Count + removed.Count + changed.Count == 0)
        {
            Output.Success($"{after.Packages.Count} package(s) installed, nothing changed.");
            return false;
        }

        foreach (var (location, package) in changed)
        {
            AnsiConsole.MarkupLine($"[yellow]↑[/] {Markup.Escape(Name(location))} {Markup.Escape(old[location].Version)} → [bold]{Markup.Escape(package.Version)}[/]");
        }
        Output.Success($"Packages: [green]+{added.Count}[/] [red]-{removed.Count}[/]{(changed.Count > 0 ? $" [yellow]~{changed.Count}[/]" : "")} [grey]({after.Packages.Count} installed)[/]");
        return true;
    }

    private static string Name(string location) => location[(location.LastIndexOf("node_modules/", StringComparison.Ordinal) + "node_modules/".Length)..];
}
