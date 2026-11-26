using Microsoft.Extensions.Logging;
using Nite.Packages;
using static Nite.Tests.PackageInstallerTests;

namespace Nite.Tests;

public class ReleaseAgeTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly DirectoryInfo _project = Directory.CreateTempSubdirectory("nite-release-age-");

    public void Dispose() => _project.Delete(recursive: true);

    [Theory]
    [InlineData("90m", 90)]
    [InlineData("12h", 12 * 60)]
    [InlineData("3d", 3 * 24 * 60)]
    [InlineData(" 1w ", 7 * 24 * 60)]
    [InlineData("0d", 0)]
    public void Parses_ages(string text, int minutes) =>
        Assert.Equal(TimeSpan.FromMinutes(minutes), ReleaseAgePolicy.ParseAge(text));

    [Theory]
    [InlineData("3")]
    [InlineData("3 days")]
    [InlineData("-1d")]
    [InlineData("1.5d")]
    public void Rejects_what_is_not_an_age(string text)
    {
        var error = Assert.Throws<InvalidOperationException>(() => ReleaseAgePolicy.ParseAge(text));
        Assert.Contains("\"3d\"", error.Message);
    }

    [Fact]
    public void Is_off_when_unset_or_zero()
    {
        Assert.Null(ReleaseAgePolicy.For(new NiteConfig()));
        Assert.Null(ReleaseAgePolicy.For(new NiteConfig { MinimumReleaseAge = "0h" }));
    }

    [Fact]
    public void Excludes_names_and_scopes()
    {
        var policy = ReleaseAgePolicy.For(new NiteConfig { MinimumReleaseAge = "3d", MinimumReleaseAgeExclude = ["left-pad", "@mine/*"] })!;
        Assert.False(policy.AppliesTo("left-pad"));
        Assert.False(policy.AppliesTo("@mine/ui"));
        Assert.True(policy.AppliesTo("left-pad-extra"));
        Assert.True(policy.AppliesTo("@mineral/ui"));
    }

    [Fact]
    public async Task Passes_over_versions_newer_than_the_age()
    {
        var registry = new FakeRegistry()
            .Add("lib", "1.0.0", published: Now.AddDays(-30))
            .Add("lib", "1.1.0", published: Now.AddDays(-5))
            .Add("lib", "1.2.0", published: Now.AddHours(-6));
        var logger = new ListLogger();

        var lockFile = await Install(registry, new NiteConfig { MinimumReleaseAge = "3d", Dependencies = new() { ["lib"] = "^1.0.0" } }, logger);

        Assert.Equal("1.1.0", lockFile.Packages["node_modules/lib"].Version);
        var passed = Assert.Single(logger.Entries, e => e.Message.StartsWith("Passed over", StringComparison.Ordinal));
        Assert.Contains("lib@1.2.0 (published 6 hour(s) ago; using 1.1.0)", passed.Message);
        Assert.Contains("minimumReleaseAge (3d)", passed.Message);
        // Publish times are only in the full document, so the abbreviated one is not asked for.
        Assert.All(registry.MetadataAccepts, accept => Assert.DoesNotContain("install-v1", accept));
    }

    [Fact]
    public async Task Applies_to_transitive_dependencies_and_dist_tags()
    {
        var registry = new FakeRegistry()
            .Add("app", "1.0.0", dependencies: new() { ["dep"] = "latest" }, published: Now.AddDays(-10))
            .Add("dep", "2.0.0", published: Now.AddDays(-10))
            .Add("dep", "2.1.0", published: Now.AddMinutes(-30));

        var lockFile = await Install(registry, new NiteConfig { MinimumReleaseAge = "1d", Dependencies = new() { ["app"] = "^1.0.0" } });

        Assert.Equal("2.0.0", lockFile.Packages["node_modules/dep"].Version);
    }

    [Fact]
    public async Task Excluded_packages_get_their_newest_version()
    {
        var registry = new FakeRegistry()
            .Add("@mine/ui", "1.0.0", published: Now.AddDays(-30))
            .Add("@mine/ui", "1.0.1", published: Now.AddMinutes(-5));

        var lockFile = await Install(registry, new NiteConfig
        {
            MinimumReleaseAge = "3d",
            MinimumReleaseAgeExclude = ["@mine/*"],
            Dependencies = new() { ["@mine/ui"] = "^1.0.0" },
        });

        Assert.Equal("1.0.1", lockFile.Packages["node_modules/@mine/ui"].Version);
    }

    [Fact]
    public async Task Fails_with_a_way_out_when_only_new_versions_match()
    {
        var registry = new FakeRegistry()
            .Add("lib", "1.0.0", published: Now.AddDays(-30))
            .Add("lib", "2.0.0", published: Now.AddHours(-2));

        var error = await Assert.ThrowsAsync<NitePackageException>(() =>
            Install(registry, new NiteConfig { MinimumReleaseAge = "3d", Dependencies = new() { ["lib"] = "2.0.0" } }));

        Assert.Contains("lib@2.0.0 was published 2 hour(s) ago", error.Message);
        Assert.Contains("minimumReleaseAgeExclude", error.Message);
    }

    [Fact]
    public async Task Keeps_versions_already_locked()
    {
        var registry = new FakeRegistry()
            .Add("lib", "1.0.0", published: Now.AddDays(-30))
            .Add("lib", "1.1.0", published: Now.AddHours(-1))
            .Add("other", "1.0.0", published: Now.AddDays(-30));
        await Install(registry, new NiteConfig { Dependencies = new() { ["lib"] = "^1.0.0" } });

        // Turning the setting on and changing nite.json re-resolves, but the locked lib stays.
        var lockFile = await Install(registry, new NiteConfig { MinimumReleaseAge = "3d", Dependencies = new() { ["lib"] = "^1.0.0", ["other"] = "^1.0.0" } });

        Assert.Equal("1.1.0", lockFile.Packages["node_modules/lib"].Version);
    }

    [Fact]
    public async Task Warns_when_the_registry_gives_no_publish_times()
    {
        var registry = new FakeRegistry().Add("lib", "1.0.0").Add("lib", "1.1.0");
        var logger = new ListLogger();

        var lockFile = await Install(registry, new NiteConfig { MinimumReleaseAge = "3d", Dependencies = new() { ["lib"] = "^1.0.0" } }, logger);

        Assert.Equal("1.1.0", lockFile.Packages["node_modules/lib"].Version);
        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("no publish times for lib", warning.Message);
    }

    [Fact]
    public async Task Reads_install_scripts_and_gzip_from_the_full_document()
    {
        var registry = new FakeRegistry { Gzip = true }
            .Add("native", "1.0.0", published: Now.AddDays(-30), scripts: new() { ["postinstall"] = "node install.js", ["test"] = "tape" });
        var logger = new ListLogger();

        await Install(registry, new NiteConfig { MinimumReleaseAge = "3d", Dependencies = new() { ["native"] = "^1.0.0" } }, logger);

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.StartsWith("native@1.0.0 has an install script", StringComparison.Ordinal));
    }

    [Fact]
    public void A_tag_stands_for_the_newest_stable_version_old_enough()
    {
        var packument = new NpmPackument
        {
            Name = "lib",
            DistTags = new() { ["latest"] = "2.0.0", ["next"] = "3.0.0-rc.2" },
            Versions = new[] { "1.0.0", "1.5.0", "2.0.0-rc.1", "2.0.0", "3.0.0-rc.1", "3.0.0-rc.2" }.ToDictionary(v => v, v => new NpmVersion { Name = "lib", Version = v }),
            Time = new()
            {
                ["1.0.0"] = Json(Now.AddDays(-90)),
                ["1.5.0"] = Json(Now.AddDays(-20)),
                ["2.0.0-rc.1"] = Json(Now.AddDays(-10)),
                ["2.0.0"] = Json(Now.AddDays(-1)),
                ["3.0.0-rc.1"] = Json(Now.AddDays(-4)),
                ["3.0.0-rc.2"] = Json(Now.AddHours(-1)),
            },
        };
        var policy = ReleaseAgePolicy.For(new NiteConfig { MinimumReleaseAge = "3d" }, new FixedTime(Now))!;

        Assert.Equal("1.5.0", policy.ForTag(packument, "2.0.0")!.ToString());
        Assert.Equal("3.0.0-rc.1", policy.ForTag(packument, "3.0.0-rc.2")!.ToString());
    }

    private static System.Text.Json.JsonElement Json(DateTimeOffset time) =>
        System.Text.Json.JsonSerializer.SerializeToElement(time.ToString("O"));

    private Task<NiteLockFile> Install(FakeRegistry registry, NiteConfig config, ListLogger? logger = null) =>
        new PackageInstaller(new HttpClient(registry), logger, new FixedTime(Now)).InstallAsync(_project.FullName, config);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
