using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;
using Nite.Assets;

namespace Nite.TagHelpers;

/// <summary>
/// <c>&lt;nite-head /&gt;</c>: the import map, dev client and island loader in development; the
/// hashed loader and its preloads in production. Put it in <c>&lt;head&gt;</c>, before other module scripts.
/// </summary>
[HtmlTargetElement("nite-head", TagStructure = TagStructure.WithoutEndTag)]
public sealed class NiteHeadTagHelper(INiteAssets assets) : TagHelper
{
    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        output.TagName = null;
        var head = await assets.GetHeadAsync(ViewContext.HttpContext);
        var html = output.Content;

        if (head.ImportMap is { Count: > 0 } imports)
        {
            // System.Text.Json escapes '<' and friends, so this cannot close the script element early.
            html.AppendHtml("<script type=\"importmap\">")
                .AppendHtml(JsonSerializer.Serialize(new { imports }))
                .AppendHtml("</script>\n");
        }
        foreach (var preload in head.Preloads)
        {
            html.AppendHtml($"<link rel=\"modulepreload\" href=\"{HtmlEncoder.Default.Encode(preload)}\">\n");
        }
        foreach (var script in head.Scripts)
        {
            html.AppendHtml($"<script type=\"module\" src=\"{HtmlEncoder.Default.Encode(script)}\"></script>\n");
        }
    }
}

/// <summary>
/// <c>&lt;island name="Counter" props="Model.Counter" client="visible"&gt;fallback&lt;/island&gt;</c>:
/// a placeholder the island loader mounts a client component into. Child content is shown until
/// the island loads, which is a good place for a server-rendered approximation.
/// </summary>
[HtmlTargetElement("island", Attributes = "name")]
public sealed class IslandTagHelper(INiteAssets assets) : TagHelper
{
    internal static readonly JsonSerializerOptions PropsJson = new(JsonSerializerDefaults.Web);

    /// <summary>The island module's path under the islands folder, without extension.</summary>
    public string Name { get; set; } = "";

    /// <summary>Props for the component, serialized as camelCase JSON.</summary>
    public object? Props { get; set; }

    /// <summary>When to load: <c>load</c> (default), <c>idle</c>, <c>visible</c> or <c>media</c>.</summary>
    public string? Client { get; set; }

    /// <summary>The media query for <c>client="media"</c>.</summary>
    public string? Media { get; set; }

    /// <summary>The export to render, when it is not the default export.</summary>
    public string? Export { get; set; }

    /// <summary>The wrapper element. Defaults to <c>div</c>.</summary>
    public string Tag { get; set; } = "div";

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var http = ViewContext.HttpContext;
        var island = await assets.GetIslandAsync(Name, http);

        output.TagName = Tag;
        output.TagMode = TagMode.StartTagAndEndTag;
        output.Attributes.SetAttribute("data-island", island.Url);
        output.Attributes.SetAttribute("data-name", Name);
        if (Props is not null)
        {
            output.Attributes.SetAttribute("data-props", JsonSerializer.Serialize(Props, PropsJson));
        }
        if (!string.IsNullOrEmpty(Client))
        {
            output.Attributes.SetAttribute("data-client", Client);
        }
        if (!string.IsNullOrEmpty(Media))
        {
            output.Attributes.SetAttribute("data-media", Media);
        }
        if (!string.IsNullOrEmpty(Export))
        {
            output.Attributes.SetAttribute("data-export", Export);
        }

        // Production CSS is emitted as a link before the island (once per page), so the island
        // does not flash unstyled. In development each CSS import injects its own <style>.
        AppendStylesheets(http, output.PreElement, island.Stylesheets);
    }

    internal static void AppendStylesheets(HttpContext http, TagHelperContent content, IReadOnlyList<string> stylesheets)
    {
        foreach (var stylesheet in stylesheets)
        {
            if (http.Items.TryAdd("nite:css:" + stylesheet, true))
            {
                content.AppendHtml($"<link rel=\"stylesheet\" href=\"{HtmlEncoder.Default.Encode(stylesheet)}\">\n");
            }
        }
    }
}

/// <summary><c>&lt;nite-module src="main.ts" /&gt;</c>: loads an entry module from the client root.</summary>
[HtmlTargetElement("nite-module", Attributes = "src", TagStructure = TagStructure.WithoutEndTag)]
public sealed class NiteModuleTagHelper(INiteAssets assets) : TagHelper
{
    public string Src { get; set; } = "";

    [ViewContext]
    [HtmlAttributeNotBound]
    public ViewContext ViewContext { get; set; } = null!;

    public override async Task ProcessAsync(TagHelperContext context, TagHelperOutput output)
    {
        var http = ViewContext.HttpContext;
        var module = await assets.GetModuleAsync(Src, http);

        output.TagName = null;
        IslandTagHelper.AppendStylesheets(http, output.Content, module.Stylesheets);
        foreach (var preload in module.Preloads)
        {
            output.Content.AppendHtml($"<link rel=\"modulepreload\" href=\"{HtmlEncoder.Default.Encode(preload)}\">\n");
        }
        output.Content.AppendHtml($"<script type=\"module\" src=\"{HtmlEncoder.Default.Encode(module.Url)}\"></script>");
    }
}
