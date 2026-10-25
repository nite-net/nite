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

        // Pages carrying client config may be per user: no shared cache may keep them.
        Assert.True(response.Headers.CacheControl!.NoCache);
        Assert.Equal(withConfig, response.Headers.CacheControl.Private);
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
