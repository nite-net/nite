using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Nite.Observability;

/// <summary>
/// Nite's traces and metrics, through .NET's own <see cref="ActivitySource"/> and <see cref="Meter"/>,
/// so the library needs no OpenTelemetry package. To collect them, add the names to your
/// OpenTelemetry setup (in an Aspire app, the ServiceDefaults project):
/// <code>
/// .WithTracing(tracing => tracing.AddSource(NiteTelemetry.SourceName))
/// .WithMetrics(metrics => metrics.AddMeter(NiteTelemetry.MeterName))
/// </code>
/// Nothing is recorded, and nothing costs anything, until a listener subscribes.
/// </summary>
public static class NiteTelemetry
{
    /// <summary>The <see cref="ActivitySource"/> name of Nite's spans.</summary>
    public const string SourceName = "Nite";

    /// <summary>The <see cref="Meter"/> name of Nite's metrics.</summary>
    public const string MeterName = "Nite";

    private static readonly string? Version = typeof(NiteTelemetry).Assembly.GetName().Version?.ToString(3);

    internal static readonly ActivitySource Source = new(SourceName, Version);
    internal static readonly Meter Meter = new(MeterName, Version);

    /// <summary>Dev server compiles of a module or stylesheet; tagged nite.kind (module, stylesheet).</summary>
    internal static readonly Histogram<double> CompileDuration = Meter.CreateHistogram<double>(
        "nite.dev.compile.duration", "s", "How long the dev server took to compile a module or stylesheet.");

    /// <summary>Dev server compile requests; tagged nite.kind and nite.cache (hit, miss).</summary>
    internal static readonly Counter<long> Compiles = Meter.CreateCounter<long>(
        "nite.dev.compiles", "{compile}", "Modules and stylesheets the dev server was asked for, by whether its cache had them.");

    /// <summary>Stylesheet tool runs; tagged nite.css.tool (sass, less, tailwind, postcss).</summary>
    internal static readonly Histogram<double> StylesheetToolDuration = Meter.CreateHistogram<double>(
        "nite.css.tool.duration", "s", "How long a stylesheet tool (Sass, Less, Tailwind, PostCSS) took on one file.");

    /// <summary>What file changes turned into in the browser; tagged nite.update.kind (update, css, reload, error).</summary>
    internal static readonly Counter<long> HotUpdates = Meter.CreateCounter<long>(
        "nite.dev.hot_updates", "{update}", "Hot updates sent to browsers, by kind.");

    internal static readonly Histogram<double> PrebundleDuration = Meter.CreateHistogram<double>(
        "nite.dev.prebundle.duration", "s", "How long pre-bundling the project's dependencies took.");

    internal static readonly Histogram<double> InstallDuration = Meter.CreateHistogram<double>(
        "nite.packages.install.duration", "s", "How long making node_modules match nite.json took.");

    /// <summary>Package tarballs needed; tagged nite.cache (hit, miss).</summary>
    internal static readonly Counter<long> Tarballs = Meter.CreateCounter<long>(
        "nite.packages.tarballs", "{tarball}", "Package tarballs fetched (project packages and the esbuild and Sass binaries), by whether the per-user cache had them.");

    internal static readonly Histogram<double> BuildDuration = Meter.CreateHistogram<double>(
        "nite.build.duration", "s", "How long a production build took.");

    /// <summary>Runs <paramref name="work"/> in a span named <paramref name="name"/>, failing the span if it throws.</summary>
    /// <param name="root">Start a new trace rather than joining whatever span is current.</param>
    internal static async Task TraceAsync(string name, Func<Task> work, bool root = false)
    {
        if (root)
        {
            // Only this async flow sees the change.
            Activity.Current = null;
        }
        using var activity = Source.StartActivity(name);
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            activity.Fail(ex);
            throw;
        }
    }

    /// <summary>Marks a span as failed, OpenTelemetry style: error status plus an exception event.</summary>
    internal static void Fail(this Activity? activity, Exception exception)
    {
        if (activity is null)
        {
            return;
        }
        activity.SetStatus(ActivityStatusCode.Error, exception.Message);
        activity.AddException(exception);
    }

    /// <summary>Seconds since <paramref name="started"/>, a <see cref="Stopwatch.GetTimestamp"/> value.</summary>
    internal static double SecondsSince(long started) => Stopwatch.GetElapsedTime(started).TotalSeconds;
}
