using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
