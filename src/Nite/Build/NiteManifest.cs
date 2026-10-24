using System.Text.Json;

namespace Nite.Build;

/// <summary>
/// <c>wwwroot/_nite/manifest.json</c>: maps source modules to their hashed output files, the CSS
/// they need and the chunks worth preloading. Paths are relative to <c>/_nite/</c>.
/// </summary>
public sealed class NiteManifest
{
    public const string FileName = "manifest.json";

    /// <summary>The manifest key of the island loader.</summary>
    public const string RuntimeKey = "@nite/islands";

    public int Version { get; init; } = 1;

    /// <summary>Island name → entry key.</summary>
    public Dictionary<string, string> Islands { get; init; } = [];

    /// <summary>Entry key (source path relative to the client root) → output.</summary>
    public Dictionary<string, NiteManifestEntry> Entries { get; init; } = [];

    public static NiteManifest Parse(string json) =>
        JsonSerializer.Deserialize<NiteManifest>(json, NiteConfig.JsonOptions)
        ?? throw new InvalidOperationException("The Nite manifest is empty.");

    public string ToJson() => JsonSerializer.Serialize(this, NiteConfig.JsonOptions);
}

public sealed class NiteManifestEntry
{
    public string File { get; init; } = "";
    public List<string> Css { get; init; } = [];

    /// <summary>Chunks the entry imports statically, for <c>modulepreload</c>.</summary>
    public List<string> Imports { get; init; } = [];
}
