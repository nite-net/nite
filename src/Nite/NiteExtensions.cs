using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Nite.Assets;
using Nite.Dev;

namespace Nite;

public enum NiteMode
{
    /// <summary>Dev server in the Development environment, build output everywhere else.</summary>
    Auto,
    Development,
    Production,
}

public sealed class NiteOptions
{
    public NiteMode Mode { get; set; } = NiteMode.Auto;

    /// <summary>The folder holding <c>nite.json</c>. Defaults to the content root.</summary>
    public string? ProjectDirectory { get; set; }

    /// <summary>
    /// Values for client code, worked out per request when a page is served and read with
    /// <c>import { config } from "nite:config"</c>. Unlike <c>NITE_PUBLIC_</c> variables, which are
    /// inlined when code is compiled, these come from the running server: settings that differ
    /// between environments (an Application Insights connection string, an API's URL), or context
    /// about the request (the signed-in user's name, a feature flag). They are serialized as camelCase
    /// JSON into the page, so return only what the browser may see.
    /// </summary>
    /// <remarks>
    /// <c>MapNiteSpa</c> and the <c>&lt;nite-head /&gt;</c> tag helper write them. Pages that carry
    /// them are sent with <c>Cache-Control: private</c>, since they may differ per user.
    /// </remarks>
    public Func<HttpContext, object?>? ClientConfig { get; set; }

    /// <summary>Like <see cref="ClientConfig"/>, for values that take awaiting. Set one or the other.</summary>
    public Func<HttpContext, Task<object?>>? ClientConfigAsync { get; set; }

    /// <summary>
    /// The request's Content-Security-Policy nonce, for a policy that allows scripts and styles by
    /// nonce. Nite puts it on what it writes into pages (the development import map and scripts,
    /// <c>&lt;nite-head /&gt;</c>'s scripts), and the development runtime puts it on the
    /// <c>&lt;style&gt;</c> elements it creates. Production builds need no inline code, so a policy
    /// of <c>'self'</c> plus the nonce covers both.
    /// </summary>
    public Func<HttpContext, string?>? CspNonce { get; set; }
}

public static class NiteExtensions
{
    internal static readonly HttpClient Http = CreateHttpClient();

    /// <summary>Registers Nite's services and tag helper dependencies.</summary>
    public static IServiceCollection AddNite(this IServiceCollection services, Action<NiteOptions>? configure = null)
    {
        var options = services.AddOptions<NiteOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<NiteOptions>>().Value;
            var environment = sp.GetRequiredService<IWebHostEnvironment>();
            var project = new NiteProject(settings.ProjectDirectory ?? environment.ContentRootPath);
            return new NiteDevServer(project, Http, sp.GetRequiredService<ILoggerFactory>());
        });
        services.TryAddSingleton<ProductionAssets>();
        services.TryAddSingleton<INiteAssets>(sp => IsDevelopment(sp)
            ? sp.GetRequiredService<NiteDevServer>()
            : sp.GetRequiredService<ProductionAssets>());
        return services;
    }

    /// <summary>
    /// In development, serves compiled modules, dependencies and the HMR socket under <c>/_nite</c>.
    /// In production this does nothing: the build output is ordinary static files in <c>wwwroot/_nite</c>.
    /// Call it before <c>UseRouting</c>/<c>MapStaticAssets</c>.
    /// </summary>
    public static IApplicationBuilder UseNite(this IApplicationBuilder app)
    {
        if (!IsDevelopment(app.ApplicationServices))
        {
            return app;
        }

        var server = app.ApplicationServices.GetRequiredService<NiteDevServer>();
        server.Start(HostStarted(app.ApplicationServices));
        app.Map(NiteProject.BasePath, branch =>
        {
            branch.UseWebSockets();
            branch.Run(server.HandleAsync);
        });
        return app;
    }

    /// <summary>
    /// Serves an HTML entry from the client root (default <c>index.html</c>) for <c>/</c> and, as a
    /// fallback, for any other path without a file extension, so client-side routes survive a
    /// refresh. Real endpoints always win. Requests under <paramref name="apiPrefix"/> never fall
    /// back, so a mistyped API call gets a 404 instead of a page of HTML.
    /// </summary>
    /// <remarks>
    /// In development the page is read from source on every request with the dev client injected;
    /// in production it is the built copy from <c>wwwroot/_nite</c>.
    /// </remarks>
    /// <param name="path">
    /// Serve the page only under this path: <c>"/login"</c> answers <c>/login</c> and
    /// <c>/login/…</c>. An app can map several single-page apps this way, plus one without a path
    /// for everything else; the more specific path wins.
    /// </param>
    public static IEndpointConventionBuilder MapNiteSpa(this IEndpointRouteBuilder endpoints, string page = "index.html", string? apiPrefix = "/api", string? path = null)
    {
        RequestDelegate serve = async context =>
        {
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                || (apiPrefix is not null && context.Request.Path.StartsWithSegments(apiPrefix)))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var assets = context.RequestServices.GetRequiredService<INiteAssets>();
            var html = await assets.GetHtmlAsync(page, context);
            var config = await NiteClientConfig.RenderAsync(context);
            if (config is not null)
            {
                html = Html.HtmlDocument.InjectHead(html, config);
            }
            // Never cache the page itself: it is what points at the current hashed bundles. With
            // client config it may be someone's own (their claims, a form token), so no cache at
            // all may keep it, the browser's own included.
            context.Response.Headers.CacheControl = config is null ? "no-cache" : "private, no-store";
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(html);
        };

        return path is null
            ? endpoints.MapFallback(serve)
            : endpoints.MapFallback($"{path.TrimEnd('/')}/{{**clientPath:nonfile}}", serve);
    }

    /// <summary>The request's CSP nonce, from <see cref="NiteOptions.CspNonce"/>.</summary>
    internal static string? CspNonce(HttpContext context) =>
        context.RequestServices.GetService<IOptions<NiteOptions>>()?.Value.CspNonce?.Invoke(context);

    /// <summary>
    /// Completes when every hosted service and the server have started, which is when
    /// OpenTelemetry starts listening. Null without a host (the dev server then starts at once).
    /// </summary>
    private static Task? HostStarted(IServiceProvider services)
    {
        if (services.GetService<IHostApplicationLifetime>() is not { } lifetime)
        {
            return null;
        }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Runs at once if the host has already started.
        lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        return started.Task;
    }

    private static bool IsDevelopment(IServiceProvider services)
    {
        var mode = services.GetRequiredService<IOptions<NiteOptions>>().Value.Mode;
        return mode switch
        {
            NiteMode.Development => true,
            NiteMode.Production => false,
            _ => services.GetRequiredService<IWebHostEnvironment>().IsDevelopment(),
        };
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"nite/{typeof(NiteExtensions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"}");
        return client;
    }
}
