using System.Text.RegularExpressions;

namespace Nite.Html;

/// <summary>
/// The parts of an HTML entry (such as <c>Client/index.html</c>) Nite cares about: module scripts
/// and stylesheets with local URLs. Everything else passes through untouched. Static files the page
/// links to (icons, images) belong in <c>wwwroot</c> and are referenced with root-relative URLs.
/// </summary>
internal static partial class HtmlDocument
{
    public static IReadOnlyList<HtmlReference> FindReferences(string html) =>
        Tags().Matches(html)
            .Select(match => ToReference(match.Value))
            .OfType<HtmlReference>()
            .ToList();

    /// <summary>
    /// Replaces script/stylesheet tags with whatever <paramref name="replace"/> returns; null keeps
    /// the tag as written.
    /// </summary>
    public static string Rewrite(string html, Func<HtmlReference, string?> replace) =>
        Tags().Replace(html, match => ToReference(match.Value) is { } reference ? replace(reference) ?? match.Value : match.Value);

    /// <summary>Inserts tags at the start of <c>&lt;head&gt;</c>, so the import map precedes every module script.</summary>
    public static string InjectHead(string html, string tags)
    {
        var head = HeadOpen().Match(html);
        if (head.Success)
        {
            return html.Insert(head.Index + head.Length, "\n" + tags);
        }
        var htmlOpen = HtmlOpen().Match(html);
        return htmlOpen.Success ? html.Insert(htmlOpen.Index + htmlOpen.Length, "\n<head>\n" + tags + "</head>") : tags + html;
    }

    /// <summary>Resolves a reference URL to a path relative to the client root, or null for external URLs.</summary>
    public static string? ResolveLocal(string htmlRelativePath, string url)
    {
        if (url.Contains("://", StringComparison.Ordinal) || url.StartsWith("//", StringComparison.Ordinal) || url.StartsWith("data:", StringComparison.Ordinal))
        {
            return null;
        }

        var path = url.Split('?', '#')[0];
        var segments = new List<string>();
        if (!path.StartsWith('/'))
        {
            segments.AddRange(htmlRelativePath.Replace('\\', '/').Split('/')[..^1]);
        }

        foreach (var segment in path.Split('/'))
        {
            switch (segment)
            {
                case "" or ".":
                    break;
                case "..":
                    if (segments.Count == 0)
                    {
                        return null; // outside the client root
                    }
                    segments.RemoveAt(segments.Count - 1);
                    break;
                default:
                    segments.Add(segment);
                    break;
            }
        }
        return segments.Count == 0 ? null : string.Join('/', segments);
    }

    private static HtmlReference? ToReference(string tag)
    {
        if (tag.StartsWith("<script", StringComparison.OrdinalIgnoreCase))
        {
            if (!ModuleType().IsMatch(tag))
            {
                return null;
            }
            var src = Attribute(tag, "src");
            return src is null ? null : new HtmlReference(HtmlReferenceKind.ModuleScript, src);
        }

        if (!StylesheetRel().IsMatch(tag))
        {
            return null;
        }
        var href = Attribute(tag, "href");
        return href is null ? null : new HtmlReference(HtmlReferenceKind.Stylesheet, href);
    }

    private static string? Attribute(string tag, string name)
    {
        var match = Regex.Match(tag, $@"\s{name}\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value : null;
    }

    [GeneratedRegex(@"<script\b[^>]*>\s*</script\s*>|<link\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Tags();

    [GeneratedRegex(@"\stype\s*=\s*[""']?module[""']?", RegexOptions.IgnoreCase)]
    private static partial Regex ModuleType();

    [GeneratedRegex(@"\srel\s*=\s*[""']?stylesheet[""']?", RegexOptions.IgnoreCase)]
    private static partial Regex StylesheetRel();

    [GeneratedRegex(@"<head\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HeadOpen();

    [GeneratedRegex(@"<html\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlOpen();
}

internal enum HtmlReferenceKind { ModuleScript, Stylesheet }

internal sealed record HtmlReference(HtmlReferenceKind Kind, string Url);
