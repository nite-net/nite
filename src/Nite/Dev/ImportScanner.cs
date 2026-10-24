using System.Text.RegularExpressions;

namespace Nite.Dev;

/// <summary>
/// Finds and rewrites import specifiers in esbuild's output. esbuild prints imports in a fixed
/// shape (<c>import x from "y"</c>, <c>export * from "y"</c>, <c>import("y")</c>), which is what
/// makes a regex good enough here; it never runs on hand-written code.
/// </summary>
internal static partial class ImportScanner
{
    public static bool IsBare(string specifier) =>
        !(specifier.StartsWith('.') || specifier.StartsWith('/') || specifier.Contains("://") || specifier.StartsWith("data:", StringComparison.Ordinal));

    /// <summary>The npm package a bare specifier belongs to: <c>react-dom/client</c> → <c>react-dom</c>.</summary>
    public static string PackageName(string specifier)
    {
        var parts = specifier.Split('/');
        return specifier.StartsWith('@') && parts.Length > 1 ? $"{parts[0]}/{parts[1]}" : parts[0];
    }

    public static IEnumerable<string> FindSpecifiers(string code) =>
        ImportPattern().Matches(code).Select(m => m.Groups["spec"].Value);

    /// <summary>Replaces every specifier for which <paramref name="rewrite"/> returns a new value.</summary>
    public static string Rewrite(string code, Func<string, string?> rewrite) =>
        ImportPattern().Replace(code, match =>
        {
            var specifier = match.Groups["spec"].Value;
            var replacement = rewrite(specifier);
            if (replacement is null || replacement == specifier)
            {
                return match.Value;
            }
            var group = match.Groups["spec"];
            return string.Concat(match.Value.AsSpan(0, group.Index - match.Index), replacement, match.Value.AsSpan(group.Index - match.Index + group.Length));
        });

    [GeneratedRegex(@"(?:\bimport\s*\(\s*|\bfrom\s*|\bimport\s+)(?<q>[""'])(?<spec>[^""'\r\n]+)\k<q>")]
    private static partial Regex ImportPattern();
}
