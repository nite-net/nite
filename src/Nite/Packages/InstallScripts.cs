namespace Nite.Packages;

/// <summary>
/// What to say about a package whose manifest has <c>hasInstallScript: true</c>. Nite never runs
/// install scripts, so a package that needs one to download or compile something may not work.
/// A few well-known packages get a more specific message.
/// </summary>
internal static class InstallScripts
{
    public enum Kind
    {
        /// <summary>Unknown script: it may or may not matter.</summary>
        Unknown,

        /// <summary>The script only prints a message (a funding banner and the like).</summary>
        MessageOnly,

        /// <summary>The script fetches a binary that Nite downloads itself.</summary>
        ProvidedByNite,

        /// <summary>A native Node.js addon, which cannot run in a browser at all.</summary>
        NativeAddon,
    }

    private const string Native = "it is a native Node.js addon, which cannot run in a browser. Server-side work like this belongs in your ASP.NET Core code";

    private static readonly Dictionary<string, (Kind Kind, string Detail)> Known = new(StringComparer.Ordinal)
    {
        ["core-js"] = (Kind.MessageOnly, "it only prints a message, so nothing is missing"),
        ["core-js-pure"] = (Kind.MessageOnly, "it only prints a message, so nothing is missing"),
        ["es5-ext"] = (Kind.MessageOnly, "it only prints a message, so nothing is missing"),
        ["esbuild"] = (Kind.ProvidedByNite, "it fetches the esbuild binary, which Nite downloads and runs itself"),
        ["node-sass"] = (Kind.NativeAddon, "it builds LibSass, a native addon. Nite compiles Sass with Dart Sass's native binary, which it downloads itself: replace node-sass with sass"),
        ["bcrypt"] = (Kind.NativeAddon, Native),
        ["better-sqlite3"] = (Kind.NativeAddon, Native),
        ["sqlite3"] = (Kind.NativeAddon, Native),
        ["argon2"] = (Kind.NativeAddon, Native),
        ["canvas"] = (Kind.NativeAddon, Native),
    };

    public static Kind Classify(string name) => Known.TryGetValue(name, out var known) ? known.Kind : Kind.Unknown;

    /// <summary>Scripts that are known to be harmless to skip are information, not warnings.</summary>
    public static bool IsHarmless(string name) => Classify(name) is Kind.MessageOnly or Kind.ProvidedByNite;

    /// <summary>The message for <paramref name="name"/>@<paramref name="version"/>.</summary>
    public static string Describe(string name, string version)
    {
        var package = $"{name}@{version}";
        if (!Known.TryGetValue(name, out var known))
        {
            return $"{package} has an install script, which Nite does not run. If the package needs it to download or build something, it may not work.";
        }
        return $"{package} has an install script, which Nite does not run; {known.Detail}.";
    }
}
