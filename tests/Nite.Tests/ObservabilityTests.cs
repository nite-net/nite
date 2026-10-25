using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Nite.Assets;
using Nite.Dev;
using Nite.Observability;
using Nite.Packages;
using static Nite.Tests.PackageInstallerTests;

namespace Nite.Tests;

public class NiteTelemetryTests : IDisposable
{
    private static readonly ActivitySource Test = new("Nite.Tests");
    private readonly DirectoryInfo _project = Directory.CreateTempSubdirectory("nite-telemetry-");

    public void Dispose() => _project.Delete(recursive: true);

    [Fact]
    public async Task Traces_an_install_with_resolve_and_materialize_spans()
    {
        var registry = new FakeRegistry()
            .Add("app", "1.0.0", dependencies: new() { ["dep"] = "^2.0.0" })
            .Add("dep", "2.1.0");
        var config = new NiteConfig { Dependencies = new() { ["app"] = "^1.0.0" } };

        var spans = await RecordAsync(() => new PackageInstaller(new HttpClient(registry)).InstallAsync(_project.FullName, config));

        var install = Assert.Single(spans, s => s.OperationName == "nite.packages.install");
        Assert.Equal(2, install.GetTagItem("nite.packages.count"));
        Assert.Equal(ActivityStatusCode.Unset, install.Status);

        var resolve = Assert.Single(spans, s => s.OperationName == "nite.packages.resolve");
        Assert.Equal(install.SpanId, resolve.ParentSpanId);
        Assert.Equal(1, resolve.GetTagItem("nite.packages.requested"));

        var materialize = Assert.Single(spans, s => s.OperationName == "nite.packages.materialize");
        Assert.Equal(install.SpanId, materialize.ParentSpanId);
        Assert.Equal(2, materialize.GetTagItem("nite.packages.installed"));
    }

    [Fact]
    public async Task Marks_a_failed_install_as_an_error_with_the_exception()
    {
        var config = new NiteConfig { Dependencies = new() { ["does-not-exist"] = "^1.0.0" } };

        var spans = await RecordAsync(() => Assert.ThrowsAsync<NitePackageException>(
            () => new PackageInstaller(new HttpClient(new FakeRegistry())).InstallAsync(_project.FullName, config)));

        var install = Assert.Single(spans, s => s.OperationName == "nite.packages.install");
        Assert.Equal(ActivityStatusCode.Error, install.Status);
        Assert.Contains("does-not-exist", install.StatusDescription);
        Assert.Contains(install.Events, e => e.Name == "exception");
    }

    [Fact]
    public async Task Measures_install_time_and_tarballs()
    {
        var measurements = new ConcurrentBag<(string Instrument, object? Cache)>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, l) =>
            {
                if (instrument.Meter.Name == NiteTelemetry.MeterName)
                {
                    l.EnableMeasurementEvents(instrument);
                }
            },
        };
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) => measurements.Add((instrument.Name, null)));
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            object? cache = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "nite.cache") cache = tag.Value;
            }
            measurements.Add((instrument.Name, cache));
        });
        listener.Start();

        var registry = new FakeRegistry().Add("solo", "1.0.0");
        await new PackageInstaller(new HttpClient(registry)).InstallAsync(_project.FullName, new NiteConfig { Dependencies = new() { ["solo"] = "1.0.0" } });

        // Other tests may install at the same time, so only check that ours were counted.
        Assert.Contains(measurements, m => m.Instrument == "nite.packages.install.duration");
        Assert.Contains(measurements, m => m.Instrument == "nite.packages.tarballs" && m.Cache is "hit" or "miss");
    }

    /// <summary>Runs <paramref name="work"/> under a span of its own and returns the Nite spans in that trace.</summary>
    private static async Task<List<Activity>> RecordAsync(Func<Task> work)
    {
        var stopped = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is NiteTelemetry.SourceName or "Nite.Tests",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Add,
        };
        ActivitySource.AddActivityListener(listener);

        ActivityTraceId trace;
        using (var root = Test.StartActivity("test")!)
        {
            trace = root.TraceId;
            await work();
        }
        return stopped.Where(a => a.TraceId == trace && a.Source.Name == NiteTelemetry.SourceName).ToList();
    }
}

public class NiteHealthCheckTests : IDisposable
{
    private readonly DirectoryInfo _project = Directory.CreateTempSubdirectory("nite-health-");

    public void Dispose() => _project.Delete(recursive: true);

    [Fact]
    public async Task Production_is_unhealthy_without_the_build_output()
    {
        var check = new NiteHealthCheck(new ProductionAssets(new FakeEnvironment()));

        var result = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("nite build", result.Description);
    }

    [Fact]
    public async Task Development_is_unhealthy_until_UseNite_starts_the_dev_server()
    {
        File.WriteAllText(Path.Combine(_project.FullName, "nite.json"), "{}");
        await using var server = new NiteDevServer(new NiteProject(_project.FullName), new HttpClient(), NullLoggerFactory.Instance);

        var result = await new NiteHealthCheck(server).CheckHealthAsync(new HealthCheckContext());

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.Contains("UseNite", result.Description);
    }

    private sealed class FakeEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "Test";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Production";
    }
}
