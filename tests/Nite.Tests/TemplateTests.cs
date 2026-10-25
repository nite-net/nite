using System.Text.Json;
using System.Text.RegularExpressions;
using Nite.Cli.Commands;
using Nite.Cli.Templates;

namespace Nite.Tests;

/// <summary>The dotnet new templates, which nite init writes too.</summary>
public partial class TemplateTests
{
    private static readonly string Content = Path.Combine(RepositoryRoot(), "src", "Nite.Templates", "content");

    [GeneratedRegex(@"NITE_[A-Z_]+")]
    private static partial Regex Placeholder();

    [Theory]
    [InlineData(ProjectTemplates.Spa)]
    [InlineData(ProjectTemplates.Islands)]
    public void The_cli_embeds_every_template_file(string template)
    {
        var onDisk = Directory.EnumerateFiles(Path.Combine(Content, template), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Path.Combine(Content, template), f).Replace('\\', '/'))
            .Where(f => !f.StartsWith(".template.config/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);

        var embedded = ProjectTemplates.For(template);

        Assert.Equal(onDisk, embedded.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(".gitignore", embedded.Keys);
        Assert.Contains("NiteApp.csproj", embedded.Keys);
    }

    [Theory]
    [InlineData(ProjectTemplates.Spa)]
    [InlineData(ProjectTemplates.Islands)]
    public void Template_json_replaces_every_placeholder_the_files_use(string template)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Content, template, ".template.config", "template.json")));
        var replaced = json.RootElement.GetProperty("symbols").EnumerateObject()
            .Select(s => s.Value.GetProperty("replaces").GetString()!)
            .ToHashSet();
        Assert.Equal("NiteApp", json.RootElement.GetProperty("sourceName").GetString());
        Assert.Equal($"nite-{template}", json.RootElement.GetProperty("shortName").GetString());

        var used = ProjectTemplates.For(template).Values.SelectMany(text => Placeholder().Matches(text).Select(m => m.Value)).ToHashSet();
        Assert.NotEmpty(used);
        Assert.Subset(used, replaced);
    }

    [Theory]
    [InlineData(ProjectTemplates.Spa)]
    [InlineData(ProjectTemplates.Islands)]
    public void Nite_init_fills_every_placeholder(string template)
    {
        var replacements = new Dictionary<string, string>
        {
            [ProjectTemplates.PackageReference] = "<!-- reference -->\n",
            ["NiteApp"] = "My-App",
            ["NITE_TITLE"] = "My-App",
            ["NITE_NAMESPACE"] = "My_App",
            ["NITE_PORT"] = "5123",
        };

        foreach (var (file, text) in ProjectTemplates.For(template))
        {
            var filled = InitCommand.Fill(text, replacements);
            Assert.DoesNotMatch(Placeholder(), filled);
            Assert.DoesNotContain("NiteApp", InitCommand.Fill(file, replacements) + filled);
        }
        var project = InitCommand.Fill(ProjectTemplates.For(template)["NiteApp.csproj"], replacements);
        Assert.Contains("<!-- reference -->", project);
        Assert.Contains("<RootNamespace>My_App</RootNamespace>", project);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Nite.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Run the tests from the repository.");
    }
}
