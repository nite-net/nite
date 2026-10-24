using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nite;

/// <summary>
/// <c>compilerOptions.paths</c> (and <c>baseUrl</c>) from the client's tsconfig, so imports such as
/// <c>@/components/Button</c> resolve in the dev server the way TypeScript and esbuild resolve them.
/// Follows <c>extends</c> and Vite's <c>references</c> layout (paths usually live in
/// <c>tsconfig.app.json</c>).
/// </summary>
internal sealed class TsconfigPaths
{
    private readonly List<(string Pattern, IReadOnlyList<string> Targets, string BaseDirectory)> _paths = [];
    private string? _baseUrl;

    /// <summary>The config file that declared the paths, which a production build extends.</summary>
    public string? SourceFile { get; private set; }

    public bool IsEmpty => _paths.Count == 0 && _baseUrl is null;

    public static TsconfigPaths Load(string clientRoot)
    {
        var result = new TsconfigPaths();
        var root = Path.Combine(clientRoot, "tsconfig.json");
        if (File.Exists(root))
        {
            result.Visit(root, new HashSet<string>(StringComparer.Ordinal));
        }
        return result;
    }

    /// <summary>The file an aliased import points at, or null when it is not an alias (a package, then).</summary>
    public string? Resolve(string specifier)
    {
        foreach (var (pattern, targets, baseDirectory) in _paths)
        {
            var star = pattern.IndexOf('*');
            string? captured = null;
            if (star < 0 && specifier == pattern)
            {
                captured = "";
            }
            else if (star >= 0 && specifier.Length >= pattern.Length - 1
                && specifier.StartsWith(pattern[..star], StringComparison.Ordinal)
                && specifier.EndsWith(pattern[(star + 1)..], StringComparison.Ordinal))
            {
                captured = specifier[star..(specifier.Length - (pattern.Length - star - 1))];
            }
            if (captured is null)
            {
                continue;
            }

            foreach (var target in targets)
            {
                var candidate = Path.GetFullPath(Path.Combine(baseDirectory, target.Replace("*", captured)));
                if (Dev.NiteDevServer.ResolveModuleFile(candidate) is { } file)
                {
                    return file;
                }
            }
        }

        // TypeScript also resolves non-relative imports against baseUrl, after paths.
        return _baseUrl is not null && Dev.NiteDevServer.ResolveModuleFile(Path.GetFullPath(Path.Combine(_baseUrl, specifier))) is { } fromBase
            ? fromBase
            : null;
    }

    private void Visit(string file, HashSet<string> visited)
    {
        if (!visited.Add(Path.GetFullPath(file)) || !File.Exists(file))
        {
            return;
        }

        JsonNode? json;
        try
        {
            json = JsonNode.Parse(File.ReadAllText(file), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException)
        {
            return; // an invalid tsconfig is the editor's problem to report, not ours
        }

        var directory = Path.GetDirectoryName(file)!;
        var options = json?["compilerOptions"];
        var baseUrl = options?["baseUrl"]?.GetValue<string>() is { } url ? Path.GetFullPath(Path.Combine(directory, url)) : null;
        if (options?["paths"] is JsonObject paths && _paths.Count == 0)
        {
            SourceFile = file;
            foreach (var (pattern, value) in paths)
            {
                var targets = value is JsonArray array ? array.Select(t => t?.GetValue<string>()).OfType<string>().ToList() : [];
                _paths.Add((pattern, targets, baseUrl ?? directory));
            }
        }
        _baseUrl ??= baseUrl;

        if (json?["references"] is JsonArray references)
        {
            foreach (var reference in references)
            {
                if (reference?["path"]?.GetValue<string>() is { } path)
                {
                    var target = Path.Combine(directory, path);
                    Visit(Directory.Exists(target) ? Path.Combine(target, "tsconfig.json") : target, visited);
                }
            }
        }

        var extends = json?["extends"] switch
        {
            JsonValue single => [single.GetValue<string>()],
            JsonArray many => many.Select(e => e?.GetValue<string>()).OfType<string>().ToList(),
            _ => new List<string>(),
        };
        foreach (var parent in extends.Where(e => e.StartsWith('.')))
        {
            var target = Path.Combine(directory, parent);
            Visit(target.EndsWith(".json", StringComparison.Ordinal) ? target : target + ".json", visited);
        }
    }
}
