using Microsoft.AspNetCore.Http;

namespace Nite.Assets;

/// <summary>
/// What the tag helpers need to know to write script tags: the dev server answers in development,
/// the build manifest answers in production.
/// </summary>
public interface INiteAssets
{
    bool IsDevelopment { get; }

    ValueTask<NiteHead> GetHeadAsync(HttpContext context);

    /// <summary>Resolves an island by name (its path under the islands folder, without extension).</summary>
    ValueTask<NiteAsset> GetIslandAsync(string name, HttpContext context);

    /// <summary>Resolves an entry module by its path relative to the client root.</summary>
    ValueTask<NiteAsset> GetModuleAsync(string source, HttpContext context);
}

/// <summary>Tags for the document head. The import map (dev only) must come before any module script.</summary>
public sealed record NiteHead(
    IReadOnlyDictionary<string, string>? ImportMap,
    IReadOnlyList<string> Preloads,
    IReadOnlyList<string> Scripts);

public sealed record NiteAsset(string Url, IReadOnlyList<string> Stylesheets, IReadOnlyList<string> Preloads);
