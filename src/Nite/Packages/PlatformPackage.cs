using System.Collections.Concurrent;
using Nite.Configuration;
using Nite.Observability;

namespace Nite.Packages;

/// <summary>
/// Downloads and caches an npm package that carries a native binary (<c>@esbuild/linux-x64</c>,
/// <c>sass-embedded-darwin-arm64</c>), outside any project, verified against the registry's
/// integrity hash. Extraction goes to a staging folder that is swapped in, so a crash never
/// leaves half a binary behind.
/// </summary>
internal static class PlatformPackage
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    /// <summary>Returns the folder the package was extracted to (it holds <c>package/</c>'s contents).</summary>
    public static async Task<string> EnsureAsync(HttpClient http, RegistrySettings? registry, string package, string version,
        string directory, string expectedFile, CancellationToken cancellationToken)
    {
        if (File.Exists(Path.Combine(directory, expectedFile)))
        {
            return directory;
        }

        var gate = Locks.GetOrAdd(directory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(Path.Combine(directory, expectedFile)))
            {
                return directory;
            }

            using var activity = NiteTelemetry.Source.StartActivity("nite.tool.download");
            activity?.SetTag("nite.package", $"{package}@{version}");
            var npm = new NpmRegistry(http, registry);
            var packument = await npm.GetPackumentAsync(package, cancellationToken);
            var manifest = packument.Versions.GetValueOrDefault(version)
                ?? throw new NitePackageException($"{package}@{version} is not published.");
            var tarball = await npm.GetTarballAsync(manifest.Dist, cancellationToken);

            var staging = directory + "." + Guid.NewGuid().ToString("N");
            Tarball.Extract(tarball, staging);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
            Directory.Move(staging, directory);
            return directory;
        }
        finally
        {
            gate.Release();
        }
    }
}
