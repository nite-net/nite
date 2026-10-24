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
        server.Start();
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
    public static IEndpointConventionBuilder MapNiteSpa(this IEndpointRouteBuilder endpoints, string page = "index.html", string? apiPrefix = "/api")
    {
        return endpoints.MapFallback(async context =>
        {
            if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
                || (apiPrefix is not null && context.Request.Path.StartsWithSegments(apiPrefix)))
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var assets = context.RequestServices.GetRequiredService<INiteAssets>();
            var html = await assets.GetHtmlAsync(page, context);
            // Never cache the page itself: it is what points at the current hashed bundles.
            context.Response.Headers.CacheControl = "no-cache";
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(html);
        });
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
