using System.Collections.Concurrent;
using Nite.Configuration;
using Nite.Observability;

namespace Nite.Packages;

/// <summary>
/// Downloads and caches an npm package that carries a native binary (<c>@esbuild/linux-x64</c>,
/// <c>sass-embedded-darwin-arm64</c>), outside any project, verified against the registry's
/// integrity hash. Extraction goes to a staging folder that is swapped in, so a crash never
/// leaves half a binary behind, and a lock file keeps processes that share the cache from
/// extracting over each other.
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
            // Other processes (two nite commands, or test hosts for two frameworks) share the cache.
            await using var crossProcess = await AcquireFileLockAsync(directory + ".lock", cancellationToken);
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
            SwapIn(staging, directory, expectedFile);
            return directory;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Moves a finished extraction into place. A folder that already holds the binary is never
    /// deleted, since another process may be running it: the staging copy is discarded instead.
    /// Only a folder without it (left by an older crash) is replaced.
    /// </summary>
    internal static void SwapIn(string staging, string directory, string expectedFile)
    {
        var expected = Path.Combine(directory, expectedFile);
        try
        {
            if (Directory.Exists(directory) && !File.Exists(expected))
            {
                Directory.Delete(directory, recursive: true);
            }
            if (!Directory.Exists(directory))
            {
                Directory.Move(staging, directory);
                return;
            }
        }
        catch (IOException) when (File.Exists(expected))
        {
            // Another process moved its copy in first.
        }
        TryDelete(staging);
    }

    /// <summary>
    /// An exclusive lock on a file, held until disposed. FileShare.None is a real lock on every
    /// platform (.NET uses flock on Unix), so it also excludes other processes.
    /// </summary>
    internal static async Task<FileStream> AcquireFileLockAsync(string path, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        while (true)
        {
            try
            {
                return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
