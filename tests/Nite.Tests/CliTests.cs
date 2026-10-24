using Nite.Cli.Commands;
using Nite.Cli.Infrastructure;
using Nite.Packages;

namespace Nite.Tests;

public class NiteConfigFileTests : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("nite-config-");
    private string ConfigPath => Path.Combine(_directory.FullName, "nite.json");

    public void Dispose() => _directory.Delete(recursive: true);

    [Fact]
    public void Adds_a_dependency_and_keeps_comments_elsewhere()
    {
        File.WriteAllText(ConfigPath, """
            {
              // The client lives here.
              "clientRoot": "Client",
              "dependencies": {
                "react": "^19.3.1"
              },
              /* tooling */
              "devDependencies": {
                "@types/react": "^19.2.0"
              }
            }
            """);

        NiteConfigFile.SetDependency(_directory.FullName, NiteConfigFile.Dependencies, "clsx", "^2.1.1");

        var text = File.ReadAllText(ConfigPath);
        Assert.Contains("// The client lives here.", text);
        Assert.Contains("/* tooling */", text);
        var config = NiteConfig.Load(_directory.FullName);
        Assert.Equal(["clsx", "react"], config.Dependencies.Keys.Order());
        Assert.Equal("^19.2.0", config.DevDependencies["@types/react"]);
    }

    [Fact]
    public void Creates_a_missing_section_and_a_missing_file()
    {
        File.WriteAllText(ConfigPath, "{\n  \"clientRoot\": \"Client\"\n}\n");
        NiteConfigFile.SetDependency(_directory.FullName, NiteConfigFile.DevDependencies, "@types/node", "^22");
        Assert.Equal("^22", NiteConfig.Load(_directory.FullName).DevDependencies["@types/node"]);
        Assert.Equal("Client", NiteConfig.Load(_directory.FullName).ClientRoot);

        File.Delete(ConfigPath);
        NiteConfigFile.SetDependency(_directory.FullName, NiteConfigFile.Dependencies, "react", "^19");
        Assert.Equal("^19", NiteConfig.Load(_directory.FullName).Dependencies["react"]);
    }

    [Fact]
    public void Removes_from_whichever_section_has_it()
    {
        File.WriteAllText(ConfigPath, """
            { "dependencies": { "a": "1", "b": "2" }, "devDependencies": { "c": "3" } }
            """);

        Assert.Equal([NiteConfigFile.DevDependencies], NiteConfigFile.RemoveDependency(_directory.FullName, "c"));
        Assert.Equal([NiteConfigFile.Dependencies], NiteConfigFile.RemoveDependency(_directory.FullName, "a"));
        Assert.Empty(NiteConfigFile.RemoveDependency(_directory.FullName, "missing"));

        var config = NiteConfig.Load(_directory.FullName);
        Assert.Equal(["b"], config.Dependencies.Keys);
        Assert.Empty(config.DevDependencies);
    }

    [Fact]
    public void Ignores_keys_with_the_same_name_in_nested_objects()
    {
        File.WriteAllText(ConfigPath, """
            { "other": { "dependencies": { "nested": "1" } }, "dependencies": { "top": "1" } }
            """);
        NiteConfigFile.SetDependency(_directory.FullName, NiteConfigFile.Dependencies, "added", "2");
        Assert.Contains("\"nested\": \"1\"", File.ReadAllText(ConfigPath).Replace("\"nested\":\"1\"", "\"nested\": \"1\""));
        Assert.Equal(["added", "top"], NiteConfig.Load(_directory.FullName).Dependencies.Keys.Order());
    }
}

public class PackageSpecTests
{
    [Theory]
    [InlineData("react", "react", null)]
    [InlineData("react@^19", "react", "^19")]
    [InlineData("@types/react", "@types/react", null)]
    [InlineData("@types/react@latest", "@types/react", "latest")]
    [InlineData("react@", "react", null)]
    public void Parses_names_and_ranges(string spec, string name, string? range)
    {
        Assert.Equal((name, range), PackageSpec.Parse(spec));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b/c")]
    [InlineData("@scope/a/b")]
    [InlineData("@")]
    public void Rejects_things_that_are_not_package_names(string spec)
    {
        Assert.Throws<CliException>(() => PackageSpec.Parse(spec));
    }

    private static readonly NpmPackument React = new()
    {
        Name = "react",
        DistTags = new() { ["latest"] = "19.3.1", ["next"] = "20.0.0-rc.1" },
        Versions = new()
        {
            ["18.3.1"] = new(), ["19.2.0"] = new(), ["19.3.1"] = new(), ["20.0.0-rc.1"] = new(),
        },
    };

    [Theory]
    [InlineData(null, false, "^19.3.1")]
    [InlineData(null, true, "19.3.1")]
    [InlineData("next", false, "^20.0.0-rc.1")]
    [InlineData("^18", false, "^18")]
    [InlineData(">=18 <19.3", false, ">=18 <19.3")]
    [InlineData("19.2.0", false, "^19.2.0")]
    [InlineData("19.2.0", true, "19.2.0")]
    public void Saves_the_range_pnpm_would(string? requested, bool exact, string expected)
    {
        Assert.Equal(expected, PackageSpec.RangeToSave("react", requested, React, exact));
    }

    [Fact]
    public void Refuses_ranges_nothing_satisfies()
    {
        Assert.Throws<CliException>(() => PackageSpec.RangeToSave("react", "^99", React, exact: false));
        Assert.Throws<CliException>(() => PackageSpec.RangeToSave("react", "github:facebook/react", React, exact: false));
    }

    [Theory]
    [InlineData("^19.2.0", "20.1.0", "^20.1.0")]
    [InlineData("~19.2.0", "20.1.0", "~20.1.0")]
    [InlineData("19.2.0", "20.1.0", "20.1.0")]
    [InlineData(">=19", "20.1.0", "^20.1.0")]
    public void Keeps_the_range_operator_when_moving_to_latest(string range, string version, string expected)
    {
        Assert.Equal(expected, PackageSpec.KeepOperator(range, version));
    }
}

public class InitNamingTests
{
    [Theory]
    [InlineData("my-app", "my-app", "my_app")]
    [InlineData("My Site", "My-Site", "My_Site")]
    [InlineData("2048", "2048", "_2048")]
    [InlineData("Contoso.Web", "Contoso.Web", "Contoso.Web")]
    [InlineData("!!!", "NiteApp", "NiteApp")]
    public void Makes_valid_project_names_and_namespaces(string input, string project, string ns)
    {
        var safe = InitCommand.SafeProjectName(input);
        Assert.Equal(project, safe);
        Assert.Equal(ns, InitCommand.SafeNamespace(safe));
    }
}
