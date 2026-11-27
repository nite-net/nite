using System.Net;
using System.Net.WebSockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Nite.Dev;
using Nite.Testing;

namespace Nite.Tests;

public class VitestUiProxyTests : IAsyncLifetime
{
    private const string Token = "secret-token";
    private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("nite-vitest-ui-");
    private WebApplication _vitest = null!;
    private WebApplication _app = null!;
    private Uri _appAddress = null!;
    private int _vitestPort;

    private NiteProject Project => new(_root.FullName);

    public async Task InitializeAsync()
    {
        File.WriteAllText(Path.Combine(_root.FullName, "nite.json"), "{}");

        // Stands in for Vitest: a cookie-guarded page under the UI base, and a token-guarded socket at the root.
        _vitest = Host(app =>
        {
            app.UseWebSockets();
            app.Run(async context =>
            {
                if (context.Request.Path == VitestUi.ApiPath)
                {
                    if (context.Request.Query["token"] != Token)
                    {
                        context.Response.StatusCode = 401;
                        return;
                    }
                    using var socket = await context.WebSockets.AcceptWebSocketAsync();
                    var buffer = new byte[256];
                    var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
                    await socket.SendAsync(Encoding.UTF8.GetBytes("echo:" + Encoding.UTF8.GetString(buffer, 0, received.Count)), WebSocketMessageType.Text, true, CancellationToken.None);
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    return;
                }
                if (context.Request.Query["token"] == Token)
                {
                    context.Response.Headers.SetCookie = $"vitest-ui-token={Token}; Path={VitestUi.BasePath}; HttpOnly";
                    context.Response.Redirect(VitestUi.BasePath);
                    return;
                }
                if (context.Request.Cookies["vitest-ui-token"] != Token)
                {
                    context.Response.StatusCode = 403;
                    return;
                }
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync($"ui {context.Request.Method} {context.Request.Path}");
            });
        });
        await _vitest.StartAsync();
        _vitestPort = Address(_vitest).Port;

        // The app side: what UseNite wires up, without the rest of the dev server.
        _app = Host(app =>
        {
            var proxy = new VitestUiProxy(Project);
            app.UseWebSockets();
            app.Map("/_nite/vitest", branch => branch.Run(context => proxy.ProxyAsync(context, "/_nite/vitest" + context.Request.Path)));
            app.Map("/_nite/coverage", branch => branch.Run(context => proxy.ServeCoverageAsync(context, context.Request.Path.Value!.TrimStart('/'))));
            app.Run(context => proxy.ProxyAsync(context, context.Request.Path.Value!));
        });
        await _app.StartAsync();
        _appAddress = Address(_app);
    }

    public async Task DisposeAsync()
    {
        await _app.DisposeAsync();
        await _vitest.DisposeAsync();
        _root.Delete(recursive: true);
    }

    private static WebApplication Host(Action<WebApplication> configure)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        configure(app);
        return app;
    }

    private static Uri Address(WebApplication app) =>
        new(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());

    private void Running() => VitestUi.Write(Project, new VitestUi(_vitestPort, Environment.ProcessId));

    private HttpClient Client() => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = _appAddress };

    [Fact]
    public async Task Passes_the_token_and_cookie_through_to_vitest()
    {
        Running();
        using var client = Client();

        var login = await client.GetAsync("/_nite/vitest/?token=" + Token);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal(VitestUi.BasePath, login.Headers.Location!.OriginalString);
        var cookie = Assert.Single(login.Headers.GetValues("Set-Cookie"));
        Assert.Contains($"Path={VitestUi.BasePath}", cookie);

        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/_nite/vitest/")).StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Get, "/_nite/vitest/assets/x.js");
        request.Headers.Add("Cookie", cookie.Split(';')[0]);
        var page = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("ui GET /_nite/vitest/assets/x.js", await page.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Relays_the_api_socket_and_leaves_the_token_check_to_vitest()
    {
        Running();
        var api = new UriBuilder(_appAddress) { Scheme = "ws", Path = VitestUi.ApiPath };

        using (var socket = new ClientWebSocket())
        {
            await socket.ConnectAsync(new UriBuilder(api.Uri) { Query = "token=" + Token }.Uri, CancellationToken.None);
            await socket.SendAsync("hello"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
            var buffer = new byte[256];
            var received = await socket.ReceiveAsync(buffer, CancellationToken.None);
            Assert.Equal("echo:hello", Encoding.UTF8.GetString(buffer, 0, received.Count));
        }

        using var refused = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<WebSocketException>(() => refused.ConnectAsync(new UriBuilder(api.Uri) { Query = "token=wrong" }.Uri, CancellationToken.None));
    }

    [Fact]
    public async Task Refuses_pages_from_other_origins()
    {
        Running();
        using var client = Client();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/_nite/vitest/?token=" + Token);
        request.Headers.Add("Origin", "http://attacker.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(request)).StatusCode);

        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Origin", "http://attacker.example");
        var api = new UriBuilder(_appAddress) { Scheme = "ws", Path = VitestUi.ApiPath, Query = "token=" + Token };
        await Assert.ThrowsAnyAsync<WebSocketException>(() => socket.ConnectAsync(api.Uri, CancellationToken.None));
    }

    [Fact]
    public async Task Says_how_to_start_vitest_when_it_is_not_running()
    {
        // A file left by a process that has exited counts as not running.
        VitestUi.Write(Project, new VitestUi(_vitestPort, ProcessId: int.MaxValue));
        Assert.Null(VitestUi.Find(Project));

        using var client = Client();
        var response = await client.GetAsync("/_nite/vitest/");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("nite test --ui", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Serves_the_coverage_report_and_nothing_outside_it()
    {
        using var client = Client();
        var empty = await client.GetAsync("/_nite/coverage/");
        Assert.Equal(HttpStatusCode.NotFound, empty.StatusCode);
        Assert.Contains("nite test --coverage", await empty.Content.ReadAsStringAsync());

        var report = Path.Combine(_root.FullName, ".nite", "coverage");
        Directory.CreateDirectory(Path.Combine(report, "src"));
        File.WriteAllText(Path.Combine(report, "index.html"), "<title>All files</title>");
        File.WriteAllText(Path.Combine(report, "src", "index.html"), "src files");
        File.WriteAllText(Path.Combine(report, "base.css"), "body{}");

        Assert.Equal("<title>All files</title>", await client.GetStringAsync("/_nite/coverage/"));
        Assert.Equal("src files", await client.GetStringAsync("/_nite/coverage/src/"));
        var css = await client.GetAsync("/_nite/coverage/base.css");
        Assert.Equal("text/css", css.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/_nite/coverage/..%2F..%2Fnite.json")).StatusCode);
    }
}
