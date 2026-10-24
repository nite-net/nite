using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace Nite.Assets;

/// <summary>
/// What the tag helpers and the SPA endpoint need to know to write script tags: the dev server
/// answers in development, the build manifest answers in production.
/// </summary>
public interface INiteAssets
{
    bool IsDevelopment { get; }

    ValueTask<NiteHead> GetHeadAsync(HttpContext context);

    /// <summary>Resolves an island by name (its path under the islands folder, without extension).</summary>
    ValueTask<NiteAsset> GetIslandAsync(string name, HttpContext context);

    /// <summary>Resolves an entry module by its path relative to the client root.</summary>
    ValueTask<NiteAsset> GetModuleAsync(string source, HttpContext context);

    /// <summary>Renders an HTML entry (such as <c>index.html</c>) with its scripts and styles wired up.</summary>
    ValueTask<string> GetHtmlAsync(string page, HttpContext context);
}

/// <summary>Tags for the document head. The import map (dev only) must come before any module script.</summary>
public sealed record NiteHead(
    IReadOnlyDictionary<string, string>? ImportMap,
    IReadOnlyList<string> Preloads,
    IReadOnlyList<string> Scripts)
{
    public string ToHtml()
    {
        var html = new StringBuilder();
        if (ImportMap is { Count: > 0 } imports)
        {
            // System.Text.Json escapes '<' and friends, so this cannot close the script element early.
            html.Append("<script type=\"importmap\">").Append(JsonSerializer.Serialize(new { imports })).Append("</script>\n");
        }
        foreach (var preload in Preloads)
        {
            html.Append("<link rel=\"modulepreload\" href=\"").Append(HtmlEncoder.Default.Encode(preload)).Append("\">\n");
        }
        foreach (var script in Scripts)
        {
            html.Append("<script type=\"module\" src=\"").Append(HtmlEncoder.Default.Encode(script)).Append("\"></script>\n");
        }
        return html.ToString();
    }
}

public sealed record NiteAsset(string Url, IReadOnlyList<string> Stylesheets, IReadOnlyList<string> Preloads);
