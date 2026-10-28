using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nite.Assets;
using Nite.Build;
using Nite.Esbuild;

namespace Nite.Tests;

public class ClientConfigTests
{
    [Fact]
    public void Cannot_close_the_script_element_early()
    {
        var html = NiteClientConfig.Render(new { Name = "</script><script>alert(1)</script>" });

        Assert.StartsWith("<script type=\"application/json\" id=\"nite-config\">{\"name\":", html);
        Assert.EndsWith("</script>", html);
        Assert.Equal(1, html.Split("</script>").Length - 1);
    }

    [Fact]
    public void Puts_the_csp_nonce_on_every_script()
    {
        var head = new NiteHead(new Dictionary<string, string> { ["react"] = "/_nite/deps/react.js" }, ["/_nite/chunk.js"], ["/_nite/client.js"]);

        var html = head.ToHtml("abc123");

        Assert.Contains("<script type=\"importmap\" nonce=\"abc123\">", html);
        Assert.Contains("<script type=\"module\" nonce=\"abc123\" src=\"/_nite/client.js\">", html);
        Assert.DoesNotContain("nonce", head.ToHtml());
    }

    [Fact]
    public async Task Writes_nothing_without_client_config()
    {
        Assert.Null(await NiteClientConfig.RenderAsync(Context(_ => { })));
    }

    [Fact]
    public async Task Runs_the_sync_or_async_callback_per_request()
    {
        var sync = await NiteClientConfig.RenderAsync(Context(o => o.ClientConfig = c => new { Path = c.Request.Path.Value }, "/a"));
        var async = await NiteClientConfig.RenderAsync(Context(o => o.ClientConfigAsync = async c =>
        {
            await Task.Yield();
            return new { Path = c.Request.Path.Value };
        }, "/b"));

        Assert.Contains("{\"path\":\"/a\"}", sync);
        Assert.Contains("{\"path\":\"/b\"}", async);
    }

    [Fact]
    public async Task Refuses_both_callbacks()
    {
        var context = Context(o =>
        {
            o.ClientConfig = _ => 1;
            o.ClientConfigAsync = _ => Task.FromResult<object?>(2);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => NiteClientConfig.RenderAsync(context).AsTask());
    }

    private static DefaultHttpContext Context(Action<NiteOptions> configure, string path = "/")
    {
        var services = new ServiceCollection();
        services.AddOptions<NiteOptions>().Configure(configure);
        return new DefaultHttpContext { RequestServices = services.BuildServiceProvider(), Request = { Path = path } };
    }
}

[Trait("Category", "Integration")]
public class ClientConfigIntegrationTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("nite-client-config-");

    public void Dispose() => _root.Delete(recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public async Task Production_builds_bundle_the_nite_config_module()
    {
        Write("nite.json", "{}");
        Write("Client/index.html", "<!doctype html><html><head></head><body><script type=\"module\" src=\"./main.ts\"></script></body></html>");
        Write("Client/main.ts", "import { config } from \"nite:config\";\ndocument.title = String((config as any).greeting);\n");

        await using var esbuild = new EsbuildService(await EsbuildBinary.EnsureAsync(new HttpClient()));
        var manifest = await new NiteBuilder(esbuild).BuildAsync(new NiteProject(_root.FullName));

        var output = File.ReadAllText(Path.Combine(_root.FullName, "wwwroot", "_nite", manifest.Entries["main.ts"].File));
        Assert.Contains("nite-config", output);
        Assert.DoesNotContain("nite:config", output);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MapNiteSpa_writes_the_values_into_each_page(bool withConfig)
    {
        Write("wwwroot/_nite/index.html", "<!doctype html><html><head><title>t</title></head><body></body></html>");
        Write("wwwroot/_nite/manifest.json", "{\"html\":{\"index.html\":\"index.html\"}}");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = _root.FullName, EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddNite(options =>
        {
            if (withConfig)
            {
                options.ClientConfig = context => new { Route = context.Request.Path.Value };
            }
        });
        await using var app = builder.Build();
        app.UseNite();
        app.MapNiteSpa();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        using var response = await http.GetAsync("/todos/3");
        var html = await response.Content.ReadAsStringAsync();

        // Pages carrying client config may be per user: no cache may keep them, the browser's included.
        var cache = response.Headers.CacheControl!;
        Assert.Equal(withConfig, cache.Private && cache.NoStore);
        Assert.Equal(!withConfig, cache.NoCache);
        if (withConfig)
        {
            Assert.Contains("<script type=\"application/json\" id=\"nite-config\">{\"route\":\"/todos/3\"}</script>", html);
            Assert.True(html.IndexOf("nite-config", StringComparison.Ordinal) < html.IndexOf("<title>", StringComparison.Ordinal));
        }
        else
        {
            Assert.DoesNotContain("nite-config", html);
        }
        await app.StopAsync();
    }
}

public class MapNiteSpaTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("nite-spa-routes-");

    public void Dispose() => _root.Delete(recursive: true);

    [Theory]
    [InlineData("/", "main app")]
    [InlineData("/todos/3", "main app")]
    [InlineData("/login", "login app")]
    [InlineData("/login/", "login app")]
    [InlineData("/login/forgot", "login app")]
    [InlineData("/loginx", "main app")]
    [InlineData("/api/missing", null)]
    [InlineData("/login/logo.png", null)]
    public async Task Serves_each_spa_under_its_own_path(string url, string? expected)
    {
        Write("wwwroot/_nite/index.html", "<html><head></head><body>main app</body></html>");
        Write("wwwroot/_nite/login/index.html", "<html><head></head><body>login app</body></html>");
        Write("wwwroot/_nite/manifest.json", "{\"html\":{\"index.html\":\"index.html\",\"login/index.html\":\"login/index.html\"}}");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = _root.FullName, EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddNite();
        await using var app = builder.Build();
        app.UseNite();
        app.MapNiteSpa("login/index.html", path: "/login");
        app.MapNiteSpa();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient { BaseAddress = new Uri(address) };
        using var response = await http.GetAsync(url);

        if (expected is null)
        {
            Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        }
        else
        {
            Assert.Contains(expected, await response.Content.ReadAsStringAsync());
        }
        await app.StopAsync();
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}

public class ImmutableAssetsTests : IDisposable
{
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("nite-immutable-");

    public void Dispose() => _root.Delete(recursive: true);

    [Theory]
    [InlineData("/_nite/main-LCLEEUI3.js", true)]
    [InlineData("/_nite/main-LCLEEUI3.js.map", true)]
    [InlineData("/_nite/chunks/chunk-WNT7WH4P.js", true)]
    [InlineData("/_nite/login/main-MEX5WWZR.js", true)]
    [InlineData("/_nite/assets/logo-ABCDEF27.svg", true)]
    [InlineData("/_nite/styles-EVDLSMZE.css", true)]
    [InlineData("/_nite/index.html", false)]
    [InlineData("/_nite/report-ABCDEFGH.html", false)]
    [InlineData("/_nite/manifest.json", false)]
    [InlineData("/_nite/main-lcleeui3.js", false)]
    [InlineData("/_nite/main-LCLEEUI9.js", false)]
    [InlineData("/_nite/main-LCLEEU.js", false)]
    [InlineData("/assets/app-LCLEEUI3.js", false)]
    public void Knows_which_build_outputs_have_a_content_hash(string path, bool hashed) =>
        Assert.Equal(hashed, Nite.Assets.ImmutableAssets.IsHashed(path));

    [Fact]
    public async Task Production_serves_hashed_files_as_immutable_and_everything_else_as_before()
    {
        Write("wwwroot/_nite/main-LCLEEUI3.js", "console.log(1);");
        Write("wwwroot/_nite/manifest.json", "{}");
        Write("wwwroot/_nite/index.html", "<!doctype html>");

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = _root.FullName, EnvironmentName = "Production" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddNite();
        await using var app = builder.Build();
        app.UseNite();
        app.UseStaticFiles();
        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient { BaseAddress = new Uri(address) };

        using var hashed = await http.GetAsync("/_nite/main-LCLEEUI3.js");
        Assert.Equal(HttpStatusCode.OK, hashed.StatusCode);
        Assert.Equal("public, max-age=31536000, immutable", hashed.Headers.CacheControl!.ToString());

        // A conditional request still gets the header on its 304.
        using var conditional = new HttpRequestMessage(HttpMethod.Get, "/_nite/main-LCLEEUI3.js");
        conditional.Headers.IfNoneMatch.Add(hashed.Headers.ETag!);
        using var notModified = await http.SendAsync(conditional);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);
        Assert.Contains(notModified.Headers.CacheControl!.Extensions, e => e.Name == "immutable");

        foreach (var path in new[] { "/_nite/manifest.json", "/_nite/index.html" })
        {
            using var other = await http.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, other.StatusCode);
            Assert.Null(other.Headers.CacheControl);
        }

        // A mistyped hash is a 404 that nothing may keep for a year.
        using var missing = await http.GetAsync("/_nite/main-AAAAAAAA.js");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Null(missing.Headers.CacheControl);
        await app.StopAsync();
    }

    private void Write(string relative, string text)
    {
        var path = Path.Combine(_root.FullName, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }
}
