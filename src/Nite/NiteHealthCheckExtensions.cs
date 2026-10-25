using Microsoft.Extensions.DependencyInjection;
using Nite.Observability;

namespace Nite;

public static class NiteHealthCheckExtensions
{
    /// <summary>
    /// Adds <see cref="NiteHealthCheck"/>: <c>builder.Services.AddHealthChecks().AddNite()</c>.
    /// Aspire's ServiceDefaults serve all checks at <c>/health</c>, and only those tagged
    /// <c>live</c> at <c>/alive</c>, so by default this one gates readiness only.
    /// </summary>
    public static IHealthChecksBuilder AddNite(this IHealthChecksBuilder builder, string name = "nite", IEnumerable<string>? tags = null) =>
        builder.AddCheck<NiteHealthCheck>(name, tags: tags);
}
