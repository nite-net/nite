using System.Net;
using System.Text;
using Nite.Configuration;
using Nite.Packages;

namespace Nite.Tests;

public class NiteDirectoriesTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] values) =>
        key => values.FirstOrDefault(v => v.Key == key).Value;

    [Fact]
    public void Uses_xdg_defaults_on_unix()
    {
        var directories = NiteDirectories.Resolve(Env(), isWindows: false, "/home/tim", null, null);
        Assert.Equal(new NiteDirectories("/home/tim/.config/nite", "/home/tim/.cache/nite", "/home/tim/.local/share/nite", "/home/tim/.local/state/nite"), directories);
    }

    [Fact]
    public void Honors_absolute_xdg_variables_and_ignores_relative_ones()
    {
        var directories = NiteDirectories.Resolve(
            Env(("XDG_CONFIG_HOME", "/xdg/config"), ("XDG_CACHE_HOME", "relative/cache")),
            isWindows: false, "/home/tim", null, null);
        Assert.Equal("/xdg/config/nite", directories.Config);
        Assert.Equal("/home/tim/.cache/nite", directories.Cache);
    }

    [Fact]
    public void Nite_variables_win_over_xdg()
    {
        var directories = NiteDirectories.Resolve(
            Env(("NITE_CACHE_DIR", "/ci/cache"), ("XDG_CACHE_HOME", "/xdg/cache")),
            isWindows: false, "/home/tim", null, null);
        Assert.Equal("/ci/cache", directories.Cache);
    }

    [Fact]
    public void Uses_appdata_on_windows_unless_xdg_is_set()
    {
        var roaming = Path.Combine("/users/tim", "AppData", "Roaming");
        var local = Path.Combine("/users/tim", "AppData", "Local");
        var directories = NiteDirectories.Resolve(Env(), isWindows: true, "/users/tim", roaming, local);
        Assert.Equal(Path.Combine(roaming, "nite"), directories.Config);
        Assert.Equal(Path.Combine(local, "nite", "cache"), directories.Cache);

        var withXdg = NiteDirectories.Resolve(Env(("XDG_CONFIG_HOME", "/xdg")), isWindows: true, "/users/tim", roaming, local);
        Assert.Equal("/xdg/nite", withXdg.Config);
    }

    [Fact]
    public void Survives_a_missing_home_directory()
    {
        var directories = NiteDirectories.Resolve(Env(), isWindows: false, "", null, null);
        Assert.StartsWith(Path.GetTempPath(), directories.Cache);
    }
}

public class NpmrcTests
{
    [Fact]
    public void Parses_keys_comments_quotes_and_variables()
    {
        var values = Npmrc.Parse("""
            # comment
            ; also a comment
            registry=https://registry.example.com/
            @acme:registry = "https://npm.acme.dev/"
            //npm.acme.dev/:_authToken=${ACME_TOKEN}
            //other.dev/:_authToken=${MISSING?}
            [section]
            strict-ssl=false
            not a key value line
            """, name => name == "ACME_TOKEN" ? "secret-123" : null);

        Assert.Equal("https://registry.example.com/", values["registry"]);
        Assert.Equal("https://npm.acme.dev/", values["@acme:registry"]);
        Assert.Equal("secret-123", values["//npm.acme.dev/:_authToken"]);
        Assert.Equal("", values["//other.dev/:_authToken"]);
        Assert.Equal("false", values["strict-ssl"]);
        Assert.Equal(5, values.Count);
    }
}

public class RegistrySettingsTests
{
    private static RegistrySettings Settings(params (string Key, string Value)[] npmrc) =>
        new("https://registry.npmjs.org", new Dictionary<string, string> { ["@acme"] = "https://npm.acme.dev/private" },
            RegistryCredential.From(npmrc.ToDictionary(p => p.Key, p => p.Value)));

    [Fact]
    public void Routes_scoped_packages_to_their_registry()
    {
        var settings = Settings();
        Assert.Equal("https://npm.acme.dev/private/", settings.RegistryFor("@acme/ui"));
        Assert.Equal("https://registry.npmjs.org/", settings.RegistryFor("@other/ui"));
        Assert.Equal("https://registry.npmjs.org/", settings.RegistryFor("react"));
    }

    [Fact]
    public void Sends_credentials_only_under_their_host_and_path()
    {
        var settings = Settings(("//npm.acme.dev/private/:_authToken", "acme-token"));

        Assert.Equal("Bearer acme-token", settings.AuthorizationFor(new Uri("https://npm.acme.dev/private/@acme%2fui"))?.ToString());
        Assert.Equal("Bearer acme-token", settings.AuthorizationFor(new Uri("https://npm.acme.dev/private/@acme/ui/-/ui-1.0.0.tgz"))?.ToString());

        Assert.Null(settings.AuthorizationFor(new Uri("https://registry.npmjs.org/react")));
        Assert.Null(settings.AuthorizationFor(new Uri("https://npm.acme.dev/public/x")));          // other path
        Assert.Null(settings.AuthorizationFor(new Uri("https://npm.acme.dev.evil.com/private/x"))); // lookalike host
        Assert.Null(settings.AuthorizationFor(new Uri("https://npm.acme.dev:8443/private/x")));    // other port
    }

    [Fact]
    public void Picks_the_most_specific_credential()
    {
        var settings = Settings(("//npm.acme.dev/:_authToken", "host-wide"), ("//npm.acme.dev/private/:_authToken", "private-only"));
        Assert.Equal("Bearer private-only", settings.AuthorizationFor(new Uri("https://npm.acme.dev/private/x"))?.ToString());
        Assert.Equal("Bearer host-wide", settings.AuthorizationFor(new Uri("https://npm.acme.dev/other/x"))?.ToString());
    }

    [Fact]
    public void Builds_basic_auth_from_username_and_password()
    {
        var password = Convert.ToBase64String(Encoding.UTF8.GetBytes("hunter2"));
        var settings = Settings(("//npm.acme.dev:8443/:username", "tim"), ("//npm.acme.dev:8443/:_password", password));
        var header = settings.AuthorizationFor(new Uri("https://npm.acme.dev:8443/@acme%2fui"));
        Assert.Equal("Basic", header?.Scheme);
        Assert.Equal("tim:hunter2", Encoding.UTF8.GetString(Convert.FromBase64String(header!.Parameter!)));
    }
}

public class NiteSettingsTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("nite-settings-");
    private string Home => Path.Combine(_root.FullName, "home");
    private string Project => Path.Combine(_root.FullName, "project");
    private NiteDirectories Directories => new(Path.Combine(Home, ".config", "nite"), Path.Combine(Home, ".cache", "nite"), "", "");

    public NiteSettingsTests()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Project);
    }

    public void Dispose() => _root.Delete(recursive: true);

    private NiteSettings Load(params (string Key, string Value)[] environment) =>
        NiteSettings.Load(Project, Directories, key =>
            key == "NPM_CONFIG_USERCONFIG" ? Path.Combine(Home, ".npmrc") : environment.FirstOrDefault(e => e.Key == key).Value);

    [Fact]
    public void Defaults_to_the_public_registry()
    {
        var settings = Load();
        Assert.Equal(RegistrySettings.NpmRegistry, settings.Get(NiteSettings.RegistryKey));
        Assert.Equal("default", settings.GetWithOrigin(NiteSettings.RegistryKey)!.Origin);
        Assert.Equal("spa", settings.Get(NiteSettings.InitTemplateKey));
    }

    [Fact]
    public void Layers_apply_in_order_with_origins()
    {
        File.WriteAllText(Path.Combine(Home, ".npmrc"), "registry=https://user-npmrc/\n@acme:registry=https://acme-user/\n//acme-user/:_authToken=t1\n");
        Assert.Equal("https://user-npmrc/", Load().Get(NiteSettings.RegistryKey));

        NiteSettings.SetUserValue(NiteSettings.RegistryKey, "https://user-config/", Directories);
        Assert.Equal("https://user-config/", Load().Get(NiteSettings.RegistryKey));

        File.WriteAllText(Path.Combine(Project, ".npmrc"), "registry=https://project-npmrc/\n");
        Assert.Equal("https://project-npmrc/", Load().Get(NiteSettings.RegistryKey));

        File.WriteAllText(Path.Combine(Project, "nite.json"), "{ \"registry\": \"https://nite-json/\" }");
        var settings = Load();
        Assert.Equal("https://nite-json/", settings.Get(NiteSettings.RegistryKey));
        Assert.Equal(Path.Combine(Project, "nite.json"), settings.GetWithOrigin(NiteSettings.RegistryKey)!.Origin);

        settings = Load(("NITE_REGISTRY", "https://env/"));
        Assert.Equal("https://env/", settings.Get(NiteSettings.RegistryKey));
        Assert.Equal("NITE_REGISTRY", settings.GetWithOrigin(NiteSettings.RegistryKey)!.Origin);

        // Untouched by the layers above: the scope and its credential from ~/.npmrc.
        Assert.Equal("https://acme-user/", settings.Registry.RegistryFor("@acme/x"));
        Assert.True(settings.GetWithOrigin("//acme-user/:_authToken")!.Secret);
        Assert.NotNull(settings.Registry.AuthorizationFor(new Uri("https://acme-user/@acme%2fx")));
    }

    [Fact]
    public void User_config_round_trips_and_refuses_credentials()
    {
        NiteSettings.SetUserValue("@acme:registry", "https://npm.acme.dev/", Directories);
        NiteSettings.SetUserValue(NiteSettings.InitTemplateKey, "islands", Directories);
        var settings = Load();
        Assert.Equal("https://npm.acme.dev/", settings.Registry.RegistryFor("@acme/ui"));
        Assert.Equal("islands", settings.Get(NiteSettings.InitTemplateKey));

        Assert.True(NiteSettings.UnsetUserValue(NiteSettings.InitTemplateKey, Directories));
        Assert.False(NiteSettings.UnsetUserValue(NiteSettings.InitTemplateKey, Directories));
        Assert.Equal("spa", Load().Get(NiteSettings.InitTemplateKey));

        var error = Assert.Throws<InvalidOperationException>(() => NiteSettings.SetUserValue("//npm.acme.dev/:_authToken", "x", Directories));
        Assert.Contains(".npmrc", error.Message);
        Assert.Throws<InvalidOperationException>(() => NiteSettings.SetUserValue("colour", "blue", Directories));
    }
}

public class NpmRegistryTests : IDisposable
{
    private readonly DirectoryInfo _cache = Directory.CreateTempSubdirectory("nite-registry-");

    public void Dispose() => _cache.Delete(recursive: true);

    private const string Packument = """{"name":"x","dist-tags":{"latest":"1.0.0"},"versions":{"1.0.0":{"name":"x","version":"1.0.0","dist":{"tarball":"https://cdn.example.com/x-1.0.0.tgz"}}}}""";

    [Fact]
    public async Task Uses_the_scope_registry_with_its_token_only()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Packument) });
        var settings = new RegistrySettings("https://registry.npmjs.org/", new Dictionary<string, string> { ["@acme"] = "https://npm.acme.dev/" },
            RegistryCredential.From(new Dictionary<string, string> { ["//npm.acme.dev/:_authToken"] = "acme-token" }));
        var registry = new NpmRegistry(new HttpClient(handler), settings, _cache.FullName);

        await registry.GetPackumentAsync("@acme/ui");
        await registry.GetPackumentAsync("react");

        Assert.Equal("https://npm.acme.dev/@acme%2Fui", handler.Requests[0].Url, ignoreCase: true);
        Assert.Equal("Bearer acme-token", handler.Requests[0].Authorization);
        Assert.Equal("https://registry.npmjs.org/react", handler.Requests[1].Url);
        Assert.Null(handler.Requests[1].Authorization);
    }

    [Fact]
    public async Task Revalidates_cached_metadata_with_its_etag()
    {
        var calls = 0;
        var handler = new RecordingHandler(request =>
        {
            calls++;
            if (request.Headers.IfNoneMatch.Any(e => e.Tag == "\"v1\""))
            {
                return new HttpResponseMessage(HttpStatusCode.NotModified);
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Packument) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"v1\"");
            return response;
        });

        var first = await new NpmRegistry(new HttpClient(handler), cacheDirectory: _cache.FullName).GetPackumentAsync("x");
        var second = await new NpmRegistry(new HttpClient(handler), cacheDirectory: _cache.FullName).GetPackumentAsync("x");

        Assert.Equal(2, calls);
        Assert.Null(handler.Requests[0].IfNoneMatch);
        Assert.Equal("\"v1\"", handler.Requests[1].IfNoneMatch);
        Assert.Equal("1.0.0", second.DistTags["latest"]);
        Assert.Equal(first.Versions.Keys, second.Versions.Keys);
    }

    [Fact]
    public async Task Explains_missing_credentials()
    {
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        var registry = new NpmRegistry(new HttpClient(handler), cacheDirectory: _cache.FullName);
        var error = await Assert.ThrowsAsync<NitePackageException>(() => registry.GetPackumentAsync("@acme/secret"));
        Assert.Contains(".npmrc", error.Message);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Url, string? Authorization, string? IfNoneMatch)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString(), request.Headers.IfNoneMatch.FirstOrDefault()?.Tag));
            return Task.FromResult(respond(request));
        }
    }
}
