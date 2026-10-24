namespace Nite.Esbuild;

/// <summary>
/// An esbuild plugin implemented in .NET. esbuild matches the filters itself (they are Go regular
/// expressions, so keep to the syntax both languages share) and calls back over the service
/// channel only for paths that match, exactly as it does for plugins written in JavaScript.
/// </summary>
public sealed class EsbuildPlugin
{
    public required string Name { get; init; }
    public List<EsbuildOnResolve> OnResolve { get; } = [];
    public List<EsbuildOnLoad> OnLoad { get; } = [];
}

/// <param name="Filter">A regular expression matched against the import path.</param>
/// <param name="Namespace">Only paths in this namespace, or any namespace when empty.</param>
public sealed record EsbuildOnResolve(string Filter, string Namespace, Func<EsbuildResolveArgs, Task<EsbuildResolveResult?>> Callback);

/// <param name="Filter">A regular expression matched against the resolved path.</param>
/// <param name="Namespace">Only paths in this namespace, or any namespace when empty.</param>
public sealed record EsbuildOnLoad(string Filter, string Namespace, Func<EsbuildLoadArgs, Task<EsbuildLoadResult?>> Callback);

/// <param name="Kind">How the path was imported: entry-point, import-statement, import-rule, url-token…</param>
public sealed record EsbuildResolveArgs(string Path, string Importer, string Namespace, string ResolveDir, string Kind);

public sealed record EsbuildResolveResult
{
    public string? Path { get; init; }
    public string? Namespace { get; init; }
    public bool External { get; init; }
    public string? Suffix { get; init; }
}

public sealed record EsbuildLoadArgs(string Path, string Namespace, string Suffix);

public sealed record EsbuildLoadResult
{
    public required string Contents { get; init; }
    public string? Loader { get; init; }
    public string? ResolveDir { get; init; }

    /// <summary>Files besides the loaded one that went into the result (Sass partials, Tailwind sources).</summary>
    public IReadOnlyList<string> WatchFiles { get; init; } = [];
}

/// <summary>Thrown by a plugin callback to report an error at a source location.</summary>
public class EsbuildPluginException(string message, string? file = null, int line = 0, int column = 0, string? lineText = null)
    : Exception(message)
{
    public string? File { get; } = file;
    public int Line { get; } = line;
    public int Column { get; } = column;
    public string? LineText { get; } = lineText;
}
