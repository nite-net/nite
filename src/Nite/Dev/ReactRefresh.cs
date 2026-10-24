using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nite.Dev;

/// <summary>
/// React Fast Refresh support without Babel. esbuild cannot run the react-refresh transform, but
/// its output has a fixed shape (top-level declarations start in column 0, exports are collected
/// in a trailing <c>export { … }</c> block), which is enough to find components, their hook calls
/// and whether every export is a component (a "refresh boundary" that can be swapped in place).
/// </summary>
internal static partial class ReactRefresh
{
    /// <summary>The specifier of the refresh runtime, pre-bundled with the other dependencies.</summary>
    public const string RuntimeSpecifier = "react-refresh/runtime";

    public static RefreshInfo Analyze(string code)
    {
        var components = new List<RefreshComponent>();
        var aliases = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, kind, text, init) in TopLevelDeclarations(code))
        {
            var isFunctionLike = kind is DeclarationKind.Function or DeclarationKind.ComponentLike;
            // esbuild names anonymous default exports "<file>_default"; count them when they render JSX,
            // so an anonymous default utility still propagates to its importers.
            var isAnonymousDefault = name.EndsWith("_default", StringComparison.Ordinal) && JsxCall().IsMatch(text);
            if (isFunctionLike && (IsComponentName(name) || isAnonymousDefault))
            {
                var hooks = HookCall().Matches(text).Select(m => m.Groups["hook"].Value);
                components.Add(new RefreshComponent(name, string.Join(',', hooks)));
            }
            else if (AliasInitializer().Match(init) is { Success: true } alias && components.Any(c => c.Name == alias.Groups[1].Value))
            {
                // `export default App` becomes `var App_default = App;`: the same component under another name.
                aliases.Add(name);
            }
        }

        var exports = ExportedLocals(code, out var hasStarExport);
        var componentNames = components.Select(c => c.Name).Concat(aliases).ToHashSet(StringComparer.Ordinal);
        var isBoundary = components.Count > 0 && exports.Count > 0 && !hasStarExport && exports.All(componentNames.Contains);
        return new RefreshInfo(components, isBoundary);
    }

    /// <summary>
    /// Code appended to a module: registers each component with the refresh runtime under a stable
    /// id, with a signature listing its hooks so React remounts (instead of crashing) when they change.
    /// </summary>
    public static string Footer(RefreshInfo info, string moduleId, string runtimeUrl)
    {
        var builder = new StringBuilder();
        builder.Append("import * as __nite_refresh from ").Append(JsonSerializer.Serialize(runtimeUrl)).Append(";\n");
        foreach (var component in info.Components)
        {
            builder.Append("__nite_refresh.register(").Append(component.Name).Append(", ")
                .Append(JsonSerializer.Serialize($"{moduleId} {component.Name}")).Append(");\n");
            builder.Append("__nite_refresh.sign(").Append(component.Name).Append(", ")
                .Append(JsonSerializer.Serialize(component.HookSignature)).Append(");\n");
        }
        return builder.ToString();
    }

    /// <summary>Inserts <paramref name="footer"/> before the inline source map comment, if any.</summary>
    public static string Append(string code, string footer)
    {
        var index = code.LastIndexOf("\n//# sourceMappingURL=", StringComparison.Ordinal);
        return index < 0 ? code + "\n" + footer : code[..(index + 1)] + footer + code[(index + 1)..];
    }

    // PascalCase, but not SCREAMING_CASE constants such as API_URL.
    private static bool IsComponentName(string name) =>
        char.IsUpper(name[0]) && name.Any(char.IsLower);

    private static IEnumerable<(string Name, DeclarationKind Kind, string Text, string Init)> TopLevelDeclarations(string code)
    {
        var lines = code.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = DeclarationStart().Match(lines[i]);
            if (!match.Success)
            {
                continue;
            }

            // A declaration runs until the next line that starts a new top-level statement.
            var end = i + 1;
            while (end < lines.Length && (lines[end].Length == 0 || char.IsWhiteSpace(lines[end][0]) || lines[end][0] is '}' or ')' or ']'))
            {
                end++;
            }
            var text = string.Join('\n', lines[i..end]);

            var kind = match.Groups["function"].Success ? DeclarationKind.Function
                : match.Groups["class"].Success ? DeclarationKind.Other
                : ComponentInitializer().IsMatch(match.Groups["init"].Value) ? DeclarationKind.ComponentLike
                : DeclarationKind.Other;
            yield return (match.Groups["name"].Value, kind, text, match.Groups["init"].Value.Trim());
        }
    }

    private static List<string> ExportedLocals(string code, out bool hasStarExport)
    {
        hasStarExport = StarExport().IsMatch(code);
        var locals = new List<string>();
        foreach (var block in ExportBlock().Matches(code).Cast<Match>())
        {
            foreach (var entry in block.Groups["body"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                locals.Add(entry.Split(" as ")[0].Trim());
            }
        }
        // Anything esbuild left as an inline export (it normally does not) is not understood, so
        // treat it as a non-component and let the update propagate.
        if (InlineExport().IsMatch(code))
        {
            locals.Add("<inline>");
        }
        return locals;
    }

    private enum DeclarationKind { Function, ComponentLike, Other }

    [GeneratedRegex(@"^(?:(?<function>(?:async\s+)?function\*?\s+)(?<name>[A-Za-z_$][\w$]*)|(?<class>class\s+)(?<name>[A-Za-z_$][\w$]*)|(?:const|let|var)\s+(?<name>[A-Za-z_$][\w$]*)\s*=\s*(?<init>.*))")]
    private static partial Regex DeclarationStart();

    // Arrow functions, function expressions and the usual component wrappers.
    [GeneratedRegex(@"^(?:/\*\s*@__PURE__\s*\*/\s*)?(?:async\s+)?(?:\(|[A-Za-z_$][\w$]*\s*=>|function\b|(?:React\.)?(?:memo|forwardRef)\s*\()")]
    private static partial Regex ComponentInitializer();

    [GeneratedRegex(@"^([A-Za-z_$][\w$]*);?$")]
    private static partial Regex AliasInitializer();

    [GeneratedRegex(@"\bjsx(?:DEV|s)?\s*\(")]
    private static partial Regex JsxCall();

    [GeneratedRegex(@"\b(?<hook>use[A-Z0-9]\w*|use)\s*\(")]
    private static partial Regex HookCall();

    [GeneratedRegex(@"^export\s*\{(?<body>[^}]*)\}\s*;?", RegexOptions.Multiline)]
    private static partial Regex ExportBlock();

    [GeneratedRegex(@"^export\s*\*", RegexOptions.Multiline)]
    private static partial Regex StarExport();

    [GeneratedRegex(@"^export\s+(?:default|const|let|var|function|class|async)\b", RegexOptions.Multiline)]
    private static partial Regex InlineExport();
}

internal sealed record RefreshComponent(string Name, string HookSignature);

internal sealed record RefreshInfo(IReadOnlyList<RefreshComponent> Components, bool IsBoundary)
{
    public static readonly RefreshInfo None = new([], false);
}
