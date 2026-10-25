using Microsoft.Extensions.Diagnostics.HealthChecks;
using Nite.Assets;
using Nite.Dev;

namespace Nite.Observability;

/// <summary>
/// Reports whether Nite can serve the app. In development: Degraded while packages install or
/// dependencies pre-bundle, Unhealthy if that failed (a typo in nite.json, a package that does
/// not exist) or if <c>UseNite</c> never ran. In production: Unhealthy if the build output is
/// missing, which usually means the publish did not run <c>nite build</c>.
/// </summary>
/// <remarks>
/// A readiness check, not a liveness one: a broken nite.json is not fixed by restarting the app.
/// Register it with <see cref="NiteHealthCheckExtensions.AddNite"/>.
/// </remarks>
public sealed class NiteHealthCheck(INiteAssets assets) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(assets switch
        {
            NiteDevServer server => server.CheckHealth(),
            ProductionAssets production => production.CheckHealth(),
            _ => HealthCheckResult.Healthy("Nite is served by a custom INiteAssets."),
        });
}
