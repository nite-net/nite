using System.Text.RegularExpressions;

namespace Nite.Dev;

/// <summary>
/// Finds the names a CommonJS module exports, without running it. Browsers can only import named
/// bindings from ESM, so pre-bundled CJS packages (React, for one) get an ESM wrapper that
/// re-exports these names. The approach is the same as Node's <c>cjs-module-lexer</c>: recognize
/// the common assignment shapes and follow <c>module.exports = require("./x")</c> re-exports.
/// </summary>
internal static partial class CjsExportLexer
{
    private static readonly HashSet<string> Reserved =
    [
        "await", "break", "case", "catch", "class", "const", "continue", "debugger", "default", "delete", "do",
        "else", "enum", "export", "extends", "false", "finally", "for", "function", "if", "implements", "import",
        "in", "instanceof", "interface", "let", "new", "null", "package", "private", "protected", "public",
        "return", "static", "super", "switch", "this", "throw", "true", "try", "typeof", "var", "void", "while",
        "with", "yield", "__esModule",
    ];

    public static IReadOnlyCollection<string> GetExports(string path)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        Visit(Path.GetFullPath(path), names, new HashSet<string>(StringComparer.Ordinal));
        names.RemoveWhere(name => Reserved.Contains(name) || !IdentifierPattern().IsMatch(name));
        return names;
    }

    internal static IEnumerable<string> GetExportsFromSource(string source)
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);
        CollectNames(source, names);
        names.RemoveWhere(name => Reserved.Contains(name) || !IdentifierPattern().IsMatch(name));
        return names;
    }

    private static void Visit(string path, SortedSet<string> names, HashSet<string> visited)
    {
        if (!visited.Add(path) || !File.Exists(path))
        {
            return;
        }

        var source = File.ReadAllText(path);
        CollectNames(source, names);

        var directory = Path.GetDirectoryName(path)!;
        foreach (var match in ReexportPattern().Matches(source).Cast<Match>())
        {
            var specifier = match.Groups["spec"].Value;
            if (specifier.StartsWith('.') && ResolveRelative(directory, specifier) is { } target)
            {
                Visit(target, names, visited);
            }
        }
    }

    private static void CollectNames(string source, SortedSet<string> names)
    {
        foreach (var match in DotAssignmentPattern().Matches(source).Cast<Match>())
        {
            names.Add(match.Groups["name"].Value);
        }
        foreach (var match in BracketAssignmentPattern().Matches(source).Cast<Match>())
        {
            names.Add(match.Groups["name"].Value);
        }
        foreach (var match in DefinePropertyPattern().Matches(source).Cast<Match>())
        {
            names.Add(match.Groups["name"].Value);
        }
        foreach (var match in ObjectLiteralPattern().Matches(source).Cast<Match>())
        {
            foreach (var property in match.Groups["body"].Value.Split(','))
            {
                var key = property.Split(':', 2)[0].Trim().Trim('"', '\'');
                if (key.StartsWith("...", StringComparison.Ordinal))
                {
                    continue;
                }
                names.Add(key);
            }
        }
    }

    private static string? ResolveRelative(string directory, string specifier)
    {
        var basePath = Path.GetFullPath(Path.Combine(directory, specifier));
        string[] candidates = [basePath, basePath + ".js", basePath + ".cjs", Path.Combine(basePath, "index.js")];
        return candidates.FirstOrDefault(File.Exists);
    }

    [GeneratedRegex(@"(?<![\w$.])(?:module\.)?exports\.(?<name>[A-Za-z_$][\w$]*)\s*=(?!=)")]
    private static partial Regex DotAssignmentPattern();

    [GeneratedRegex(@"(?<![\w$.])(?:module\.)?exports\[\s*(?<q>[""'])(?<name>[^""']+)\k<q>\s*\]\s*=(?!=)")]
    private static partial Regex BracketAssignmentPattern();

    [GeneratedRegex(@"Object\.defineProperty\(\s*(?:module\.)?exports\s*,\s*(?<q>[""'])(?<name>[^""']+)\k<q>")]
    private static partial Regex DefinePropertyPattern();

    // module.exports = { a, b: x, "c": y } with no nested braces, the shape bundlers emit.
    [GeneratedRegex(@"module\.exports\s*=\s*\{(?<body>[^{}]*)\}")]
    private static partial Regex ObjectLiteralPattern();

    [GeneratedRegex(@"(?:module\.exports\s*=\s*require|__exportStar\(\s*require|__export\(\s*require)\(\s*(?<q>[""'])(?<spec>[^""']+)\k<q>\s*\)")]
    private static partial Regex ReexportPattern();

    [GeneratedRegex(@"^[A-Za-z_$][\w$]*$")]
    private static partial Regex IdentifierPattern();
}
