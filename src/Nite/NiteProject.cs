namespace Nite;

/// <summary>A project directory with its <c>nite.json</c> and the absolute paths derived from it.</summary>
public sealed class NiteProject
{
    /// <summary>Source file extensions Nite compiles, in resolution order for extensionless imports.</summary>
    internal static readonly string[] ModuleExtensions = [".tsx", ".ts", ".jsx", ".js", ".mts", ".mjs"];

    /// <summary>URL prefix for everything Nite serves, in development and production.</summary>
    public const string BasePath = "/_nite";

    public NiteProject(string projectDirectory, NiteConfig? config = null)
    {
        Directory = Path.GetFullPath(projectDirectory);
        Config = config ?? NiteConfig.Load(Directory);
        ClientRoot = Path.GetFullPath(Path.Combine(Directory, Config.ClientRoot));
        IslandsDirectory = Path.GetFullPath(Path.Combine(ClientRoot, Config.Islands));
        OutputDirectory = Path.Combine(Directory, "wwwroot", BasePath.TrimStart('/'));
        WorkDirectory = Path.Combine(Directory, ".nite");
    }

    public string Directory { get; }
    public NiteConfig Config { get; }
    public string ClientRoot { get; }
    public string IslandsDirectory { get; }

    /// <summary>Where production builds go: <c>wwwroot/_nite</c>, so they publish as static web assets.</summary>
    public string OutputDirectory { get; }

    /// <summary>Scratch space for pre-bundled dependencies and generated runtime files.</summary>
    public string WorkDirectory { get; }

    public bool UsesReactRenderer => string.Equals(Config.Renderer, "react", StringComparison.OrdinalIgnoreCase);

    /// <summary>Island names (paths under the islands folder, without extension) mapped to their files.</summary>
    public IReadOnlyDictionary<string, string> FindIslands()
    {
        var islands = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (!System.IO.Directory.Exists(IslandsDirectory))
        {
            return islands;
        }

        foreach (var file in System.IO.Directory.EnumerateFiles(IslandsDirectory, "*", SearchOption.AllDirectories))
        {
            if (!ModuleExtensions.Contains(Path.GetExtension(file)) || file.EndsWith(".d.ts", StringComparison.Ordinal))
            {
                continue;
            }
            var relative = Path.GetRelativePath(IslandsDirectory, file);
            var name = NitePaths.ToUrlPath(Path.ChangeExtension(relative, null));
            islands.TryAdd(name, file);
        }
        return islands;
    }

    /// <summary>The client-root-relative URL path for a file, such as <c>islands/Counter.tsx</c>.</summary>
    public string RelativeToClientRoot(string fullPath) =>
        NitePaths.ToUrlPath(Path.GetRelativePath(ClientRoot, fullPath));

    /// <summary>Resolves a client-relative path to a file inside the client root, or null.</summary>
    public string? ResolveClientPath(string relativePath)
    {
        var full = Path.GetFullPath(Path.Combine(ClientRoot, relativePath.TrimStart('/')));
        return IsInsideClientRoot(full) ? full : null;
    }

    public bool IsInsideClientRoot(string fullPath) =>
        fullPath.StartsWith(ClientRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>The configured custom renderer module, when <see cref="NiteConfig.Renderer"/> is not "react".</summary>
    public string? CustomRendererPath =>
        UsesReactRenderer ? null : ResolveClientPath(Config.Renderer)
            ?? throw new InvalidOperationException($"Renderer '{Config.Renderer}' must be a module inside {ClientRoot}.");
}
