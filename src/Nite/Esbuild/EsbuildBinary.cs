using System.Runtime.InteropServices;
using Nite.Configuration;
using Nite.Packages;

namespace Nite.Esbuild;

/// <summary>
/// Finds or downloads the platform's esbuild binary. It comes from the same npm package
/// (<c>@esbuild/linux-x64</c> and friends) that esbuild's own installer uses, verified against the
/// registry's integrity hash and cached per user.
/// </summary>
public static class EsbuildBinary
{
    /// <summary>The esbuild version this build of Nite speaks the service protocol of.</summary>
    public const string Version = "0.28.2";

    private static readonly SemaphoreSlim DownloadLock = new(1, 1);

    public static async Task<string> EnsureAsync(HttpClient http, RegistrySettings? registry = null, CancellationToken cancellationToken = default)
    {
        // An explicit path is useful offline or when an organization vendors binaries.
        var overridden = Environment.GetEnvironmentVariable("NITE_ESBUILD_PATH");
        if (!string.IsNullOrEmpty(overridden))
        {
            return File.Exists(overridden) ? overridden : throw new FileNotFoundException("NITE_ESBUILD_PATH points to a missing file.", overridden);
        }

        var platform = PlatformPackage();
        var directory = Path.Combine(NiteDirectories.Current.Cache, "esbuild", $"{Version}-{platform}");
        var binary = Path.Combine(directory, OperatingSystem.IsWindows() ? "esbuild.exe" : Path.Combine("bin", "esbuild"));
        if (File.Exists(binary))
        {
            return binary;
        }

        await DownloadLock.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(binary))
            {
                return binary;
            }

            var packument = await new NpmRegistry(http, registry).GetPackumentAsync($"@esbuild/{platform}", cancellationToken);
            var manifest = packument.Versions.GetValueOrDefault(Version)
                ?? throw new NitePackageException($"@esbuild/{platform}@{Version} is not published.");
            var tarball = await new NpmRegistry(http, registry).GetTarballAsync(manifest.Dist, cancellationToken);

            // Extract next to the final folder and swap it in, so a crash never leaves half a binary.
            var staging = directory + "." + Guid.NewGuid().ToString("N");
            Tarball.Extract(tarball, staging);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
            Directory.Move(staging, directory);
            return binary;
        }
        finally
        {
            DownloadLock.Release();
        }
    }

    private static string PlatformPackage()
    {
        var os = OperatingSystem.IsWindows() ? "win32"
            : OperatingSystem.IsMacOS() ? "darwin"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsFreeBSD() ? "freebsd"
            : throw new PlatformNotSupportedException("Nite has no esbuild binary for this operating system. Set NITE_ESBUILD_PATH.");

        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "ia32",
            Architecture.Arm => "arm",
            _ => throw new PlatformNotSupportedException($"Nite has no esbuild binary for {RuntimeInformation.OSArchitecture}. Set NITE_ESBUILD_PATH."),
        };
        return $"{os}-{arch}";
    }
}
