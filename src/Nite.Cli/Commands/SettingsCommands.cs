using System.ComponentModel;
using Nite.Cli.Infrastructure;
using Nite.Configuration;
using Nite.Packages;
using Spectre.Console;
using Spectre.Console.Cli;

namespace Nite.Cli.Commands;

internal sealed class ConfigListSettings : CommandSettings
{
    [CommandOption("-C|--dir <DIRECTORY>")]
    [Description("Project whose nite.json and .npmrc to include. Defaults to the current directory when it has a nite.json.")]
    public string? Directory { get; set; }

    [CommandOption("--show-origin")]
    [Description("Show where each value comes from.")]
    public bool ShowOrigin { get; set; }

    public string? ProjectDirectory
    {
        get
        {
            var directory = Path.GetFullPath(Directory ?? Environment.CurrentDirectory);
            return Directory is not null || File.Exists(Path.Combine(directory, NiteConfig.FileName)) ? directory : null;
        }
    }
}

internal sealed class ConfigListCommand : Command<ConfigListSettings>
{
    public override int Execute(CommandContext context, ConfigListSettings settings, CancellationToken cancellationToken)
    {
        var effective = NiteSettings.Load(settings.ProjectDirectory);
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        table.AddColumn("Key").AddColumn("Value");
        if (settings.ShowOrigin)
        {
            table.AddColumn("Origin");
        }

        foreach (var value in effective.Values)
        {
            var shown = value.Secret ? "[grey](set)[/]" : Markup.Escape(value.Value);
            var origin = value.Origin == "default" ? "[grey]default[/]" : Markup.Escape(Tooling.Relative(value.Origin));
            if (settings.ShowOrigin)
            {
                table.AddRow(Markup.Escape(value.Key), shown, origin);
            }
            else
            {
                table.AddRow(Markup.Escape(value.Key), shown);
            }
        }
        AnsiConsole.Write(table);
        Output.Hint($"User config: {Markup.Escape(NiteDirectories.Current.ConfigFile)}");
        return 0;
    }
}

internal sealed class ConfigKeySettings : CommandSettings
{
    [CommandArgument(0, "<KEY>")]
    [Description("[green]registry[/], [green]@scope:registry[/] or [green]init.template[/].")]
    public string Key { get; set; } = "";
}

internal sealed class ConfigGetCommand : Command<ConfigKeySettings>
{
    public override int Execute(CommandContext context, ConfigKeySettings settings, CancellationToken cancellationToken)
    {
        var directory = Environment.CurrentDirectory;
        var effective = NiteSettings.Load(File.Exists(Path.Combine(directory, NiteConfig.FileName)) ? directory : null);
        var value = effective.GetWithOrigin(settings.Key);
        if (value is null)
        {
            return 1;
        }
        // Plain output, so it composes: REGISTRY=$(nite config get registry)
        Console.WriteLine(value.Secret ? "(set)" : value.Value);
        return 0;
    }
}

internal sealed class ConfigSetSettings : CommandSettings
{
    [CommandArgument(0, "<KEY>")]
    [Description("[green]registry[/], [green]@scope:registry[/] or [green]init.template[/].")]
    public string Key { get; set; } = "";

    [CommandArgument(1, "<VALUE>")]
    public string Value { get; set; } = "";

    public override ValidationResult Validate()
    {
        if (!NiteSettings.IsKnownKey(Key))
        {
            return ValidationResult.Error(NiteSettings.UnknownKeyMessage(Key));
        }
        if (Key.EndsWith("registry", StringComparison.Ordinal) && !Uri.TryCreate(Value, UriKind.Absolute, out var uri) | uri?.Scheme is not ("https" or "http"))
        {
            return ValidationResult.Error($"'{Value}' is not an http(s) URL.");
        }
        if (Key == NiteSettings.InitTemplateKey && !Templates.ProjectTemplates.Names.Contains(Value))
        {
            return ValidationResult.Error($"Unknown template '{Value}'. Choose {string.Join(" or ", Templates.ProjectTemplates.Names)}.");
        }
        return ValidationResult.Success();
    }
}

/// <summary><c>nite config set</c>: writes the user config. Credentials belong in .npmrc and are refused.</summary>
internal sealed class ConfigSetCommand : Command<ConfigSetSettings>
{
    public override int Execute(CommandContext context, ConfigSetSettings settings, CancellationToken cancellationToken)
    {
        NiteSettings.SetUserValue(settings.Key, settings.Value);
        Output.Success($"{Markup.Escape(settings.Key)} = {Markup.Escape(settings.Value)} [grey]({Markup.Escape(NiteDirectories.Current.ConfigFile)})[/]");

        // A project or environment value would still win; say so rather than let it look ignored.
        var directory = Environment.CurrentDirectory;
        var effective = NiteSettings.Load(File.Exists(Path.Combine(directory, NiteConfig.FileName)) ? directory : null).GetWithOrigin(settings.Key);
        if (effective is not null && effective.Origin != NiteDirectories.Current.ConfigFile)
        {
            AnsiConsole.MarkupLine($"[yellow]warn[/]  {Markup.Escape(effective.Origin)} overrides it here ({Markup.Escape(effective.Value)}).");
        }
        return 0;
    }
}

internal sealed class ConfigUnsetCommand : Command<ConfigKeySettings>
{
    public override int Execute(CommandContext context, ConfigKeySettings settings, CancellationToken cancellationToken)
    {
        if (!NiteSettings.UnsetUserValue(settings.Key))
        {
            AnsiConsole.MarkupLine($"[yellow]warn[/]  {Markup.Escape(settings.Key)} is not set in {Markup.Escape(NiteDirectories.Current.ConfigFile)}");
            return 1;
        }
        Output.Success($"Removed {Markup.Escape(settings.Key)}");
        return 0;
    }
}

internal sealed class ConfigPathCommand : Command
{
    public override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        Console.WriteLine(NiteDirectories.Current.ConfigFile);
        return 0;
    }
}

internal sealed class CacheDirCommand : Command
{
    public override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        Console.WriteLine(NiteDirectories.Current.Cache);
        return 0;
    }
}

/// <summary><c>nite cache clean</c>: deletes the cache, plus the cache folders older Nite versions left under LocalApplicationData.</summary>
internal sealed class CacheCleanCommand : Command
{
    public override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        var targets = new[] { NiteDirectories.Current.Cache }.Concat(NiteDirectories.LegacyCacheFolders()).Where(Directory.Exists).ToList();
        if (targets.Count == 0)
        {
            Output.Success("The cache is already empty.");
            return 0;
        }

        long freed = 0;
        foreach (var target in targets)
        {
            freed += Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
            Directory.Delete(target, recursive: true);
            Output.Hint($"removed {Markup.Escape(target)}");
        }
        Output.Success($"Freed {freed / 1024.0 / 1024.0:0.0} MiB. Packages, esbuild and the Sass compiler download again on next use.");
        return 0;
    }
}

internal sealed class CacheVerifyCommand : Command
{
    public override int Execute(CommandContext context, CancellationToken cancellationToken)
    {
        var (checkedCount, removed) = NpmRegistry.VerifyTarballCache(NiteDirectories.Current.Cache);
        if (removed > 0)
        {
            AnsiConsole.MarkupLine($"[yellow]warn[/]  Removed {removed} corrupt tarball(s); they download again on next use.");
        }
        Output.Success($"Checked {checkedCount} cached tarball(s){(removed == 0 ? ", all intact" : "")}.");
        return 0;
    }
}
