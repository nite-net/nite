namespace Nite.Configuration;

/// <summary>
/// Where Nite keeps per-user files, following the XDG Base Directory specification:
/// <list type="bullet">
/// <item><b>Config</b>: settings people edit (<c>config.json</c>).</item>
/// <item><b>Cache</b>: anything re-downloadable (tarballs, esbuild, registry metadata); safe to delete.</item>
/// <item><b>Data</b> and <b>State</b>: reserved for things that are neither.</item>
/// </list>
/// Each can be overridden with <c>NITE_CONFIG_DIR</c>, <c>NITE_CACHE_DIR</c>, <c>NITE_DATA_DIR</c> or
/// <c>NITE_STATE_DIR</c>; otherwise <c>XDG_*_HOME</c> applies on every OS when set (to an absolute
/// path, as the spec requires). Defaults are the XDG ones on Linux and macOS (developers expect
/// <c>~/.config</c> from a CLI), and the native AppData folders on Windows.
/// </summary>
public sealed record NiteDirectories(string Config, string Cache, string Data, string State)
{
    private const string AppName = "nite";

    public static NiteDirectories Current { get; } = Resolve(
        Environment.GetEnvironmentVariable,
        OperatingSystem.IsWindows(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    /// <summary>The user configuration file.</summary>
    public string ConfigFile => System.IO.Path.Combine(Config, "config.json");

    /// <summary>
    /// Where Nite 0.1 and 0.2 kept their cache (.NET's LocalApplicationData), so <c>nite cache clean</c>
    /// can remove it. On Linux that is <c>~/.local/share/nite</c>, which is now the data directory, so
    /// only the old cache's own subfolders count.
    /// </summary>
    public static IEnumerable<string> LegacyCacheFolders()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrEmpty(root))
        {
            yield break;
        }
        yield return System.IO.Path.Combine(root, AppName, "tarballs");
        yield return System.IO.Path.Combine(root, AppName, "esbuild");
    }

    internal static NiteDirectories Resolve(Func<string, string?> environment, bool isWindows, string? home, string? appData, string? localAppData)
    {
        if (string.IsNullOrEmpty(home))
        {
            // Containers and service accounts sometimes have no home directory at all.
            home = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nite-home");
        }

        string Pick(string niteVariable, string xdgVariable, Func<string> fallback)
        {
            if (environment(niteVariable) is { Length: > 0 } overridden)
            {
                return System.IO.Path.GetFullPath(overridden);
            }
            if (environment(xdgVariable) is { Length: > 0 } xdg && System.IO.Path.IsPathFullyQualified(xdg))
            {
                return System.IO.Path.Combine(xdg, AppName);
            }
            return fallback();
        }

        var windowsLocal = string.IsNullOrEmpty(localAppData) ? System.IO.Path.Combine(home, "AppData", "Local") : localAppData;
        var windowsRoaming = string.IsNullOrEmpty(appData) ? System.IO.Path.Combine(home, "AppData", "Roaming") : appData;

        return new NiteDirectories(
            Config: Pick("NITE_CONFIG_DIR", "XDG_CONFIG_HOME", () => isWindows
                ? System.IO.Path.Combine(windowsRoaming, AppName)
                : System.IO.Path.Combine(home, ".config", AppName)),
            Cache: Pick("NITE_CACHE_DIR", "XDG_CACHE_HOME", () => isWindows
                ? System.IO.Path.Combine(windowsLocal, AppName, "cache")
                : System.IO.Path.Combine(home, ".cache", AppName)),
            Data: Pick("NITE_DATA_DIR", "XDG_DATA_HOME", () => isWindows
                ? System.IO.Path.Combine(windowsLocal, AppName, "data")
                : System.IO.Path.Combine(home, ".local", "share", AppName)),
            State: Pick("NITE_STATE_DIR", "XDG_STATE_HOME", () => isWindows
                ? System.IO.Path.Combine(windowsLocal, AppName, "state")
                : System.IO.Path.Combine(home, ".local", "state", AppName)));
    }
}
