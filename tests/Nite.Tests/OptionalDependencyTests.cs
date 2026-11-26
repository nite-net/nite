using Microsoft.Extensions.Logging;
using Nite.Packages;
using static Nite.Tests.PackageInstallerTests;

namespace Nite.Tests;

public class OptionalDependencyTests : IDisposable
{
    private static readonly NpmPlatform LinuxGlibc = new("linux", "x64", "glibc");
    private static readonly NpmPlatform MacArm = new("darwin", "arm64", null);
    private readonly DirectoryInfo _project = Directory.CreateTempSubdirectory("nite-optional-");

    public void Dispose() => _project.Delete(recursive: true);

    private static FakeRegistry ToolWithBinaries() => new FakeRegistry()
        .Add("tool", "1.0.0", optionalDependencies: new()
        {
            ["@tool/linux-x64-gnu"] = "1.0.0",
            ["@tool/linux-x64-musl"] = "1.0.0",
            ["@tool/darwin-arm64"] = "1.0.0",
            ["@tool/missing"] = "1.0.0",
            ["@tool/unpublished-version"] = "9.9.9",
        })
        .Add("@tool/linux-x64-gnu", "1.0.0", os: ["linux"], cpu: ["x64"])
        .Add("@tool/linux-x64-musl", "1.0.0", os: ["linux"], cpu: ["x64"])
        .Add("@tool/darwin-arm64", "1.0.0", os: ["darwin"], cpu: ["arm64"])
        .Add("@tool/unpublished-version", "1.0.0");

    [Fact]
    public async Task Locks_every_platform_and_installs_the_one_that_fits()
    {
        var lockFile = await Install(ToolWithBinaries(), Config(("tool", "^1.0.0")), LinuxGlibc);

        Assert.Equal(["node_modules/@tool/darwin-arm64", "node_modules/@tool/linux-x64-gnu", "node_modules/@tool/linux-x64-musl", "node_modules/tool"],
            lockFile.Packages.Keys.Order(StringComparer.Ordinal));
        Assert.True(lockFile.Packages["node_modules/@tool/darwin-arm64"].Optional);
        Assert.Null(lockFile.Packages["node_modules/tool"].Optional);
        Assert.Equal(["musl"], lockFile.Packages["node_modules/@tool/linux-x64-musl"].Libc!);
        Assert.Equal(NiteLockFile.CurrentResolver, lockFile.Resolver);

        Assert.True(Installed("@tool/linux-x64-gnu"));
        Assert.False(Installed("@tool/linux-x64-musl"));
        Assert.False(Installed("@tool/darwin-arm64"));
    }

    [Fact]
    public async Task Moving_the_lockfile_to_another_platform_swaps_the_binaries()
    {
        var registry = ToolWithBinaries();
        await Install(registry, Config(("tool", "^1.0.0")), LinuxGlibc);
        await Install(registry, Config(("tool", "^1.0.0")), MacArm);

        Assert.True(Installed("@tool/darwin-arm64"));
        Assert.False(Installed("@tool/linux-x64-gnu"));
        Assert.True(Installed("tool"));
    }

    [Fact]
    public async Task A_package_any_required_path_reaches_is_not_optional()
    {
        var registry = new FakeRegistry()
            .Add("app", "1.0.0", dependencies: new() { ["shared"] = "^1.0.0" }, optionalDependencies: new() { ["extra"] = "^1.0.0" })
            .Add("extra", "1.0.0", dependencies: new() { ["shared"] = "^1.0.0", ["only-extra"] = "^1.0.0" })
            .Add("shared", "1.0.0")
            .Add("only-extra", "1.0.0");

        var lockFile = await Install(registry, Config(("app", "^1.0.0")), LinuxGlibc);

        Assert.Null(lockFile.Packages["node_modules/shared"].Optional);
        Assert.True(lockFile.Packages["node_modules/extra"].Optional);
        Assert.True(lockFile.Packages["node_modules/only-extra"].Optional);
    }

    [Fact]
    public async Task Re_resolves_an_older_lockfile_once_and_keeps_its_versions()
    {
        var registry = ToolWithBinaries().Add("tool", "1.1.0");
        var lockPath = Path.Combine(_project.FullName, PackageInstaller.LockFileName);
        await File.WriteAllTextAsync(lockPath, """
            {
              "lockfileVersion": 1,
              "requires": { "tool": "^1.0.0" },
              "packages": {
                "node_modules/tool": { "version": "1.0.0", "resolved": "https://registry.example.com/tool/-/tool-1.0.0.tgz", "integrity": "sha512-x" }
              }
            }
            """);

        var lockFile = await Install(registry, Config(("tool", "^1.0.0")), LinuxGlibc);

        Assert.Equal("1.0.0", lockFile.Packages["node_modules/tool"].Version);
        Assert.Contains("node_modules/@tool/linux-x64-gnu", lockFile.Packages.Keys);
        Assert.Contains("\"resolver\": 2", await File.ReadAllTextAsync(lockPath));
    }

    [Fact]
    public async Task Warns_about_a_required_package_for_another_platform()
    {
        var registry = new FakeRegistry().Add("mac-only", "1.0.0", os: ["darwin"]);
        var logger = new ListLogger();

        await new PackageInstaller(new HttpClient(registry), logger, platform: LinuxGlibc).InstallAsync(_project.FullName, Config(("mac-only", "^1.0.0")));

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("mac-only@1.0.0 is for darwin", warning.Message);
        Assert.False(Installed("mac-only"));
    }

    [Theory]
    [InlineData(new[] { "linux" }, true)]
    [InlineData(new[] { "darwin", "win32" }, false)]
    [InlineData(new[] { "!win32" }, true)]
    [InlineData(new[] { "!linux" }, false)]
    [InlineData(new string[0], true)]
    public void Matches_os_lists_like_npm(string[] os, bool expected) =>
        Assert.Equal(expected, LinuxGlibc.Supports(os, null, null));

    [Fact]
    public void Checks_libc_on_linux_only()
    {
        Assert.False(LinuxGlibc.Supports(["linux"], ["x64"], ["musl"]));
        Assert.True(new NpmPlatform("linux", "x64", "musl").Supports(["linux"], ["x64"], ["musl"]));
        Assert.True(MacArm.Supports(null, null, ["glibc"]));
    }

    [Theory]
    [InlineData("@rollup/rollup-linux-x64-musl", "musl")]
    [InlineData("lightningcss-linux-arm64-gnu", "glibc")]
    [InlineData("@rolldown/binding-linux-x64-gnu", "glibc")]
    [InlineData("@esbuild/linux-x64", null)]
    public void Reads_libc_from_the_name_when_the_manifest_does_not_say(string name, string? expected) =>
        Assert.Equal(expected, NpmPlatform.LibcOf(name, null)?.Single());

    private bool Installed(string name) => File.Exists(Path.Combine(_project.FullName, "node_modules", name, "package.json"));

    private static NiteConfig Config(params (string Name, string Range)[] dependencies) =>
        new() { Dependencies = dependencies.ToDictionary(d => d.Name, d => d.Range) };

    private Task<NiteLockFile> Install(FakeRegistry registry, NiteConfig config, NpmPlatform platform) =>
        new PackageInstaller(new HttpClient(registry), platform: platform).InstallAsync(_project.FullName, config);
}
