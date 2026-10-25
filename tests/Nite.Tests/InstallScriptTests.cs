using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Nite.Packages;

namespace Nite.Tests;

public class InstallScriptsTests
{
    [Theory]
    [InlineData("core-js", "only prints a message")]
    [InlineData("es5-ext", "only prints a message")]
    [InlineData("esbuild", "Nite downloads and runs itself")]
    [InlineData("node-sass", "Dart Sass")]
    [InlineData("bcrypt", "cannot run in a browser")]
    [InlineData("better-sqlite3", "cannot run in a browser")]
    [InlineData("left-pad", "may not work")]
    public void Explains_what_a_skipped_script_means(string name, string expected)
    {
        var message = InstallScripts.Describe(name, "1.2.3");
        Assert.StartsWith($"{name}@1.2.3 has an install script, which Nite does not run", message);
        Assert.Contains(expected, message);
    }

    [Fact]
    public void Only_known_harmless_scripts_are_not_warnings()
    {
        Assert.True(InstallScripts.IsHarmless("core-js"));
        Assert.True(InstallScripts.IsHarmless("esbuild"));
        Assert.False(InstallScripts.IsHarmless("bcrypt"));
        Assert.False(InstallScripts.IsHarmless("node-sass"));
        Assert.False(InstallScripts.IsHarmless("some-unknown-package"));
    }
}

public class PackageInstallerTests : IDisposable
{
    private readonly DirectoryInfo _project = Directory.CreateTempSubdirectory("nite-install-scripts-");

    public void Dispose() => _project.Delete(recursive: true);

    [Fact]
    public async Task Reports_packages_with_install_scripts_once_after_resolving()
    {
        var registry = new FakeRegistry()
            .Add("app", "1.0.0", dependencies: new() { ["core-js"] = "^3.0.0", ["bcrypt"] = "^5.0.0", ["plain"] = "^1.0.0", ["tool"] = "^2.0.0" })
            .Add("other", "1.0.0", dependencies: new() { ["tool"] = "^1.0.0" })
            .Add("core-js", "3.40.0", hasInstallScript: true)
            .Add("bcrypt", "5.1.1", hasInstallScript: true)
            .Add("plain", "1.0.0")
            .Add("tool", "1.0.0", hasInstallScript: true)
            .Add("tool", "2.0.0", hasInstallScript: true);
        var config = new NiteConfig { Dependencies = new() { ["app"] = "^1.0.0", ["other"] = "^1.0.0" } };
        var logger = new ListLogger();

        await new PackageInstaller(new HttpClient(registry), logger).InstallAsync(_project.FullName, config);

        var scripts = logger.Entries.Where(e => e.Message.Contains("install script")).ToList();
        Assert.Collection(scripts,
            e => { Assert.Equal(LogLevel.Warning, e.Level); Assert.Contains("bcrypt@5.1.1", e.Message); Assert.Contains("browser", e.Message); },
            e => { Assert.Equal(LogLevel.Information, e.Level); Assert.StartsWith("core-js@3.40.0", e.Message); },
            e => { Assert.Equal(LogLevel.Warning, e.Level); Assert.StartsWith("tool@1.0.0", e.Message); },
            e => { Assert.Equal(LogLevel.Warning, e.Level); Assert.StartsWith("tool@2.0.0", e.Message); });
        Assert.True(File.Exists(Path.Combine(_project.FullName, "node_modules", "bcrypt", "package.json")));

        // A current lockfile means no resolution, so no repeat of the messages.
        logger.Entries.Clear();
        await new PackageInstaller(new HttpClient(registry), logger).InstallAsync(_project.FullName, config);
        Assert.DoesNotContain(logger.Entries, e => e.Message.Contains("install script"));
    }

    private sealed class ListLogger : ILogger<PackageInstaller>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>Serves packuments and tarballs for made-up packages.</summary>
    internal sealed class FakeRegistry : HttpMessageHandler
    {
        private readonly Dictionary<string, Dictionary<string, object>> _versions = [];
        private readonly Dictionary<string, byte[]> _tarballs = [];

        public FakeRegistry Add(string name, string version, Dictionary<string, string>? dependencies = null, bool hasInstallScript = false)
        {
            var tarball = MakeTarball(name, version);
            var url = $"https://registry.example.com/{name}/-/{name}-{version}.tgz";
            _tarballs[url] = tarball;
            if (!_versions.TryGetValue(name, out var versions))
            {
                _versions[name] = versions = [];
            }
            versions[version] = new Dictionary<string, object?>
            {
                ["name"] = name,
                ["version"] = version,
                ["dependencies"] = dependencies,
                ["hasInstallScript"] = hasInstallScript ? true : null,
                ["dist"] = new { tarball = url, integrity = "sha512-" + Convert.ToBase64String(SHA512.HashData(tarball)) },
            }.Where(p => p.Value is not null).ToDictionary(p => p.Key, p => p.Value!);
            return this;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (_tarballs.TryGetValue(url, out var tarball))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(tarball) });
            }

            var name = Uri.UnescapeDataString(request.RequestUri.AbsolutePath.TrimStart('/'));
            if (!_versions.TryGetValue(name, out var versions))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            var packument = new { name, distTags = new { latest = versions.Keys.Last() }, versions };
            var json = JsonSerializer.Serialize(packument).Replace("\"distTags\"", "\"dist-tags\"");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }

        private static byte[] MakeTarball(string name, string version)
        {
            using var output = new MemoryStream();
            using (var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true))
            using (var tar = new TarWriter(gzip))
            {
                var manifest = Encoding.UTF8.GetBytes($$"""{"name":"{{name}}","version":"{{version}}"}""");
                tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "package/package.json") { DataStream = new MemoryStream(manifest) });
            }
            return output.ToArray();
        }
    }
}
