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

    public static async Task<string> EnsureAsync(HttpClient http, RegistrySettings? registry = null, CancellationToken cancellationToken = default)
    {
        // An explicit path is useful offline or when an organization vendors binaries.
        var overridden = Environment.GetEnvironmentVariable("NITE_ESBUILD_PATH");
        if (!string.IsNullOrEmpty(overridden))
        {
            return File.Exists(overridden) ? overridden : throw new FileNotFoundException("NITE_ESBUILD_PATH points to a missing file.", overridden);
        }

        var platform = PlatformName();
        var directory = Path.Combine(NiteDirectories.Current.Cache, "esbuild", $"{Version}-{platform}");
        var binary = Path.Combine(directory, OperatingSystem.IsWindows() ? "esbuild.exe" : Path.Combine("bin", "esbuild"));
        await PlatformPackage.EnsureAsync(http, registry, $"@esbuild/{platform}", Version, directory,
            Path.GetRelativePath(directory, binary), cancellationToken);
        return binary;
    }

    private static string PlatformName()
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
