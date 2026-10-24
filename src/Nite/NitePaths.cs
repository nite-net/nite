namespace Nite;

internal static class NitePaths
{
    /// <summary>
    /// The per-user cache for tarballs and the esbuild binary. <c>NITE_CACHE_DIR</c> overrides it,
    /// which is handy for CI caches and containers with a read-only home directory.
    /// </summary>
    public static string CacheDirectory
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("NITE_CACHE_DIR");
            if (!string.IsNullOrEmpty(overridden))
            {
                return overridden;
            }

            var baseDirectory = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
            if (string.IsNullOrEmpty(baseDirectory))
            {
                baseDirectory = Path.Combine(Path.GetTempPath(), "nite-cache");
            }
            return Path.Combine(baseDirectory, "nite");
        }
    }

    public static string ToUrlPath(string relativePath) => relativePath.Replace('\\', '/');
}
